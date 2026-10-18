using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Moq;
using OrderDeck.App.Services;
using OrderDeck.App.Services.Sync;
using OrderDeck.App.ViewModels;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Sales;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Core.Time;
using OrderDeck.Licensing.Api.Models;
using OrderDeck.Tests.Fakes;
using OrderDeck.Tests.TestHelpers;

namespace OrderDeck.Tests.Sync;

/// <summary>
/// Ödeme isteğinin mod On sarmalı (spec 2026-10-13 §6.4 istemci adımları, §6.5-2, §10.3 "ön gönderim: bekleyen komut
/// isteği engeller, ilgisiz takılı kuyruk engellemez"; W3-07), betikli sahte sunucuyla (<see cref="FakeSalesServer"/>)
/// ve AppHost gibi kurulmuş GERÇEK servislerle (<see cref="SalesPc"/>): ödeme servisi, kira sarmalı, bekleyen komut ve
/// ödeme kaydı göndericileri, müşteri ve oturum/etiket gönderimi aynı örnekler. İsteklerin sırası sunucunun
/// günlüğünden (<see cref="FakeSalesServer.RequestLog"/>) okunur.
///
/// <para>Mod Off (bugün her lisans) hiçbir şeyi değiştirmez: ön gönderim yok, kira ucu yok, bugünkü yerel yol ve W3-06'nın
/// kirasız kaydı.</para>
/// </summary>
public sealed class PaymentLeaseTests : IDisposable
{
    private readonly FakeSalesServer _server = new() { DeriveLiveSessions = true, MinClientVersion = "0.9.11" };
    private readonly List<IDisposable> _owned = new();
    private readonly List<string> _files = new();

    public void Dispose()
    {
        _server.Dispose();
        foreach (var d in _owned) d.Dispose();
        SqliteConnection.ClearAllPools();
        foreach (var p in _files)
            foreach (var f in new[] { p, p + ".bak", p + ".tmp" })
                try { File.Delete(f); } catch { /* en iyi çaba */ }
    }

    // ── kurulum ───────────────────────────────────────────────────────────

    /// <summary>Bir bilgisayar; mod Off'ta bir tur (hello + state). <paramref name="modeOn"/> ise sunucu modu açar (lisans
    /// geneli kargo: sürüm 3, eşik 1000, ücret 50) ve bilgisayar açılış turunu bitirir.</summary>
    private async Task<SalesPc> PcAsync(bool modeOn = true)
    {
        var pc = SalesPc.New(_server, "PC-A", _owned, _files);
        (await pc.TourAsync()).Should().Be(SalesPullOutcome.ModeOff);
        if (!modeOn) return pc;
        _server.Mode = 1;
        _server.ModeChangedAt = DateTimeOffset.UtcNow;
        _server.Shipping = new FakeSalesServer.ShippingBody(Version: 3, FreeShippingThreshold: 1000m, Fee: 50m);
        (await pc.SettleAsync()).Should().Be(SalesPullOutcome.CaughtUp, "mod On açıldı");
        pc.State.GetMode().Should().Be(1);
        return pc;
    }

    private sealed record Buyer(Customer Customer, IReadOnlyList<Label> Labels);

    /// <summary>Canlı yayında alıcı: etiketleri basılmış, telefonu kayıtlı (GUID'li müşteri, bakiye akışına girer).</summary>
    private static async Task<Buyer> BuyerAsync(SalesPc pc, string session, string username, params decimal[] prices)
    {
        var labels = prices.Select((p, i) => pc.Sell(session, username, $"K{i + 1}", p)).ToList();
        await pc.PrintAsync(labels.Select(l => l.Id).ToArray());
        return new Buyer(WithPhone(pc, username), labels);
    }

    private static Customer WithPhone(SalesPc pc, string username)
    {
        var id = pc.Customers.FindByPlatformAndUsername("tiktok", username)!.Id;
        pc.Customers.UpdatePhone(id, TestPhone.NewE164()).Should().Be(1);
        return pc.Customers.GetById(id)!;
    }

    private sealed record Residual(string Session, Customer Buyer, Label Printed, Label Pending);

    /// <summary>
    /// Bu bilgisayarın devredilmiş yayını: alıcının basılmış etiketi (120) ve basılmamış kalıntı etiketi (80); kalıntının
    /// ilk baskısı çevrimdışı basıldı → bekleyen <c>mark-printed</c> (§5.5), etiketin örtüsü. Bağlantı geri gelir.
    /// </summary>
    private static async Task<Residual> HandedOverWithPendingPrintAsync(SalesPc pc, string username)
    {
        var session = pc.StartStream();
        var printed = pc.Sell(session, username, "A1", 120m);
        var residual = pc.Sell(session, username, "B2", 80m);
        await pc.PrintAsync(printed.Id);
        pc.EndStream(session);
        await pc.BackgroundRoundAsync();
        await pc.BackgroundRoundAsync();
        pc.SessionOf(session)!.Authority.Should().Be("server", "yayın devredildi");
        pc.ResidualOffer().Should().Contain(residual.Id);

        pc.Offline = true;
        await pc.SettleAsync();
        (await pc.PrintAsync(residual.Id)).Pending.Should().Equal(residual.Id);
        pc.Offline = false;
        pc.Row(residual.Id)!.PrintedAt.Should().BeNull("bekleyen baskı onaylanmadı");
        return new Residual(session, WithPhone(pc, username), printed, residual);
    }

    /// <summary>Ödeme kartının/raporunun tutar tanımı: kişinin bütün üyelerinin kapsamdaki basılmış etiketleri.</summary>
    private static Task<PaymentRequestOutcome> RequestAsync(SalesPc pc, Customer customer, string scope,
        IReadOnlySet<string>? tokens = null) =>
        pc.Payments.RequestPaymentAsync(customer,
            p => pc.LabelRepo.GetPrintedTotal(p.MemberIds.ToList(), PaymentHistoryRecords.SessionIdOf(scope)).Total,
            DateTime.Now, scope, tokens);

    private static string Scope(string sessionId) => $"session:{sessionId}";

    private static void UseCloudApi(SalesPc pc) => pc.Store.Update(s => s.Payment.UseCloudApi = true);

    /// <summary>Sunucu günlüğünün <paramref name="mark"/>'tan sonraki kısmı, okunur adlarla; lisans çözümü atlanır, art
    /// arda aynı ad bire iner.</summary>
    private IReadOnlyList<string> Log(int mark)
    {
        var result = new List<string>();
        foreach (var entry in _server.RequestLog.Skip(mark))
        {
            var name = Name(entry);
            if (name is null) continue;
            if (result.Count == 0 || result[^1] != name) result.Add(name);
        }
        return result;
    }

    private static string? Name(string entry) => entry switch
    {
        _ when entry.EndsWith("/me/licenses", StringComparison.Ordinal) => null,
        _ when entry.EndsWith("/sales/commands", StringComparison.Ordinal) => "komut",
        _ when Regex.IsMatch(entry, @"/payment-requests/[0-9a-fA-F-]{36}/finish$") => "finish",
        _ when entry.EndsWith("/payment-requests/begin", StringComparison.Ordinal) => "begin",
        _ when entry.EndsWith("/wpf-customers/sync", StringComparison.Ordinal) => "müşteri",
        _ when entry.EndsWith("/sessions/sync", StringComparison.Ordinal)
               || entry.EndsWith("/orders/sync", StringComparison.Ordinal) => "satış",
        _ when entry.Contains("/customer-balance/", StringComparison.Ordinal) => "bakiye",
        _ when entry.EndsWith("/whatsapp/send", StringComparison.Ordinal) => "mesaj",
        _ => entry,
    };

    private static decimal JobTotal(SalesPc pc, Customer customer, string scope) =>
        pc.Jobs.GetPersonScopeJobs([customer.Id], scope).Jobs.Should().ContainSingle().Subject.ProductTotal;

    // ── mod Off ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Mod_Offta_on_gonderim_ve_kira_yok_bugunku_yol_ve_kirasiz_kayit()
    {
        var pc = await PcAsync(modeOn: false);
        var session = pc.StartStream();
        var buyer = await BuyerAsync(pc, session, "alici_off", 150m);
        pc.Sell(session, "alici_baska", "Z9", 40m);                     // gönderilmemiş satış: ön gönderim olsa giderdi
        var mark = _server.RequestLog.Count;

        (await RequestAsync(pc, buyer.Customer, Scope(session))).Result.Should().Be(PaymentRequestResult.Opened);

        Log(mark).Should().OnlyContain(e => e == "bakiye",
            "mod Off: ön gönderim, begin ve finish yok; yalnız bugünkü bakiye istekleri");
        pc.Launcher.LaunchedUrls.Should().ContainSingle();
        pc.PendingPaymentRecords.List().Should().ContainSingle()
            .Which.Kind.Should().Be(PendingPaymentRecordKinds.Record, "W3-06: mod Off'ta kirasız kayıt");
    }

    // ── ön gönderim ───────────────────────────────────────────────────────

    [Fact]
    public async Task Mutlu_yol_sirasi_bekleyen_komut_finish_musteri_satis_begin_bakiye_mesaj_finish_delivered()
    {
        var pc = await PcAsync();
        UseCloudApi(pc);
        _server.BalancePreview = 50m;
        await HandedOverWithPendingPrintAsync(pc, "alici_kalinti");     // başka kişinin bekleyen komutu
        var stale = Guid.NewGuid();
        pc.PaymentRecords.EnqueueFinish(new PendingPaymentFinish(stale, PaymentRequestStates.Abandoned, 0m,
            null, false)).Should().BeTrue();
        var live = pc.StartStream();
        var buyer = await BuyerAsync(pc, live, "alici_canli", 200m);    // müşteri ve etiket kuyrukta
        var mark = _server.RequestLog.Count;

        (await RequestAsync(pc, buyer.Customer, Scope(live))).Result.Should().Be(PaymentRequestResult.Sent);

        Log(mark).Should().Equal(new[] { "komut", "finish", "müşteri", "satış", "begin", "bakiye", "mesaj", "finish" },
            "§6.4: ön gönderim (bekleyen komut → bekleyen finish → müşteri → oturum ve etiket), kira bakiyeden önce, "
          + "teslimden sonra finish");
        var begin = _server.Calls(FakeSalesServer.PaymentRequestsBegin).Should().ContainSingle().Subject.Json;
        var finishes = _server.Calls(FakeSalesServer.PaymentRequestsFinish);
        finishes.Select(f => f.RecordId).Should().Equal(stale, begin.GetProperty("recordId").GetGuid());
        var finish = finishes[1].Json;
        finish.GetProperty("outcome").GetString().Should().Be("delivered");
        finish.GetProperty("netAmount").GetDecimal().Should().Be(200m + 50m - 50m, "kargo dahil toplam − bakiye");
        finish.GetProperty("appliedAmount").GetDecimal().Should().Be(50m);
        finish.GetProperty("fullyFromBalance").GetBoolean().Should().BeFalse();
        finish.GetProperty("replicaId").GetGuid().Should().Be(pc.Replica);
        finish.GetProperty("seenMode").GetInt32().Should().Be(1);
        pc.PendingCount.Should().Be(0);
        pc.PendingPaymentRecords.Count().Should().Be(0, "finish'ler gitti; mod On'da kirasız kayıt yok");
        _server.Faults.Should().BeEmpty();
    }

    [Fact]
    public async Task Begin_govdesi_butun_uyeler_GUIDsiz_ham_Id_dahil_siparisler_katki_kumesi_surum_onbellekten()
    {
        var pc = await PcAsync();
        var live = pc.StartStream();
        var buyer = await BuyerAsync(pc, live, "alici_govde", 150m, 70m);
        pc.Sell(live, "alici_govde", "U1", 40m);                         // basılmamış: katkı yok
        var cancelled = pc.Sell(live, "alici_govde", "C1", 30m);
        await pc.PrintAsync(cancelled.Id);
        pc.Labels.Cancel([cancelled.Id], "musteri");                      // iptal: katkı yok
        var guidless = new Customer($"instagram:ornek_g_{Guid.NewGuid():N}", "instagram", "ornek_g", "Örnek", null,
            1, 1, false, null, null, 0, 0m, null, null, null);
        pc.Customers.Insert(guidless);
        pc.Customers.MergeIntoGroup([buyer.Customer.Id, guidless.Id]);
        var guidlessLabel = InsertLabel(pc, live, guidless.Id, "instagram", "ornek_g", 25m, printed: true);
        InsertLabel(pc, live, buyer.Customer.Id, "tiktok", "alici_govde", 60m, printed: true, tentative: true);

        (await RequestAsync(pc, buyer.Customer, Scope(live))).Result.Should().Be(PaymentRequestResult.Opened);

        var body = _server.Calls(FakeSalesServer.PaymentRequestsBegin).Should().ContainSingle().Subject.Json;
        body.GetProperty("ownerId").GetString().Should().Be(buyer.Customer.Id);
        body.GetProperty("memberIds").EnumerateArray().Select(m => m.GetString()).Should()
            .BeEquivalentTo([buyer.Customer.Id, guidless.Id], "kişinin BÜTÜN üyeleri, GUID'siz ham Id dahil (§6.4-3)");
        body.GetProperty("scopeKey").GetString().Should().Be(Scope(live));
        body.GetProperty("orderIds").EnumerateArray().Select(o => o.GetGuid()).Should().BeEquivalentTo(
            buyer.Labels.Select(l => Guid.ParseExact(l.Id, "N")).Append(Guid.ParseExact(guidlessLabel, "N")),
            "katkı kümesi: basılmış, iptal edilmemiş, geçici yedek değil");
        body.GetProperty("productTotal").GetDecimal().Should().Be(150m + 70m + 25m);
        body.GetProperty("totalBeforeBalance").GetDecimal().Should().Be(150m + 70m + 25m + 50m,
            "kargo lisans geneli ayardan (eşik 1000, ücret 50)");
        body.GetProperty("recipientPays").GetBoolean().Should().BeFalse();
        body.GetProperty("shippingSettingsVersion").GetInt32().Should().Be(3, "sürüm sales/state önbelleğinden");
        body.GetProperty("confirmedPriorIds").GetArrayLength().Should().Be(0);
        body.TryGetProperty("confirmedPriorsDigest", out _).Should().BeFalse("onay alanları boş (W3-09 doldurur)");
        body.GetProperty("replicaId").GetGuid().Should().Be(pc.Replica);
        body.GetProperty("protocol").GetInt32().Should().Be(2);
        body.GetProperty("seenMode").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task WhatsApp_acilamazsa_finish_abandoned_ve_deneme_geri_alinir()
    {
        var pc = await PcAsync();
        var live = pc.StartStream();
        var buyer = await BuyerAsync(pc, live, "alici_acilmaz", 120m);
        pc.Launcher.ThrowOnLaunch = new InvalidOperationException("wa.me açılamadı");
        var scope = Scope(live);

        (await RequestAsync(pc, buyer.Customer, scope)).Result.Should().Be(PaymentRequestResult.LaunchFailed);

        FinishOutcomes().Should().Equal("abandoned");
        var job = pc.Jobs.GetPersonScopeJobs([buyer.Customer.Id], scope).Jobs.Should().ContainSingle().Subject;
        job.HasUnconfirmedSend.Should().BeFalse("AbandonSend: mesaj gitmedi, deneme geri alındı");
        job.Deliveries.Should().Be(0);
    }

    [Fact]
    public async Task Cloud_APIden_sonra_wa_me_acilamazsa_finish_unconfirmed()
    {
        var pc = await PcAsync();
        UseCloudApi(pc);
        var live = pc.StartStream();
        var buyer = await BuyerAsync(pc, live, "alici_belirsiz", 120m);
        _server.EnqueueJson(FakeSalesServer.WhatsAppSend, 200,
            """{"ok":false,"errorCode":"server_error","errorMessage":"fake","messageId":null}""");
        pc.Launcher.ThrowOnLaunch = new InvalidOperationException("wa.me açılamadı");

        (await RequestAsync(pc, buyer.Customer, Scope(live))).Result.Should().Be(PaymentRequestResult.LaunchFailed);

        FinishOutcomes().Should().Equal(new[] { "unconfirmed" },
            "Cloud API mesajı göndermiş olabilir (§6.4 'Akışın sonu')");
    }

    [Fact]
    public async Task Yerel_tekrar_gonderim_sorusu_donmeden_once_finish_abandoned()
    {
        var pc = await PcAsync();
        var live = pc.StartStream();
        var buyer = await BuyerAsync(pc, live, "alici_soru", 120m);
        var scope = Scope(live);
        (await RequestAsync(pc, buyer.Customer, scope)).Result.Should().Be(PaymentRequestResult.Opened);

        var second = await RequestAsync(pc, buyer.Customer, scope);

        second.Result.Should().Be(PaymentRequestResult.AlreadyRequested, "sahip kararı 2: yerel soru bugünkü gibi");
        var begins = _server.Calls(FakeSalesServer.PaymentRequestsBegin).Select(b => b.Json.GetProperty("recordId").GetGuid())
            .ToList();
        begins.Should().HaveCount(2).And.OnlyHaveUniqueItems("akış başına yeni recordId");
        var finishes = _server.Calls(FakeSalesServer.PaymentRequestsFinish);
        finishes.Select(f => f.RecordId!.Value).Should().Equal(begins);
        finishes.Select(f => f.Json.GetProperty("outcome").GetString()).Should().Equal("delivered", "abandoned");
        pc.Launcher.LaunchedUrls.Should().ContainSingle();
    }

    [Fact]
    public async Task Finish_gonderilemezse_kuyrukta_kalir_sonraki_gonderimde_gider()
    {
        var pc = await PcAsync();
        var live = pc.StartStream();
        var buyer = await BuyerAsync(pc, live, "alici_kuyruk", 120m);
        _server.EnqueueStatus(FakeSalesServer.PaymentRequestsFinish, 503);

        (await RequestAsync(pc, buyer.Customer, Scope(live))).Result.Should().Be(PaymentRequestResult.Opened);

        var row = pc.PendingPaymentRecords.List().Should().ContainSingle().Subject;
        row.Kind.Should().Be(PendingPaymentRecordKinds.Finish);
        row.Attempts.Should().Be(1);
        var recordId = _server.Calls(FakeSalesServer.PaymentRequestsBegin).Single().Json.GetProperty("recordId").GetGuid();
        row.RecordId.Should().Be(recordId.ToString("N"));

        (await pc.PaymentRecords.FlushAsync(CancellationToken.None)).Sent.Should().Be(1);

        pc.PendingPaymentRecords.Count().Should().Be(0);
        _server.Calls(FakeSalesServer.PaymentRequestsFinish).Select(f => f.RecordId).Should().Equal(recordId, recordId);
        FinishOutcomes().Should().Equal("delivered", "delivered");
    }

    [Fact]
    public async Task Begin_cevrimdisi_GUIDli_uyede_mesaj_yok_ayni_recordId_ile_bir_kez_yinelenir()
    {
        var pc = await PcAsync();
        UseCloudApi(pc);
        var live = pc.StartStream();
        var buyer = await BuyerAsync(pc, live, "alici_cevrimdisi", 120m);
        _server.EnqueueTimeout(FakeSalesServer.PaymentRequestsBegin);
        _server.EnqueueTimeout(FakeSalesServer.PaymentRequestsBegin);

        (await RequestAsync(pc, buyer.Customer, Scope(live))).Result.Should().Be(PaymentRequestResult.BalanceUncertain,
            "M9: kira alınamazsa GUID'li kişide mesaj gitmez (bugünkü belirsizlik sunumu)");

        var begins = _server.Calls(FakeSalesServer.PaymentRequestsBegin);
        begins.Should().HaveCount(2);
        begins[1].RawJson.Should().Be(begins[0].RawJson, "yanıtı kaybolan isteğin akış içi yinelemesi aynı Id ve gövdeyle");
        _server.Calls(FakeSalesServer.WhatsAppSend).Should().BeEmpty();
        _server.Calls(FakeSalesServer.BalancePreviewEndpoint).Should().BeEmpty("bakiye adımına geçilmedi");
        pc.Launcher.LaunchedUrls.Should().BeEmpty();
        var finish = _server.Calls(FakeSalesServer.PaymentRequestsFinish).Should().ContainSingle(
            "kira sunucuda yazılmış olabilir: abandoned ile kapanır, sonraki istekte sahte 'gitmiş olabilir' çıkmaz").Subject;
        finish.RecordId.Should().Be(begins[0].Json.GetProperty("recordId").GetGuid());
        finish.Json.GetProperty("outcome").GetString().Should().Be("abandoned");
    }

    private IReadOnlyList<string?> FinishOutcomes() =>
        _server.Calls(FakeSalesServer.PaymentRequestsFinish).Select(f => f.Json.GetProperty("outcome").GetString())
            .ToList();

    /// <summary>Etiket satırı doğrudan (GUID'siz müşteri ya da geçici yedek kurulumu için); Id "N" biçimli.</summary>
    private static string InsertLabel(SalesPc pc, string sessionId, string customerId, string platform, string username,
        decimal price, bool printed, bool tentative = false)
    {
        var id = Guid.NewGuid().ToString("N");
        pc.Exec(
            @"INSERT INTO Label (Id, SessionId, CustomerId, Platform, Username, DisplayName, MessageText, Code, Price,
                                 AddedAt, PrintedAt, IsTentativeBackup)
              VALUES (@id, @sessionId, @customerId, @platform, @username, 'Örnek', 'aldım', 'G1', @price, 1500,
                      @printedAt, @tentative)",
            new { id, sessionId, customerId, platform, username, price, printedAt = printed ? 2000L : (long?)null,
                  tentative = tentative ? 1 : 0 });
        return id;
    }

    [Fact]
    public async Task On_gonderim_kira_alinmadan_kosar_ayni_kisinin_bekleyen_baskisi_tutara_girer()
    {
        // U13/U17: bekleyen komutun yanıt satırı kişinin kendi kirası altında yazılamazdı (ApplyItem Busy döner, satır
        // akışa kalır) ve kâğıdı basılmış ürün istenmezdi.
        var pc = await PcAsync();
        var residual = await HandedOverWithPendingPrintAsync(pc, "alici_ayni");
        var scope = Scope(residual.Session);

        (await RequestAsync(pc, residual.Buyer, scope)).Result.Should().Be(PaymentRequestResult.Opened);

        pc.PendingCount.Should().Be(0);
        pc.Row(residual.Pending.Id)!.PrintedAt.Should().NotBeNull("ön gönderimin yanıtı yerel satıra yazıldı");
        JobTotal(pc, residual.Buyer, scope).Should().Be(120m + 80m + 50m,
            "tutar ön gönderimden sonra kapsamdan yeniden okundu: bekleyen baskı da girdi (kargo lisans geneli ayardan)");
    }

    [Fact]
    public async Task Kisinin_etiketini_orten_bekleyen_komut_gonderilemezse_istek_engellenir_mesaj_yok()
    {
        var pc = await PcAsync();
        UseCloudApi(pc);
        var residual = await HandedOverWithPendingPrintAsync(pc, "alici_ortu");
        _server.EnqueueStatus(FakeSalesServer.Commands, 503);
        var mark = _server.RequestLog.Count;

        var outcome = await RequestAsync(pc, residual.Buyer, "cumulative");

        outcome.Result.Should().Be(PaymentRequestResult.Blocked);
        outcome.Message.Should().Be("Bekleyen baskı ya da iptal onayı gönderilemedi; bağlantı gelince tekrar dene.");
        Log(mark).Should().NotContain(e => e == "bakiye" || e == "mesaj" || e == "begin",
            "engel kişi okunduktan sonra, bakiyeden ve kiradan önce");
        pc.Launcher.LaunchedUrls.Should().BeEmpty();
        pc.PendingCount.Should().Be(1, "komut kuyrukta kaldı");
    }

    [Fact]
    public async Task Baska_kisinin_bekleyen_komutu_ve_takili_satis_gonderimi_istegi_engellemez()
    {
        var pc = await PcAsync();
        UseCloudApi(pc);
        await HandedOverWithPendingPrintAsync(pc, "alici_ilgisiz");
        var live = pc.StartStream();
        var buyer = await BuyerAsync(pc, live, "alici_hedef", 300m);
        _server.EnqueueStatus(FakeSalesServer.Commands, 503);
        _server.EnqueueStatus(FakeSalesServer.OrdersSync, 503);

        (await RequestAsync(pc, buyer.Customer, Scope(live))).Result.Should().Be(PaymentRequestResult.Sent,
            "ilgisiz takılı kuyruk tek başına engel değil (§6.4-1); karar sunucu toplamının");

        pc.PendingCount.Should().Be(1, "başka kişinin komutu kuyrukta kaldı");
        _server.Calls(FakeSalesServer.WhatsAppSend).Should().ContainSingle();
    }

    [Fact]
    public async Task On_gonderimin_zaman_asimi_engel_degil()
    {
        var pc = await PcAsync();
        UseCloudApi(pc);
        pc.Lease.PrePushTimeout = TimeSpan.FromMilliseconds(300);
        var live = pc.StartStream();
        var buyer = await BuyerAsync(pc, live, "alici_sure", 90m);
        var hold = _server.Hold(FakeSalesServer.SessionsSync);
        try
        {
            (await RequestAsync(pc, buyer.Customer, Scope(live))).Result.Should().Be(PaymentRequestResult.Sent);
            hold.Arrived.IsCompleted.Should().BeTrue("oturum gönderimi başladı ve takıldı");
        }
        finally { hold.Release(); }
    }

    // ── satır içi gösterim ────────────────────────────────────────────────

    [Fact]
    public async Task Musteri_kartinda_engel_satir_ici_seritte_acilir_pencere_yok()
    {
        var pc = await PcAsync();
        var residual = await HandedOverWithPendingPrintAsync(pc, "alici_kart");
        _server.EnqueueStatus(FakeSalesServer.Commands, 503);
        var dialogs = new FakeDialogService();
        var vm = new CustomerSearchViewModel(pc.Customers,
            new CustomerService(pc.Customers, pc.Sessions, pc.LabelRepo, Mock.Of<IClock>(c => c.UnixNow() == 1L)),
            pc.Sessions, pc.LabelRepo, pc.Payments, dialogs);
        var card = new CustomerSearchViewModel.CustomerCard
        {
            Primary = residual.Buyer, Members = [residual.Buyer],
        };

        await vm.OpenWhatsAppCommand.ExecuteAsync(card);

        vm.PaymentProblem.Should().Be(PaymentLeaseMessages.PendingCommandBlocked);
        dialogs.ErrorsShown.Should().BeEmpty();
        dialogs.InfosShown.Should().BeEmpty();
        dialogs.Shown.Should().BeEmpty("engel açılır pencere açmaz (#508)");

        vm.DismissPaymentProblemCommand.Execute(null);
        vm.PaymentProblem.Should().BeNull();
    }
}
