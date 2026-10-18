using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Moq;
using OrderDeck.App.Services;
using OrderDeck.App.Services.Sync;
using OrderDeck.App.ViewModels;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Payments;
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

    [Fact]
    public async Task Mod_Offta_giren_istek_gonderim_surerken_acilis_kipi_1_yapsa_da_kirasiz_kaydi_yazilir()
    {
        // Lisansta enable --apply; bu bilgisayarın açılış turu, mod Off yolundan girmiş bir istek Cloud API'de beklerken
        // biter. Kayıt kararı girişteki kipe göre: isteğin ne kirası ne kaydı olmasaydı öbür bilgisayarın begin'i (e)'de
        // öncül bulamaz, "zaten istendi" sorulmadan ikinci istek giderdi.
        var pc = await PcAsync(modeOn: false);
        UseCloudApi(pc);
        var session = pc.StartStream();
        var buyer = await BuyerAsync(pc, session, "alici_gecis", 150m);
        var hold = _server.Hold(FakeSalesServer.WhatsAppSend);
        Task<PaymentRequestOutcome> request;
        try
        {
            request = RequestAsync(pc, buyer.Customer, Scope(session));
            await hold.Arrived.WaitAsync(TimeSpan.FromSeconds(30));
            _server.Mode = 1;                                               // sales-sync enable --apply
            _server.ModeChangedAt = DateTimeOffset.UtcNow;
            _server.Shipping = new FakeSalesServer.ShippingBody(Version: 3, FreeShippingThreshold: 1000m, Fee: 50m);
            await pc.TourAsync();
            pc.State.GetMode().Should().Be(1, "ön koşul: açılış istek sürerken bitti");
        }
        finally { hold.Release(); }

        (await request).Result.Should().Be(PaymentRequestResult.Sent);

        Begins().Should().BeEmpty("istek girişte mod Off yolunu seçti: kira yok");
        var job = pc.Jobs.GetPersonScopeJobs([buyer.Customer.Id], Scope(session)).Jobs.Should().ContainSingle().Subject;
        pc.PendingPaymentRecords.List().Where(r => r.Kind == PendingPaymentRecordKinds.Record)
            .Select(PendingPaymentRecordCodec.DecodeRecord).Should().ContainSingle("teslimin kirasız kaydı kuyrukta")
            .Which.RecordId.Should().Be(PaymentRecordIds.Delivered(job.Id, 1));
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
    public async Task Cloud_API_isleniyor_SendPending_finish_unconfirmed()
    {
        // Sonuç bilinmiyor (in_progress): öbür bilgisayarda "istendi" değil "istenmiş olabilir" sorulmalı (§6.4 "Akışın sonu").
        var pc = await PcAsync();
        UseCloudApi(pc);
        var live = pc.StartStream();
        var buyer = await BuyerAsync(pc, live, "alici_isleniyor", 120m);
        _server.EnqueueJson(FakeSalesServer.WhatsAppSend, 200,
            """{"ok":false,"errorCode":"in_progress","errorMessage":"işleniyor","messageId":null}""");

        (await RequestAsync(pc, buyer.Customer, Scope(live))).Result.Should().Be(PaymentRequestResult.SendPending);

        var finish = _server.Calls(FakeSalesServer.PaymentRequestsFinish).Should().ContainSingle().Subject.Json;
        finish.GetProperty("outcome").GetString().Should().Be("unconfirmed");
        finish.GetProperty("netAmount").GetDecimal().Should().Be(120m + 50m);
        pc.Launcher.LaunchedUrls.Should().BeEmpty("bugünkü davranış: wa.me açılmaz");
    }

    [Fact]
    public async Task Tamami_bakiyeden_odenen_istegin_finishi_fullyFromBalance_ve_dusumu_tasir()
    {
        // Uzak "ödendi" rozeti ve BalancePaidLabels bu bayrağa dayanır (§6.4 "Teslim kayıtlarının okunması").
        var pc = await PcAsync();
        UseCloudApi(pc);
        _server.BalancePreview = 500m;
        var live = pc.StartStream();
        var buyer = await BuyerAsync(pc, live, "alici_bakiyeden", 120m);

        (await RequestAsync(pc, buyer.Customer, Scope(live))).Result.Should().Be(PaymentRequestResult.Sent);

        var finish = _server.Calls(FakeSalesServer.PaymentRequestsFinish).Should().ContainSingle().Subject.Json;
        finish.GetProperty("outcome").GetString().Should().Be("delivered");
        finish.GetProperty("netAmount").GetDecimal().Should().Be(0m);
        finish.GetProperty("appliedAmount").GetDecimal().Should().Be(120m + 50m);
        finish.GetProperty("fullyFromBalance").GetBoolean().Should().BeTrue();
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

    [Theory]
    [InlineData("unconfirmed", "abandoned")]   // süresi dolmuş kendi kirası: finish(abandoned) onu kapatır
    [InlineData("abandoned", null)]            // son hâl: kapatılacak bir şey yok
    public async Task Begin_yinelemesi_kendi_kirasini_kapanmis_dondururse_mesaj_yok_suresi_dolmussa_abandoned_kapanir(
        string replayedState, string? expectedFinish)
    {
        // İlk begin'in yanıtı kayboldu; aynı Id'li yineleme kira süresi dolduktan sonra yanıt aldı ve sunucu kaydı
        // "unconfirmed" döndürdü. Mesaj hiç gitmedi: kayıt öyle kalsaydı öbür bilgisayarların her sorusunda "istenmiş
        // olabilir (sonucu bilinmiyor)" diye görünürdü. Sunucunun finish'i Unconfirmed kaydı abandoned'a çevirir.
        var pc = await PcAsync();
        var live = pc.StartStream();
        var buyer = await BuyerAsync(pc, live, $"alici_yineleme_{replayedState}", 120m);
        _server.EnqueueStatus(FakeSalesServer.PaymentRequestsBegin, 503);
        _server.NextBeginReplayState = replayedState;

        (await RequestAsync(pc, buyer.Customer, Scope(live))).Result.Should().Be(PaymentRequestResult.BalanceUncertain);

        var begins = Begins();
        begins.Should().HaveCount(2);
        pc.Launcher.LaunchedUrls.Should().BeEmpty();
        _server.Calls(FakeSalesServer.BalancePreviewEndpoint).Should().BeEmpty();
        if (expectedFinish is null)
        {
            _server.Calls(FakeSalesServer.PaymentRequestsFinish).Should().BeEmpty("son hâldeki kayıt bitirilmez");
            return;
        }
        var finish = _server.Calls(FakeSalesServer.PaymentRequestsFinish).Should().ContainSingle().Subject;
        finish.RecordId.Should().Be(begins[0].GetProperty("recordId").GetGuid());
        finish.Json.GetProperty("outcome").GetString().Should().Be(expectedFinish);
    }

    [Fact]
    public async Task Begin_ilk_denemesi_belirsizken_yinelemesi_reddedilirse_ilk_deneme_abandoned_ile_kapanir()
    {
        // İlk begin'in yanıtı alınamadı (sunucu kişi kilidini tutarken yavaş); aynı Id'li yineleme kilidi bekleyip 409
        // in-progress-elsewhere (kilit zaman aşımı, makine ve kayıt yok) aldı. İstemci Id'yi siliyordu: ilk deneme sonradan
        // Sending olarak yazılırsa hiç kapanmıyor, 120 sn öbür bilgisayarları engelleyip kalıcı "istenmiş olabilir" kalıyordu.
        // Sonucu bilinmeyen deneme çıkışta abandoned ile kapanır (kayıt yoksa sunucu record-not-found der, zararsız düşer).
        var pc = await PcAsync();
        var live = pc.StartStream();
        var buyer = await BuyerAsync(pc, live, "alici_belirsiz_ret", 120m);
        _server.EnqueueStatus(FakeSalesServer.PaymentRequestsBegin, 503);
        _server.EnqueueProblem(FakeSalesServer.PaymentRequestsBegin, 409, "in-progress-elsewhere");

        (await RequestAsync(pc, buyer.Customer, Scope(live))).Result.Should().Be(PaymentRequestResult.PaymentInProgress);

        var begins = Begins();
        begins.Should().HaveCount(2);
        begins[1].GetProperty("recordId").GetGuid().Should().Be(begins[0].GetProperty("recordId").GetGuid(),
            "yineleme aynı Id'yle");
        pc.Launcher.LaunchedUrls.Should().BeEmpty();
        var finish = _server.Calls(FakeSalesServer.PaymentRequestsFinish).Should().ContainSingle().Subject;
        finish.RecordId.Should().Be(begins[0].GetProperty("recordId").GetGuid());
        finish.Json.GetProperty("outcome").GetString().Should().Be("abandoned");
    }

    [Fact]
    public async Task Belirsiz_begin_sonrasi_ozet_reddinde_yeni_kira_teslim_eski_deneme_abandoned_kapanir()
    {
        // İlk begin belirsiz, aynı Id'li yineleme invalid-priors-digest (400) aldı; özetsiz yeni Id'li begin kirayı aldı ve
        // mesaj gitti. Yeni kira delivered kapanır; sonucu bilinmeyen ilk deneme de abandoned ile kapanır — yeni Id'nin
        // akışa yazılması onu unutturmaz.
        var pc = await PcAsync();
        UseCloudApi(pc);
        var live = pc.StartStream();
        var buyer = await BuyerAsync(pc, live, "alici_belirsiz_ozet", 120m);
        var stale = PaymentLeaseConfirmation.Create(FakeSalesServer.PriorsDigest.Compute([Guid.NewGuid()]), []);
        _server.EnqueueStatus(FakeSalesServer.PaymentRequestsBegin, 503);
        _server.EnqueueProblem(FakeSalesServer.PaymentRequestsBegin, 400, "invalid-priors-digest");

        (await RequestAsync(pc, buyer.Customer, Scope(live), new HashSet<string>(StringComparer.Ordinal) { stale.Token }))
            .Result.Should().Be(PaymentRequestResult.Sent);

        var begins = Begins();
        begins.Should().HaveCount(3);
        var first = begins[0].GetProperty("recordId").GetGuid();
        var lease = begins[2].GetProperty("recordId").GetGuid();
        lease.Should().NotBe(first);
        var finishes = _server.Calls(FakeSalesServer.PaymentRequestsFinish);
        finishes.Should().HaveCount(2);
        finishes.Single(f => f.RecordId == lease).Json.GetProperty("outcome").GetString().Should().Be("delivered");
        finishes.Single(f => f.RecordId == first).Json.GetProperty("outcome").GetString().Should().Be("abandoned");
    }

    // ── kapalı hata ve 409 eşlemeleri ─────────────────────────────────────

    [Fact]
    public async Task Baska_bilgisayarin_kirasi_PaymentInProgress_makine_adiyla_mesaj_ve_finish_yok()
    {
        var pc = await PcAsync();
        UseCloudApi(pc);
        var live = pc.StartStream();
        var buyer = await BuyerAsync(pc, live, "alici_baska_pc", 120m);
        _server.EnqueueProblem(FakeSalesServer.PaymentRequestsBegin, 409, "in-progress-elsewhere",
            new Dictionary<string, object?>
            {
                ["machine"] = "PC-B", ["since"] = DateTimeOffset.UtcNow, ["recordId"] = Guid.NewGuid(),
            });

        var outcome = await RequestAsync(pc, buyer.Customer, Scope(live));

        outcome.Result.Should().Be(PaymentRequestResult.PaymentInProgress);
        outcome.Message.Should().Be("PC-B'de bu kişiye ödeme isteniyor — biraz sonra tekrar deneyin.");
        _server.Calls(FakeSalesServer.WhatsAppSend).Should().BeEmpty();
        _server.Calls(FakeSalesServer.PaymentRequestsFinish).Should().BeEmpty("ret kira yazmaz; kapatılacak bir şey yok");
        _server.Calls(FakeSalesServer.BalancePreviewEndpoint).Should().BeEmpty("bakiyeden önce durdu");
    }

    [Fact]
    public async Task Kilit_zaman_asiminda_makine_adi_yoksa_bugunku_metin()
    {
        var pc = await PcAsync();
        var live = pc.StartStream();
        var buyer = await BuyerAsync(pc, live, "alici_kilit", 120m);
        _server.EnqueueProblem(FakeSalesServer.PaymentRequestsBegin, 409, "in-progress-elsewhere");
        var dialogs = new FakeDialogService();

        var outcome = await RequestAsync(pc, buyer.Customer, Scope(live));
        PaymentResultPresenter.Notify(dialogs, outcome);

        outcome.Result.Should().Be(PaymentRequestResult.PaymentInProgress);
        outcome.Message.Should().BeNull();
        dialogs.InfosShown.Should().Equal(
            "Bu müşteri için başka bir pencerede ödeme isteği sürüyor — biraz sonra tekrar deneyin.");
    }

    [Fact]
    public async Task Begin_cevrimdisi_GUIDsiz_ve_yalniz_kendi_etiketleri_olan_kiside_yerel_yol_ve_kayit()
    {
        var pc = await PcAsync();
        var live = pc.StartStream();
        var guidless = GuidlessCustomer(pc, "ornek_yerel");
        var label = InsertLabel(pc, live, guidless.Id, "tiktok", "ornek_yerel", 100m, printed: true);
        _server.EnqueueTimeout(FakeSalesServer.PaymentRequestsBegin);
        _server.EnqueueTimeout(FakeSalesServer.PaymentRequestsBegin);

        (await RequestAsync(pc, guidless, Scope(live))).Result.Should().Be(PaymentRequestResult.Opened,
            "§5.8: yalnız kendi etiketleri olan GUID'siz kişide bugünkü gibi yerel (çevrimdışı canlı yayın, Y3)");

        pc.Launcher.LaunchedUrls.Should().ContainSingle();
        var record = pc.PendingPaymentRecords.List().Where(r => r.Kind == PendingPaymentRecordKinds.Record)
            .Select(PendingPaymentRecordCodec.DecodeRecord).Should().ContainSingle(
                "kayıt bağlantı gelince yazılır (W3-06'nın kirasız kaydı)").Subject;
        record.OwnerId.Should().Be(guidless.Id);
        record.ScopeKey.Should().Be(Scope(live));
        record.State.Should().Be(PaymentRequestStates.Delivered);
        record.NetAmount.Should().Be(100m + 50m);
        record.OrderIds.Should().Equal(Guid.ParseExact(label, "N"));
        FinishOutcomes().Should().Equal(new[] { "abandoned" },
            "sonucu bilinmeyen begin kapanır; teslimi kirasız kayıt taşır");
    }

    [Fact]
    public async Task Begin_cevrimdisi_GUIDsiz_ama_uzak_yayinda_etiketi_olan_kiside_mesaj_yok()
    {
        // §6.5-2: akıştan GUID'siz Id'li müşteri de iner; kural GUID'e değil uzak etikete de bakar.
        var pc = await PcAsync();
        var guidless = GuidlessCustomer(pc, "ornek_uzak");
        var remote = Guid.NewGuid();
        _server.SeedSession(remote, DateTimeOffset.UtcNow.AddHours(-3), DateTimeOffset.UtcNow.AddHours(-2),
            owner: Guid.NewGuid(), title: "Akşam yayını", ownerMachineName: "PC-B");
        _server.ReleaseSession(remote);
        var order = Guid.NewGuid();
        _server.SeedOrder(new FakeSalesServer.OrderBody(order, remote, guidless.Id, "tiktok", "ornek_uzak", "Örnek",
                "A1 aldım", "A1", 90m, DateTimeOffset.UtcNow.AddHours(-3), DateTimeOffset.UtcNow.AddHours(-3), null, null,
                false, false, false, null, null, null, null, null, null, null, null, null),
            version: 1, origin: Guid.NewGuid());
        await pc.SettleAsync();
        pc.SessionOf(remote.ToString("N"))!.Origin.Should().Be("remote");
        pc.Row(order.ToString("N"))!.CustomerId.Should().Be(guidless.Id);
        _server.EnqueueTimeout(FakeSalesServer.PaymentRequestsBegin);
        _server.EnqueueTimeout(FakeSalesServer.PaymentRequestsBegin);

        (await RequestAsync(pc, guidless, "cumulative")).Result.Should().Be(PaymentRequestResult.BalanceUncertain,
            "uzak etiketi olan hiçbir kişi kirayı atlayamaz (fail-closed)");

        pc.Launcher.LaunchedUrls.Should().BeEmpty();
        pc.PendingPaymentRecords.List().Should().NotContain(r => r.Kind == PendingPaymentRecordKinds.Record);
    }

    [Fact]
    public async Task Kira_ucu_yoksa_satir_ici_engel_mesaj_yok()
    {
        var pc = await PcAsync();
        var live = pc.StartStream();
        var buyer = await BuyerAsync(pc, live, "alici_uc_yok", 120m);
        _server.EnqueueStatus(FakeSalesServer.PaymentRequestsBegin, 404);

        var outcome = await RequestAsync(pc, buyer.Customer, Scope(live));

        outcome.Result.Should().Be(PaymentRequestResult.Blocked, "§8.6: S3 geri alınırsa GUID'li kişide fail-closed");
        outcome.Message.Should().Be("Sunucu güncelleniyor; biraz sonra tekrar dene.");
        pc.Launcher.LaunchedUrls.Should().BeEmpty();
        _server.Calls(FakeSalesServer.PaymentRequestsFinish).Should().BeEmpty();
    }

    [Fact]
    public async Task Guncelleme_gerekli_engel_ve_guncelleme_sorusu_bir_kez()
    {
        var pc = await PcAsync();
        var live = pc.StartStream();
        var buyer = await BuyerAsync(pc, live, "alici_surum", 120m);
        _server.EnqueueProblem(FakeSalesServer.PaymentRequestsBegin, 409, "client-update-required");
        _server.EnqueueProblem(FakeSalesServer.PaymentRequestsBegin, 409, "client-update-required");

        var first = await RequestAsync(pc, buyer.Customer, Scope(live));
        var second = await RequestAsync(pc, buyer.Customer, Scope(live));

        first.Result.Should().Be(PaymentRequestResult.Blocked);
        first.Message.Should().Be("Bu işlem için uygulamayı güncelle.");
        second.Message.Should().Be("Bu işlem için uygulamayı güncelle.");
        pc.UpdatePrompts.Should().Be(1, "güncelleme sorusu süreç başına bir kez");
        pc.Launcher.LaunchedUrls.Should().BeEmpty();
    }

    [Theory]
    [InlineData(409, "content-conflict")]
    [InlineData(400, "invalid-orders")]
    public async Task Icerik_cakismasi_ve_gecersiz_istek_ic_hata_engeli(int status, string code)
    {
        var pc = await PcAsync();
        var live = pc.StartStream();
        var buyer = await BuyerAsync(pc, live, "alici_ic_hata", 120m);
        _server.EnqueueProblem(FakeSalesServer.PaymentRequestsBegin, status, code);

        var outcome = await RequestAsync(pc, buyer.Customer, Scope(live));

        outcome.Result.Should().Be(PaymentRequestResult.Blocked);
        outcome.Message.Should().Be("İşlem kaydedilemedi (iç hata); tekrar dene.");
        pc.Launcher.LaunchedUrls.Should().BeEmpty();
    }

    // ── already-requested sorusu (W3-09) ──────────────────────────────────

    /// <summary>Bugün 10:00 (yerel saat): sorudaki "(10:00)".</summary>
    private static readonly DateTimeOffset TenOClock = new(DateTime.Today.AddHours(10));

    /// <summary><c>already-requested</c>'ın öncülü (sunucunun <c>PaymentRequestPrior</c>'ı).</summary>
    private static object Prior(Guid id, string machine, decimal? net, decimal? before, DateTimeOffset at,
        bool unconfirmed = false) =>
        new { id, machine, netAmount = net, totalBeforeBalance = before, at, finishedAt = at.AddSeconds(15), unconfirmed };

    /// <summary>Sıradaki <c>begin</c>'in yanıtı 409 <c>already-requested</c>; #523 öncesi biçim (özet yok).</summary>
    private void EnqueueAlreadyRequested(params object[] priors) =>
        _server.EnqueueProblem(FakeSalesServer.PaymentRequestsBegin, 409, "already-requested",
            new Dictionary<string, object?> { ["priors"] = priors });

    /// <summary>Kartın ve raporun yolu: sorular operatöre sorulur (<see cref="PaymentResultPresenter.RequestConfirmedAsync"/>).</summary>
    private static Task<PaymentRequestOutcome> AskAsync(SalesPc pc, Customer customer, string scope,
        FakeDialogService dialogs) =>
        PaymentResultPresenter.RequestConfirmedAsync(dialogs, tokens => RequestAsync(pc, customer, scope, tokens));

    private static IReadOnlyList<Guid> ConfirmedPriorIds(System.Text.Json.JsonElement begin) =>
        begin.GetProperty("confirmedPriorIds").EnumerateArray().Select(i => i.GetGuid()).ToList();

    [Fact]
    public async Task Already_requested_PC_A_oncu_makine_adiyla_sorulur_mesaj_ve_finish_yok()
    {
        // §6.8: PC-B yeniden dener → "PC-A'dan 550 ₺ istendi (10:00); tekrar gönderilsin mi?"
        var pc = await PcAsync();
        UseCloudApi(pc);
        var live = pc.StartStream();
        var buyer = await BuyerAsync(pc, live, "alici_ret", 500m);
        EnqueueAlreadyRequested(Prior(Guid.NewGuid(), "PC-A", 550m, 550m, TenOClock));
        var dialogs = new FakeDialogService();

        var outcome = await AskAsync(pc, buyer.Customer, "cumulative", dialogs);

        outcome.Result.Should().Be(PaymentRequestResult.AlreadyRequested, "'Hayır': hiçbir şey yapılmadı");
        var (title, message) = dialogs.Confirmations.Should().ContainSingle().Subject;
        title.Should().Be("Ödeme isteği zaten gönderildi");
        message.Should().Be("PC-A'dan 550,00 TL istendi (10:00); tekrar gönderilsin mi?",
            "sahip kararı 2'nin sorusu, sunucunun öncülü makine adıyla (tutar uygulamanın biçimiyle)");
        outcome.Prompt!.Requests.Should().ContainSingle().Which.Machine.Should().Be("PC-A");
        _server.Calls(FakeSalesServer.WhatsAppSend).Should().BeEmpty();
        _server.Calls(FakeSalesServer.BalancePreviewEndpoint).Should().BeEmpty("bakiyeden önce durdu");
        _server.Calls(FakeSalesServer.PaymentRequestsBegin).Should().ContainSingle();
        _server.Calls(FakeSalesServer.PaymentRequestsFinish).Should().BeEmpty("ret kira yazmaz; kapatılacak kira yok");
        pc.Launcher.LaunchedUrls.Should().BeEmpty();
    }

    [Fact]
    public async Task Teslim_kaydi_olmadan_dolan_kira_istenmis_olabilir_diye_sorulur()
    {
        var pc = await PcAsync();
        var live = pc.StartStream();
        var buyer = await BuyerAsync(pc, live, "alici_dolmus", 500m);
        EnqueueAlreadyRequested(Prior(Guid.NewGuid(), "PC-A", net: null, before: 550m, TenOClock, unconfirmed: true));
        var dialogs = new FakeDialogService();

        var outcome = await AskAsync(pc, buyer.Customer, "cumulative", dialogs);

        outcome.Result.Should().Be(PaymentRequestResult.AlreadyRequested);
        var request = outcome.Prompt!.Requests.Should().ContainSingle().Subject;
        request.Amount.Should().Be(550m, "neti olmayan kirada sorunun tutarı kiranın bakiye öncesi tutarı (§6.8)");
        request.Unconfirmed.Should().BeTrue();
        var (title, message) = dialogs.Confirmations.Should().ContainSingle().Subject;
        title.Should().Be("Ödeme isteği gönderilmiş olabilir");
        message.Should().Be("10:00'da PC-A'dan 550,00 TL istenmiş olabilir (sonucu bilinmiyor); tekrar gönderilsin mi?");
        pc.Launcher.LaunchedUrls.Should().BeEmpty();
    }

    [Fact]
    public async Task Onayda_ikinci_begin_oncul_Idleriyle_gecer_mesaj_bir_kez()
    {
        var pc = await PcAsync();
        var live = pc.StartStream();
        var buyer = await BuyerAsync(pc, live, "alici_onayli", 500m);
        var priors = new[] { Guid.NewGuid(), Guid.NewGuid() };
        EnqueueAlreadyRequested(
            Prior(priors[0], "PC-B", 300m, 350m, TenOClock.AddHours(-2)),
            Prior(priors[1], "PC-A", 550m, 550m, TenOClock));
        var dialogs = new FakeDialogService { ConfirmResult = _ => true };

        var outcome = await AskAsync(pc, buyer.Customer, "cumulative", dialogs);

        outcome.Result.Should().Be(PaymentRequestResult.Opened);
        dialogs.Confirmations.Should().ContainSingle().Which.Message.Should().StartWith("PC-A'dan 550,00 TL istendi",
            "en yeni öncül sorulur (sunucunun kapanış sırası)");
        var begins = Begins();
        begins.Should().HaveCount(2);
        ConfirmedPriorIds(begins[0]).Should().BeEmpty();
        ConfirmedPriorIds(begins[1]).Should().Equal(priors, "onay belirteci uzak kayıt Id'lerini kapsar (§6.4)");
        begins[1].TryGetProperty("confirmedPriorsDigest", out _).Should().BeFalse("#523 öncesi yanıtta özet yoktu");
        pc.Launcher.LaunchedUrls.Should().ContainSingle("mesaj bir kez");
        _server.Calls(FakeSalesServer.PaymentRequestsFinish).Should().ContainSingle()
            .Which.RecordId.Should().Be(begins[1].GetProperty("recordId").GetGuid());
        FinishOutcomes().Should().Equal("delivered");
    }

    [Fact]
    public async Task Son_oncul_kiraysa_ve_tutar_degistiyse_soru_yeni_toplami_da_soyler()
    {
        var pc = await PcAsync();
        var live = pc.StartStream();
        var buyer = await BuyerAsync(pc, live, "alici_k5", 500m);
        EnqueueAlreadyRequested(Prior(Guid.NewGuid(), "PC-B", 200m, 300m, TenOClock));

        var outcome = await RequestAsync(pc, buyer.Customer, "cumulative");

        outcome.Prompt!.NewTotal.Should().Be(550m, "K5: bu isteğin bakiye öncesi toplamı son kiranınkinden farklı");
        outcome.Prompt.PriorHadBalance.Should().BeTrue("son kiranın netinden bakiye düşülmüştü");
    }

    /// <summary>Bu bilgisayarın teslim edilmiş isteği: ilk istek kirayla gider (sunucu kaydı yok sayılır), yerel iş teslim
    /// edilmiş olur. Döner: o isteğin kira Id'si.</summary>
    private async Task<Guid> DeliveredLocallyAsync(SalesPc pc, Customer customer, string scope)
    {
        (await RequestAsync(pc, customer, scope)).Result.Should().Be(PaymentRequestResult.Opened);
        pc.Jobs.GetPersonScopeJobs([customer.Id], scope).Jobs.Should().ContainSingle().Which.IsDelivered.Should().BeTrue();
        return Begins()[^1].GetProperty("recordId").GetGuid();
    }

    [Fact]
    public async Task Yerel_teslim_ve_uzak_oncul_tek_diyalog_onayda_mesaj()
    {
        var pc = await PcAsync();
        var live = pc.StartStream();
        var buyer = await BuyerAsync(pc, live, "alici_iki_kapi", 500m);
        var own = await DeliveredLocallyAsync(pc, buyer.Customer, "cumulative");
        EnqueueAlreadyRequested(
            Prior(own, "PC-A", 550m, 550m, TenOClock.AddHours(-1)),
            Prior(Guid.NewGuid(), "PC-B", 550m, 550m, TenOClock));
        var dialogs = new FakeDialogService { ConfirmResult = _ => true };

        var outcome = await AskAsync(pc, buyer.Customer, "cumulative", dialogs);

        outcome.Result.Should().Be(PaymentRequestResult.Opened);
        dialogs.Confirmations.Should().ContainSingle("tek onay uzak onayı da yerel kapıyı da geçirir; ikinci diyalog yok")
            .Which.Message.Should().Be("PC-B'den 550,00 TL istendi (10:00); tekrar gönderilsin mi?");
        pc.Launcher.LaunchedUrls.Should().HaveCount(2, "ilk istek ve onaylanan tekrar");
        Begins().Should().HaveCount(3);
        FinishOutcomes().Should().Equal("delivered", "delivered");
        pc.Jobs.GetPersonScopeJobs([buyer.Customer.Id], "cumulative").Jobs.Should().ContainSingle()
            .Which.Deliveries.Should().Be(2);
    }

    [Fact]
    public async Task Yerel_teslim_ve_uzak_oncul_Hayir_mesaj_yok_yeni_kira_yok()
    {
        var pc = await PcAsync();
        var live = pc.StartStream();
        var buyer = await BuyerAsync(pc, live, "alici_hayir", 500m);
        var own = await DeliveredLocallyAsync(pc, buyer.Customer, "cumulative");
        EnqueueAlreadyRequested(Prior(own, "PC-A", 550m, 550m, TenOClock));
        var dialogs = new FakeDialogService();

        var outcome = await AskAsync(pc, buyer.Customer, "cumulative", dialogs);

        outcome.Result.Should().Be(PaymentRequestResult.AlreadyRequested);
        outcome.Prompt!.Kind.Should().Be(PersonScopePromptKind.Resend);
        outcome.Prompt.AlsoConfirms.Should().ContainSingle("yerel planın belirteci aynı soruya katıldı");
        dialogs.Confirmations.Should().ContainSingle();
        pc.Launcher.LaunchedUrls.Should().ContainSingle("yalnız ilk istek");
        Begins().Should().HaveCount(2);
        FinishOutcomes().Should().Equal(new[] { "delivered" }, "409 kira yazmaz: kapatılacak kira yok");
    }

    [Fact]
    public async Task Soru_ile_onay_arasinda_yerel_durum_degisirse_kira_abandoned_kapanir_yerel_soru_Hayir_mesaj_yok()
    {
        var pc = await PcAsync();
        var live = pc.StartStream();
        var buyer = await BuyerAsync(pc, live, "alici_degisti", 500m);
        var own = await DeliveredLocallyAsync(pc, buyer.Customer, "cumulative");
        EnqueueAlreadyRequested(Prior(own, "PC-A", 550m, 550m, TenOClock));
        var job = pc.Jobs.GetPersonScopeJobs([buyer.Customer.Id], "cumulative").Jobs.Single();
        var answers = 0;
        var dialogs = new FakeDialogService
        {
            ConfirmResult = _ =>
            {
                if (++answers > 1) return false;
                pc.Jobs.BeginSend(job.Id, 550m);                          // öbür pencere bu arada gönderdi
                return true;
            },
        };

        var outcome = await AskAsync(pc, buyer.Customer, "cumulative", dialogs);

        outcome.Result.Should().Be(PaymentRequestResult.AlreadyRequested);
        dialogs.Confirmations.Should().HaveCount(2, "yerel belirteç tutmadı; yerel kapı yeniden sordu (bugünkü gibi)");
        dialogs.Confirmations[1].Message.Should().Contain("sonucu kaydedilemedi");
        pc.Launcher.LaunchedUrls.Should().ContainSingle("yalnız ilk istek");
        var begins = Begins();
        begins.Should().HaveCount(3);
        var finishes = _server.Calls(FakeSalesServer.PaymentRequestsFinish);
        finishes.Select(f => f.Json.GetProperty("outcome").GetString()).Should().Equal("delivered", "abandoned");
        finishes[1].RecordId.Should().Be(begins[2].GetProperty("recordId").GetGuid(),
            "onay turunun kirası soruyla kapandı (W3-07)");
    }

    [Fact]
    public async Task Duzeltilmis_istek_listesi_uzak_ve_yerel_soruyu_tek_onayla_gecer()
    {
        var pc = await PcAsync();
        var live = pc.StartStream();
        var buyer = await BuyerAsync(pc, live, "alici_duzeltme_onay", 500m);
        var own = await DeliveredLocallyAsync(pc, buyer.Customer, "cumulative");
        EnqueueAlreadyRequested(Prior(own, "PC-A", 550m, 550m, TenOClock));
        var corrected = new OrderDeck.App.Services.PriceCorrection.CorrectedPaymentRequests(
            pc.Customers, pc.LabelRepo, pc.Jobs, pc.Shipping, pc.Payments);
        var candidate = new OrderDeck.App.Services.PriceCorrection.CorrectedRequestCandidate(
            buyer.Customer.Id, "Örnek", "cumulative", 550m, TenOClock, false, 550m);

        var outcome = (await corrected.ResendAsync(live, DateTime.Now, [candidate])).Should().ContainSingle().Subject;

        outcome.Status.Should().Be(OrderDeck.App.Services.PriceCorrection.ResendStatus.Opened,
            "listenin onayı sorunun bütün belirteçlerini verir; yerel kapı ikinci kez sormaz");
        pc.Launcher.LaunchedUrls.Should().HaveCount(2);
    }

    // ── onay özeti: priorsDigest → confirmedPriorsDigest (#523; W3-09) ────
    // Sahte sunucu #523'ün (e) kuralını aynalar (FakeSalesServer.AlreadyRequestedRule): Id listesinin dışında öncül
    // kalırsa onay özeti bütün kümenin özetine eşit olmalı.

    /// <summary>Sunucuya alıcının kümülatif kapsamındaki bir kaydını ekler; kapanış sırası <paramref name="minute"/>.</summary>
    private FakeSalesServer.PaymentPrior SeedPrior(Customer buyer, string machine, decimal net, int minute)
    {
        var at = TenOClock.AddMinutes(minute);
        var prior = new FakeSalesServer.PaymentPrior(Guid.NewGuid(), machine, [buyer.Id], "cumulative", net, net, at,
            at.AddSeconds(15), Unconfirmed: false);
        _server.AddPaymentPrior(prior);
        return prior;
    }

    private static string? ConfirmedDigest(System.Text.Json.JsonElement begin) =>
        begin.TryGetProperty("confirmedPriorsDigest", out var digest) ? digest.GetString() : null;

    [Fact]
    public async Task Onayda_ikinci_begin_409un_ozetini_aynen_ve_oncul_Idlerini_tasir_gecer_mesaj_bir_kez()
    {
        var pc = await PcAsync();
        var live = pc.StartStream();
        var buyer = await BuyerAsync(pc, live, "alici_ozet", 500m);
        var priors = new[] { SeedPrior(buyer.Customer, "PC-B", 300m, -60), SeedPrior(buyer.Customer, "PC-A", 550m, 0) };
        var dialogs = new FakeDialogService { ConfirmResult = _ => true };

        var outcome = await AskAsync(pc, buyer.Customer, "cumulative", dialogs);

        outcome.Result.Should().Be(PaymentRequestResult.Opened);
        dialogs.Confirmations.Should().ContainSingle()
            .Which.Message.Should().Be("PC-A'dan 550,00 TL istendi (10:00); tekrar gönderilsin mi?");
        var begins = Begins();
        begins.Should().HaveCount(2);
        ConfirmedDigest(begins[0]).Should().BeNull("ilk begin'de onay yok: alan telde hiç yazılmaz");
        ConfirmedDigest(begins[1]).Should().Be(FakeSalesServer.PriorsDigest.Compute(priors.Select(p => p.Id)),
            "409'un priorsDigest'i aynen");
        ConfirmedPriorIds(begins[1]).Should().Equal(priors.Select(p => p.Id));
        pc.Launcher.LaunchedUrls.Should().ContainSingle("mesaj bir kez");
        FinishOutcomes().Should().Equal("delivered");
    }

    [Fact]
    public async Task Besyuzu_asan_oncullu_soru_tek_onay_turunda_ozetle_ve_en_cok_500_Idyle_gecer()
    {
        // §12-16, §13 W3: kayıtlar kapanmaz; 500'den fazla öncül biriken kişi × kapsam Id listesiyle kalıcı kilitlenirdi.
        var pc = await PcAsync();
        var live = pc.StartStream();
        var buyer = await BuyerAsync(pc, live, "alici_sadik", 500m);
        var priors = Enumerable.Range(0, 600).Select(i => SeedPrior(buyer.Customer, "PC-B", 550m, i - 600)).ToList();
        var dialogs = new FakeDialogService { ConfirmResult = _ => true };

        var outcome = await AskAsync(pc, buyer.Customer, "cumulative", dialogs);

        outcome.Result.Should().Be(PaymentRequestResult.Opened);
        dialogs.Confirmations.Should().ContainSingle("tek diyalog");
        var begins = Begins();
        begins.Should().HaveCount(2, "tek onay turu");
        ConfirmedPriorIds(begins[1]).Should().HaveCount(PaymentRequestLimits.MaxConfirmedPriorIds,
                "fazlası gönderilmez (500'ü aşan liste 400 invalid-confirmed-priors alırdı)")
            .And.Equal(priors.Take(PaymentRequestLimits.MaxConfirmedPriorIds).Select(p => p.Id));
        ConfirmedDigest(begins[1]).Should().Be(FakeSalesServer.PriorsDigest.Compute(priors.Select(p => p.Id)),
            "bütün kümeyi özet onaylar");
        pc.Launcher.LaunchedUrls.Should().ContainSingle();
    }

    [Fact]
    public async Task Soru_ile_onay_arasinda_kume_degisirse_yeni_ozetle_yeniden_sorulur_ucuncu_begin_yalniz_en_yeni_ozeti_tasir()
    {
        var pc = await PcAsync();
        var live = pc.StartStream();
        var buyer = await BuyerAsync(pc, live, "alici_kume", 500m);
        var first = SeedPrior(buyer.Customer, "PC-B", 300m, -60);
        FakeSalesServer.PaymentPrior? late = null;
        var dialogs = new FakeDialogService
        {
            ConfirmResult = _ =>
            {
                late ??= SeedPrior(buyer.Customer, "PC-C", 550m, 0);  // soru ile onay arasında PC-C istedi
                return true;
            },
        };

        var outcome = await AskAsync(pc, buyer.Customer, "cumulative", dialogs);

        outcome.Result.Should().Be(PaymentRequestResult.Opened);
        dialogs.Confirmations.Should().HaveCount(2, "küme değişti: taze 409 yeni özetle yeniden sordu");
        dialogs.Confirmations[1].Message.Should().StartWith("PC-C'den 550,00 TL istendi");
        var begins = Begins();
        begins.Should().HaveCount(3);
        var d1 = FakeSalesServer.PriorsDigest.Compute([first.Id]);
        var d2 = FakeSalesServer.PriorsDigest.Compute([first.Id, late!.Id]);
        ConfirmedDigest(begins[1]).Should().Be(d1);
        ConfirmedPriorIds(begins[1]).Should().Equal(first.Id);
        ConfirmedDigest(begins[2]).Should().Be(d2, "yeni already-requested'in özeti öncekinin yerini aldı");
        ConfirmedPriorIds(begins[2]).Should().Equal(new[] { late.Id }, "yalnız en yeni sorunun öncülleri");
        _server.Calls(FakeSalesServer.PaymentRequestsBegin).Should().OnlyContain(
            c => !c.RawJson.Contains("confirmedThrough", StringComparison.OrdinalIgnoreCase),
            "terk edilen filigran hiçbir biçimde yazılmaz");
        typeof(PaymentRequestBeginRequest).GetProperties().Should().NotContain(p => p.Name.Contains("Through"));
        pc.Launcher.LaunchedUrls.Should().ContainSingle();
    }

    [Fact]
    public async Task Invalid_priors_digest_ozet_atilir_bir_kez_ozetsiz_yinelenir_sonuc_soru()
    {
        var pc = await PcAsync();
        var live = pc.StartStream();
        var buyer = await BuyerAsync(pc, live, "alici_bozuk_ozet", 500m);
        SeedPrior(buyer.Customer, "PC-B", 550m, 0);
        var stale = PaymentLeaseConfirmation.Create(FakeSalesServer.PriorsDigest.Compute([Guid.NewGuid()]), []);
        _server.EnqueueProblem(FakeSalesServer.PaymentRequestsBegin, 400, "invalid-priors-digest");

        var outcome = await RequestAsync(pc, buyer.Customer, "cumulative",
            new HashSet<string>(StringComparer.Ordinal) { stale.Token });

        outcome.Result.Should().Be(PaymentRequestResult.AlreadyRequested, "sert hata yok; sunucunun taze 409'u soruyu getirdi");
        outcome.Prompt!.Requests.Should().ContainSingle().Which.Machine.Should().Be("PC-B");
        var begins = Begins();
        begins.Should().HaveCount(2, "bir kez özetsiz yinelendi");
        ConfirmedDigest(begins[0]).Should().Be(stale.PriorsDigest);
        begins[1].TryGetProperty("confirmedPriorsDigest", out _).Should().BeFalse("özet atıldı: gövdede anahtar yok");
        ConfirmedPriorIds(begins[1]).Should().BeEmpty();
        pc.Launcher.LaunchedUrls.Should().BeEmpty();
        _server.Calls(FakeSalesServer.PaymentRequestsFinish).Should().BeEmpty();
    }

    // ── yeniden denenen retler (W3-08) ────────────────────────────────────

    private IReadOnlyList<System.Text.Json.JsonElement> Begins() =>
        _server.Calls(FakeSalesServer.PaymentRequestsBegin).Select(c => c.Json).ToList();

    private static IReadOnlyList<string?> MemberIds(System.Text.Json.JsonElement begin) =>
        begin.GetProperty("memberIds").EnumerateArray().Select(m => m.GetString()).ToList();

    /// <summary>GUID'li, telefonsuz yerel müşteri (grubun ikinci hesabı).</summary>
    private static Customer GuidCustomer(SalesPc pc, string platform, string username)
    {
        var customer = new Customer(Guid.NewGuid().ToString("N"), platform, username, "Örnek", null,
            1, 1, false, null, null, 0, 0m, null, null, null);
        pc.Customers.Insert(customer);
        return customer;
    }

    /// <summary>Başka bilgisayarın (PC-B) devredilmiş yayınında alıcının basılmış etiketi — yalnız sunucuda; bu bilgisayara
    /// ancak bir akış turuyla iner.</summary>
    private Guid SeedRemoteOrder(Customer buyer, decimal price) => SeedRemote(buyer, price).Order;

    /// <summary><see cref="SeedRemoteOrder"/>; yayının Id'siyle.</summary>
    private (Guid Session, Guid Order) SeedRemote(Customer buyer, decimal price)
    {
        var session = Guid.NewGuid();
        _server.SeedSession(session, DateTimeOffset.UtcNow.AddHours(-3), DateTimeOffset.UtcNow.AddHours(-2),
            owner: Guid.NewGuid(), title: "Akşam yayını", ownerMachineName: "PC-B");
        _server.ReleaseSession(session);
        var order = Guid.NewGuid();
        _server.SeedOrder(new FakeSalesServer.OrderBody(order, session, buyer.Id, buyer.Platform, buyer.Username, "Örnek",
                "R1 aldım", "R1", price, DateTimeOffset.UtcNow.AddHours(-3), DateTimeOffset.UtcNow.AddHours(-3), null, null,
                false, false, false, null, null, null, null, null, null, null, null, null),
            version: 1, origin: Guid.NewGuid());
        return (session, order);
    }

    private static Dictionary<string, object?> StaleTotal(decimal serverTotal, string? liveMachine = null)
    {
        var extensions = new Dictionary<string, object?> { ["serverTotal"] = serverTotal };
        if (liveMachine is not null)
            extensions["liveElsewhere"] = new[] { new { sessionId = Guid.NewGuid(), machine = liveMachine } };
        return extensions;
    }

    [Fact]
    public async Task Members_stale_kiralar_birakilmisken_musteri_turu_kisi_yeniden_kurulur_bir_kez_yeniden_dener()
    {
        var pc = await PcAsync();
        var live = pc.StartStream();
        var buyer = await BuyerAsync(pc, live, "alici_grup", 120m);
        var second = GuidCustomer(pc, "instagram", "alici_grup_ig");
        _server.EnqueueProblem(FakeSalesServer.PaymentRequestsBegin, 409, "members-stale",
            new Dictionary<string, object?> { ["members"] = new[] { buyer.Customer.Id, second.Id } });
        var busyDuringTour = new List<bool>();
        _server.DuringCustomerChanges = () =>
        {
            busyDuringTour.Add(pc.Busy.RunLocked(isBusy => isBusy(buyer.Customer.Id)));
            // Müşteri akışı grup değişikliğini getirdi (FakeCustomerServer'ın işi; burada yerelde taklit).
            if (busyDuringTour.Count == 1) pc.Customers.MergeIntoGroup([buyer.Customer.Id, second.Id]);
        };

        var outcome = await RequestAsync(pc, buyer.Customer, Scope(live));

        outcome.Result.Should().Be(PaymentRequestResult.Opened, "müşteri turundan sonra yeniden deneme geçti");
        busyDuringTour.Should().NotBeEmpty("members-stale bir müşteri akışı turu koşturur")
            .And.OnlyContain(busy => !busy, "tur sırasında kişinin kirası bırakılmıştı (ApplyItem meşgul müşteriyi erteler)");
        var begins = Begins();
        begins.Should().HaveCount(2);
        MemberIds(begins[0]).Should().Equal(buyer.Customer.Id);
        MemberIds(begins[1]).Should().BeEquivalentTo([buyer.Customer.Id, second.Id], "kişi yeniden kuruldu");
        begins[1].GetProperty("recordId").GetGuid().Should().NotBe(begins[0].GetProperty("recordId").GetGuid(),
            "her yeniden koşu yeni recordId");
        _server.Calls(FakeSalesServer.PaymentRequestsFinish).Select(f => f.RecordId).Should().Equal(
            new Guid?[] { begins[1].GetProperty("recordId").GetGuid() }, "ret kira yazmaz; yalnız geçen kira kapanır");
        pc.Launcher.LaunchedUrls.Should().ContainSingle();
    }

    [Fact]
    public async Task Members_stale_surerse_satir_ici_engel_mesaj_yok_tam_iki_begin()
    {
        var pc = await PcAsync();
        UseCloudApi(pc);
        var live = pc.StartStream();
        var buyer = await BuyerAsync(pc, live, "alici_grup_surer", 120m);
        for (var i = 0; i < 2; i++)
            _server.EnqueueProblem(FakeSalesServer.PaymentRequestsBegin, 409, "members-stale",
                new Dictionary<string, object?> { ["members"] = new[] { buyer.Customer.Id, Guid.NewGuid().ToString("N") } });
        var mark = _server.RequestLog.Count;

        var outcome = await RequestAsync(pc, buyer.Customer, Scope(live));

        outcome.Result.Should().Be(PaymentRequestResult.Blocked);
        outcome.Message.Should().Be("Müşteri grupları eşitleniyor; biraz sonra tekrar dene.");
        Begins().Should().HaveCount(2, "akış bir kez yeniden koşar");
        Log(mark).Should().ContainInOrder("begin", "GET " + CustomerChangesPath(), "begin");
        _server.Calls(FakeSalesServer.WhatsAppSend).Should().BeEmpty();
        _server.Calls(FakeSalesServer.BalancePreviewEndpoint).Should().BeEmpty("bakiyeden önce durdu");
        _server.Calls(FakeSalesServer.PaymentRequestsFinish).Should().BeEmpty();
        pc.Launcher.LaunchedUrls.Should().BeEmpty();
    }

    private string CustomerChangesPath() => $"/api/v1/licenses/{_server.LicenseId}/wpf-customers/changes";

    [Fact]
    public async Task Stale_total_satis_turundan_sonra_guncel_toplamla_gecer()
    {
        var pc = await PcAsync();
        var live = pc.StartStream();
        var buyer = await BuyerAsync(pc, live, "alici_toplam", 120m);
        var remote = SeedRemoteOrder(buyer.Customer, 90m);
        _server.EnqueueProblem(FakeSalesServer.PaymentRequestsBegin, 409, "stale-total", StaleTotal(210m));

        var outcome = await RequestAsync(pc, buyer.Customer, "cumulative");

        outcome.Result.Should().Be(PaymentRequestResult.Opened);
        var begins = Begins();
        begins.Should().HaveCount(2);
        begins[0].GetProperty("productTotal").GetDecimal().Should().Be(120m);
        begins[1].GetProperty("productTotal").GetDecimal().Should().Be(210m,
            "satış akışı turu PC-B'nin etiketini indirdi (kira bırakılmıştı; ApplyItem müşteriyi ertelemedi)");
        begins[1].GetProperty("orderIds").EnumerateArray().Select(o => o.GetGuid()).Should().Contain(remote);
        begins[1].GetProperty("totalBeforeBalance").GetDecimal().Should().Be(210m + 50m);
        FinishOutcomes().Should().Equal("delivered");
        _server.Calls(FakeSalesServer.PaymentRequestsFinish).Single().Json.GetProperty("netAmount").GetDecimal()
            .Should().Be(260m, "mesaj güncel toplamla gitti");
        pc.Launcher.LaunchedUrls.Should().ContainSingle();
    }

    [Fact]
    public async Task Stale_total_surerse_metnin_iki_tutari_ikinci_denemeden()
    {
        var pc = await PcAsync();
        UseCloudApi(pc);
        var live = pc.StartStream();
        var buyer = await BuyerAsync(pc, live, "alici_toplam_surer", 120m);
        SeedRemoteOrder(buyer.Customer, 90m);
        _server.EnqueueProblem(FakeSalesServer.PaymentRequestsBegin, 409, "stale-total", StaleTotal(250m));
        _server.EnqueueProblem(FakeSalesServer.PaymentRequestsBegin, 409, "stale-total", StaleTotal(260m));

        var outcome = await RequestAsync(pc, buyer.Customer, "cumulative");

        outcome.Result.Should().Be(PaymentRequestResult.Blocked);
        outcome.Message.Should().Be("Bu bilgisayar güncel değil (sunucu 260,00 TL, burada 210,00 TL).",
            "sunucunun toplamı ikinci 409'dan, yerel toplam turdan sonra yeniden okunan");
        Begins().Should().HaveCount(2);
        _server.Calls(FakeSalesServer.WhatsAppSend).Should().BeEmpty();
        _server.Calls(FakeSalesServer.PaymentRequestsFinish).Should().BeEmpty();
    }

    [Fact]
    public async Task Stale_total_liveElsewhere_surerse_baska_bilgisayarin_yayini_metni()
    {
        var pc = await PcAsync();
        var live = pc.StartStream();
        var buyer = await BuyerAsync(pc, live, "alici_canli_baska", 120m);
        _server.EnqueueProblem(FakeSalesServer.PaymentRequestsBegin, 409, "stale-total", StaleTotal(200m, "PC-B"));
        _server.EnqueueProblem(FakeSalesServer.PaymentRequestsBegin, 409, "stale-total", StaleTotal(200m, "PC-B"));

        var outcome = await RequestAsync(pc, buyer.Customer, "cumulative");

        outcome.Result.Should().Be(PaymentRequestResult.Blocked);
        outcome.Message.Should().Be(
            "Bu kişinin PC-B'de süren yayında etiketi var; istek yayın bitip devredilince yapılabilir.");
        Begins().Should().HaveCount(2);
        pc.Launcher.LaunchedUrls.Should().BeEmpty();
    }

    [Fact]
    public async Task Members_stale_ve_stale_totalin_yeniden_deneme_haklari_ayri()
    {
        // §6.4: members-stale de stale-total de "bir tur ve bir kez yeniden deneme" alır. Hak ortaktı: grup değişikliğiyle
        // gelen members-stale'den sonra kişi yeniden kurulunca küme yeni üyenin henüz inmemiş etiketleri yüzünden tutmuyor
        // (stale-total), satış turu hiç koşmadan "Bu bilgisayar güncel değil" engeli çıkıyordu.
        var pc = await PcAsync();
        var live = pc.StartStream();
        var buyer = await BuyerAsync(pc, live, "alici_ayri_hak", 120m);
        _server.EnqueueProblem(FakeSalesServer.PaymentRequestsBegin, 409, "members-stale",
            new Dictionary<string, object?> { ["members"] = new[] { buyer.Customer.Id } });
        _server.EnqueueProblem(FakeSalesServer.PaymentRequestsBegin, 409, "stale-total", StaleTotal(180m));

        var outcome = await RequestAsync(pc, buyer.Customer, Scope(live));

        outcome.Result.Should().Be(PaymentRequestResult.Opened, "stale-total'ın kendi turu ve yeniden denemesi var");
        Begins().Should().HaveCount(3);
        pc.Launcher.LaunchedUrls.Should().ContainSingle();
    }

    [Fact]
    public async Task Her_ret_turunun_hakki_bir_kez_ikinci_members_stale_engeldir()
    {
        // Haklar ayrı ama her biri bir kez: members-stale → stale-total → members-stale zincirinde üçüncü ret engeldir (döngü
        // yok; bir isteğin en çok bir müşteri turu, bir satış turu ve bir Off turu).
        var pc = await PcAsync();
        var live = pc.StartStream();
        var buyer = await BuyerAsync(pc, live, "alici_hak_bir_kez", 120m);
        var members = new Dictionary<string, object?> { ["members"] = new[] { buyer.Customer.Id } };
        _server.EnqueueProblem(FakeSalesServer.PaymentRequestsBegin, 409, "members-stale", members);
        _server.EnqueueProblem(FakeSalesServer.PaymentRequestsBegin, 409, "stale-total", StaleTotal(180m));
        _server.EnqueueProblem(FakeSalesServer.PaymentRequestsBegin, 409, "members-stale", members);

        var outcome = await RequestAsync(pc, buyer.Customer, Scope(live));

        outcome.Result.Should().Be(PaymentRequestResult.Blocked);
        outcome.Message.Should().Be("Müşteri grupları eşitleniyor; biraz sonra tekrar dene.");
        Begins().Should().HaveCount(3);
        pc.Launcher.LaunchedUrls.Should().BeEmpty();
    }

    public static TheoryData<string> RetriedRejections => new() { "members-stale", "stale-total", "settings-stale" };

    [Theory]
    [MemberData(nameof(RetriedRejections))]
    public async Task Yeniden_denemede_onay_ozeti_ve_Id_listesi_istek_govdelerinde_ayni(string code)
    {
        var pc = await PcAsync();
        var live = pc.StartStream();
        var buyer = await BuyerAsync(pc, live, "alici_onay", 120m);
        var digest = FakeSalesServer.PriorsDigest.Compute([Guid.NewGuid()]);   // sunucunun biçimi (#523)
        var priors = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var confirmation = PaymentLeaseConfirmation.Create(digest, priors);
        _server.EnqueueProblem(FakeSalesServer.PaymentRequestsBegin, 409, code, code switch
        {
            "members-stale" => new Dictionary<string, object?> { ["members"] = new[] { buyer.Customer.Id } },
            "settings-stale" => SettingsStale(version: 4, fee: 60m),
            _ => StaleTotal(120m),
        });

        var outcome = await RequestAsync(pc, buyer.Customer, Scope(live),
            new HashSet<string>(StringComparer.Ordinal) { confirmation.Token });

        outcome.Result.Should().Be(PaymentRequestResult.Opened);
        var begins = Begins();
        begins.Should().HaveCount(2);
        foreach (var begin in begins)
        {
            begin.GetProperty("confirmedPriorsDigest").GetString().Should().Be(digest, "özet aynen yankılanır (#523)");
            begin.GetProperty("confirmedPriorIds").EnumerateArray().Select(i => i.GetGuid()).Should().Equal(priors);
        }
    }

    private static Dictionary<string, object?> SettingsStale(int version, decimal fee) => new()
    {
        ["current"] = new
        {
            version, freeShippingThreshold = 1000m, fee, updatedAt = DateTimeOffset.UtcNow, updatedBy = "cli",
        },
    };

    [Fact]
    public async Task Settings_stale_onbellek_currentten_kira_yerinde_yeni_surum_ve_ucretle_yinelenir_mesaj_yeni_ucretle()
    {
        var pc = await PcAsync();
        var live = pc.StartStream();
        var buyer = await BuyerAsync(pc, live, "alici_kargo", 120m);
        _server.EnqueueProblem(FakeSalesServer.PaymentRequestsBegin, 409, "settings-stale", SettingsStale(4, 60m));
        var mark = _server.RequestLog.Count;

        var outcome = await RequestAsync(pc, buyer.Customer, Scope(live));

        outcome.Result.Should().Be(PaymentRequestResult.Opened);
        var cache = pc.State.GetShippingCache();
        cache.Version.Should().Be(4, "409'un current'ı lisans geneli kargo önbelleğine yazıldı");
        cache.FreeShippingThreshold.Should().Be(1000m);
        cache.ShippingFee.Should().Be(60m);
        var begins = Begins();
        begins.Should().HaveCount(2);
        begins[0].GetProperty("shippingSettingsVersion").GetInt32().Should().Be(3);
        begins[0].GetProperty("totalBeforeBalance").GetDecimal().Should().Be(120m + 50m);
        begins[1].GetProperty("shippingSettingsVersion").GetInt32().Should().Be(4);
        begins[1].GetProperty("totalBeforeBalance").GetDecimal().Should().Be(120m + 60m, "kargo yeniden hesaplandı");
        begins[1].GetProperty("recordId").GetGuid().Should().NotBe(begins[0].GetProperty("recordId").GetGuid());
        var raw = _server.RequestLog.Skip(mark).Where(e => !e.EndsWith("/me/licenses", StringComparison.Ordinal)).ToList();
        var first = raw.FindIndex(e => e.EndsWith("/payment-requests/begin", StringComparison.Ordinal));
        raw[first + 1].Should().EndWith("/payment-requests/begin",
            "kira yerinde yinelendi: kişi bırakılmadı, arada tur ya da ön gönderim yok");
        Uri.UnescapeDataString(pc.Launcher.LaunchedUrls.Should().ContainSingle().Subject)
            .Should().Contain("Kargo: 60,00 TL", "mesaj yeni ücretle");
        _server.Calls(FakeSalesServer.PaymentRequestsFinish).Single().Json.GetProperty("netAmount").GetDecimal()
            .Should().Be(180m);
    }

    [Fact]
    public async Task Settings_stale_surerse_satir_ici_engel_mesaj_yok()
    {
        var pc = await PcAsync();
        UseCloudApi(pc);
        var live = pc.StartStream();
        var buyer = await BuyerAsync(pc, live, "alici_kargo_surer", 120m);
        _server.EnqueueProblem(FakeSalesServer.PaymentRequestsBegin, 409, "settings-stale", SettingsStale(4, 60m));
        _server.EnqueueProblem(FakeSalesServer.PaymentRequestsBegin, 409, "settings-stale", SettingsStale(5, 70m));

        var outcome = await RequestAsync(pc, buyer.Customer, Scope(live));

        outcome.Result.Should().Be(PaymentRequestResult.Blocked);
        outcome.Message.Should().Be("Kargo ayarı bu arada değişti; biraz sonra tekrar dene.");
        Begins().Should().HaveCount(2, "yerinde bir kez yinelenir");
        pc.State.GetShippingCache().Version.Should().Be(5, "her settings-stale önbelleği sunucunun değeriyle yeniler");
        _server.Calls(FakeSalesServer.WhatsAppSend).Should().BeEmpty();
        _server.Calls(FakeSalesServer.PaymentRequestsFinish).Should().BeEmpty();
    }

    [Fact]
    public async Task Mode_off_Off_turu_beklenir_uzak_etiketler_silinir_mesaj_yerel_toplamla_ve_kirasiz_kayitla_gider()
    {
        var pc = await PcAsync();
        var live = pc.StartStream();
        var buyer = await BuyerAsync(pc, live, "alici_kapandi", 120m);
        SeedRemoteOrder(buyer.Customer, 90m);
        await pc.SettleAsync();
        pc.RemoteRows.Should().BeGreaterThan(0, "PC-B'nin etiketi mod On'da indi");
        _server.Mode = 0;                                               // sales-sync disable
        _server.ModeChangedAt = DateTimeOffset.UtcNow;

        var outcome = await RequestAsync(pc, buyer.Customer, "cumulative");

        outcome.Result.Should().Be(PaymentRequestResult.Opened, "Off turundan sonra istek mod Off yolunda yeniden kuruldu");
        Begins().Should().ContainSingle("ikinci begin yok: mod Off'ta kira yok")
            .Which.GetProperty("productTotal").GetDecimal().Should().Be(210m, "ilk koşu uzak etiketi de saymıştı");
        pc.State.GetMode().Should().Be(0);
        pc.RemoteRows.Should().Be(0, "Off turu uzak etiketleri sildi");
        var record = pc.PendingPaymentRecords.List().Where(r => r.Kind == PendingPaymentRecordKinds.Record)
            .Select(PendingPaymentRecordCodec.DecodeRecord).Should().ContainSingle("W3-06: mod Off'ta kirasız kayıt").Subject;
        record.NetAmount.Should().Be(120m + 50m, "mesaj yerel toplamla gitti");
        record.OrderIds.Should().Equal(Guid.ParseExact(buyer.Labels[0].Id, "N"));
        pc.Launcher.LaunchedUrls.Should().ContainSingle();
        _server.Calls(FakeSalesServer.PaymentRequestsFinish).Should().BeEmpty("ret kira yazmaz");
    }

    [Fact]
    public async Task Mode_off_Off_turu_suresinde_bitmezse_engel_mesaj_yok()
    {
        var pc = await PcAsync();
        var live = pc.StartStream();
        var buyer = await BuyerAsync(pc, live, "alici_kapandi_bekler", 120m);
        pc.Lease.ModeOffRoundTimeout = TimeSpan.FromMilliseconds(300);
        _server.Mode = 0;
        _server.ModeChangedAt = DateTimeOffset.UtcNow;
        var hold = _server.Hold(FakeSalesServer.Changes);
        try
        {
            var outcome = await RequestAsync(pc, buyer.Customer, Scope(live));

            outcome.Result.Should().Be(PaymentRequestResult.Blocked);
            outcome.Message.Should().Be("Senkron kapatıldı; ekran yenileniyor.");
            hold.Arrived.IsCompleted.Should().BeTrue("Off turunun son akışı başladı ve takıldı");
            Begins().Should().ContainSingle();
            pc.State.GetMode().Should().Be(1, "Off turu bitmedi");
            pc.Launcher.LaunchedUrls.Should().BeEmpty();
            pc.PendingPaymentRecords.List().Should().NotContain(r => r.Kind == PendingPaymentRecordKinds.Record);
        }
        finally { hold.Release(); }
        await WaitUntilAsync(() => pc.State.GetMode() == 0);           // tur iptal edilmedi, arka planda bitti
    }

    /// <summary>Etiketi bu bilgisayarda olmayan, telefonlu GUID'li alıcı; PC-B'nin devredilmiş yayınında 500'lük basılmış
    /// etiketi var (akıştan indi). Başka bilgisayarın bu kişiye teslim edilmiş isteğinin düşümü sunucuda.</summary>
    private async Task<(Customer Buyer, string RemoteSession)> RemoteOnlyBuyerAsync(SalesPc pc, string username)
    {
        GuidCustomer(pc, "tiktok", username);
        var buyer = WithPhone(pc, username);
        var (session, _) = SeedRemote(buyer, 500m);
        await pc.SettleAsync();
        pc.RemoteRows.Should().BeGreaterThan(0, "PC-B'nin etiketi mod On'da indi");
        _server.BalancePreview = 100m;
        _server.ScopeDeductions[Guid.ParseExact(buyer.Id, "N")] = new FakeSalesServer.ScopeDeduction(Guid.NewGuid(), 100m,
            500m + 50m, DateTimeOffset.UtcNow.AddMinutes(-5));
        return (buyer, session.ToString("N"));
    }

    [Fact]
    public async Task Mode_off_sonrasi_uzak_yayin_kapsami_yeniden_kurulmaz_mesaj_bakiye_ve_kayit_yok()
    {
        // Off turu PC-B'nin yayınını ve etiketlerini sildi: "yerel toplamla yeniden kurma" 0 TL + kargo isteği olurdu, kapsam
        // uzlaştırması başka bilgisayarın düşümünü benimseyip tutar farkıyla geri alırdı.
        var pc = await PcAsync();
        var (buyer, remote) = await RemoteOnlyBuyerAsync(pc, "alici_uzak_yayin");
        _server.Mode = 0;                                               // sales-sync disable
        _server.ModeChangedAt = DateTimeOffset.UtcNow;
        var mark = _server.RequestLog.Count;

        var outcome = await RequestAsync(pc, buyer, Scope(remote));

        outcome.Result.Should().Be(PaymentRequestResult.Blocked);
        outcome.Message.Should().Be("Senkron kapatıldı; bu yayın bu bilgisayarda değil.");
        pc.State.GetMode().Should().Be(0, "Off turu bitti");
        Begins().Should().ContainSingle("ilk koşunun begin'i; yeniden kurma kirasız ve mesajsız durdu")
            .Which.GetProperty("productTotal").GetDecimal().Should().Be(500m);
        Log(mark).Should().NotContain("bakiye", "bakiye uçlarına (scope, apply, reverse) hiç gidilmedi");
        _server.Calls(FakeSalesServer.BalanceReverseEndpoint).Should().BeEmpty();
        pc.Launcher.LaunchedUrls.Should().BeEmpty();
        pc.PendingPaymentRecords.List().Should().NotContain(r => r.Kind == PendingPaymentRecordKinds.Record);
        pc.Jobs.GetPersonScopeJobs([buyer.Id], Scope(remote)).Jobs.Should().BeEmpty("iş açılmadı");
    }

    [Fact]
    public async Task Mode_off_sonrasi_kisinin_butun_etiketleri_uzaktiysa_kumulatif_yeniden_kurulmaz()
    {
        var pc = await PcAsync();
        var (buyer, _) = await RemoteOnlyBuyerAsync(pc, "alici_uzak_kumulatif");
        _server.Mode = 0;
        _server.ModeChangedAt = DateTimeOffset.UtcNow;
        var mark = _server.RequestLog.Count;

        var outcome = await RequestAsync(pc, buyer, "cumulative");

        outcome.Result.Should().Be(PaymentRequestResult.Blocked);
        outcome.Message.Should().Be("Senkron kapatıldı; ekran yenileniyor.");
        Begins().Should().ContainSingle().Which.GetProperty("productTotal").GetDecimal().Should().Be(500m);
        pc.RemoteRows.Should().Be(0, "Off turu uzak etiketleri sildi: yerel toplam 0");
        Log(mark).Should().NotContain("bakiye");
        pc.Launcher.LaunchedUrls.Should().BeEmpty("operatörün gördüğü 500 TL'nin hiçbir parçası bu bilgisayarda değil");
        pc.PendingPaymentRecords.List().Should().NotContain(r => r.Kind == PendingPaymentRecordKinds.Record);
    }

    [Fact]
    public async Task Mod_Offta_acik_kalmis_ekranin_silinmis_uzak_yayin_kapsamina_istek_gitmez()
    {
        // Off turundan sonra açık kalmış rapor ya da arama listesi (son yayın uzak olabilir) silinmiş yayını gösteriyor:
        // begin olmadan doğrudan mod Off yolu da aynı engelle durur.
        var pc = await PcAsync();
        var (buyer, remote) = await RemoteOnlyBuyerAsync(pc, "alici_bayat_ekran");
        _server.Mode = 0;
        _server.ModeChangedAt = DateTimeOffset.UtcNow;
        await pc.SettleAsync();
        pc.State.GetMode().Should().Be(0, "Off turu bitti");
        var mark = _server.RequestLog.Count;

        var outcome = await RequestAsync(pc, buyer, Scope(remote));

        outcome.Result.Should().Be(PaymentRequestResult.Blocked);
        outcome.Message.Should().Be("Senkron kapatıldı; bu yayın bu bilgisayarda değil.");
        Log(mark).Should().BeEmpty("mod Off: begin yok; bakiye uçlarına da gidilmedi");
        pc.Launcher.LaunchedUrls.Should().BeEmpty();
    }

    // ── kapsam uzlaştırmasında sunucu üyeleri (W3-08) ─────────────────────

    [Fact]
    public async Task Sunucu_uyelerinde_yerelde_bilinmeyen_uyenin_dusumu_benimsenir_ikinci_dusum_yok()
    {
        // §6.4 "Kapsam uzlaştırması": grup senkronu gecikmiş bilgisayar başka üyeye yazılmış düşümü kiranın sunucu
        // üyeleriyle görür ve kendi çapasından ikinci düşüm yazmaz.
        var pc = await PcAsync();
        _server.BalancePreview = 50m;                                   // benimsenmeseydi apply ikinci düşümü yazardı
        var live = pc.StartStream();
        var buyer = await BuyerAsync(pc, live, "alici_uzlasma", 120m);
        var unknown = Guid.NewGuid();
        pc.Customers.GetById(unknown.ToString("N")).Should().BeNull("üye yalnız sunucuda");
        _server.ExtraBeginMembers.Add(unknown.ToString("N"));
        var transaction = Guid.NewGuid();
        _server.ScopeDeductions[unknown] = new FakeSalesServer.ScopeDeduction(transaction, 50m, 120m + 50m,
            DateTimeOffset.UtcNow.AddMinutes(-5));
        var scope = Scope(live);

        (await RequestAsync(pc, buyer.Customer, scope)).Result.Should().Be(PaymentRequestResult.Opened);

        var asked = _server.Calls(FakeSalesServer.BalanceScopeEndpoint).Select(c => c.RawJson).ToList();
        asked.Should().Contain(q => q.Contains(Guid.ParseExact(buyer.Customer.Id, "N").ToString("D")));
        asked.Should().Contain(q => q.Contains(unknown.ToString("D")), "/scope sunucu üyesine de soruldu");
        _server.Calls(FakeSalesServer.BalanceApplyEndpoint).Should().BeEmpty("düşüm benimsendi: ikinci düşüm yok");
        var job = pc.Jobs.GetPersonScopeJobs([buyer.Customer.Id], scope).Jobs.Should().ContainSingle().Subject;
        job.ApplyKey.Should().Be(transaction);
        job.AppliedAmount.Should().Be(50m);
        job.CustomerId.Should().Be(buyer.Customer.Id,
            "iş yerelde bilinmeyen üyeye taşınmaz — yerel kişi onu bulmaya devam eder");
        var finish = _server.Calls(FakeSalesServer.PaymentRequestsFinish).Single().Json;
        finish.GetProperty("netAmount").GetDecimal().Should().Be(120m + 50m - 50m);
        finish.GetProperty("appliedAmount").GetDecimal().Should().Be(50m);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("koşul 10 sn içinde tutmadı");
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task Yayin_raporunda_engel_satir_ici_seritte_acilir_pencere_yok()
    {
        var pc = await PcAsync();
        var live = pc.StartStream();
        await BuyerAsync(pc, live, "alici_rapor", 120m);
        _server.EnqueueStatus(FakeSalesServer.PaymentRequestsBegin, 404);
        var dialogs = new FakeDialogService();
        var vm = new StreamReportViewModel(pc.LabelRepo, pc.Sessions, new GiveawayRepository(pc.Db), pc.Customers,
            pc.Payments, dialogs);
        vm.Load(live);

        await vm.OpenWhatsAppCommand.ExecuteAsync(vm.TopCustomers.Should().ContainSingle().Subject);

        vm.PaymentProblem.Should().Be("Sunucu güncelleniyor; biraz sonra tekrar dene.");
        dialogs.Shown.Should().BeEmpty();
        dialogs.InfosShown.Should().BeEmpty();
        dialogs.ErrorsShown.Should().BeEmpty();
    }

    [Fact]
    public async Task Duzeltilmis_istek_listesinde_engel_elle_bakilmali_ve_nedeni()
    {
        var pc = await PcAsync();
        var live = pc.StartStream();
        var buyer = await BuyerAsync(pc, live, "alici_duzeltme", 120m);
        _server.EnqueueProblem(FakeSalesServer.PaymentRequestsBegin, 409, "content-conflict");
        var corrected = new OrderDeck.App.Services.PriceCorrection.CorrectedPaymentRequests(
            pc.Customers, pc.LabelRepo, pc.Jobs, pc.Shipping, pc.Payments);
        var candidate = new OrderDeck.App.Services.PriceCorrection.CorrectedRequestCandidate(
            buyer.Customer.Id, "Örnek", Scope(live), 100m, DateTimeOffset.UtcNow, false, 170m);

        var outcome = (await corrected.ResendAsync(live, DateTime.Now, [candidate])).Should().ContainSingle().Subject;

        outcome.Status.Should().Be(OrderDeck.App.Services.PriceCorrection.ResendStatus.Manual);
        outcome.Note.Should().Be("İşlem kaydedilemedi (iç hata); tekrar dene.");
        pc.Launcher.LaunchedUrls.Should().BeEmpty();
    }

    /// <summary>GUID'siz (ham Id'li) telefonlu müşteri: bakiye işi açılmaz.</summary>
    private static Customer GuidlessCustomer(SalesPc pc, string username)
    {
        var customer = new Customer($"tiktok:{username}_{Guid.NewGuid():N}", "tiktok", username, "Örnek", null,
            1, 1, false, null, null, 0, 0m, null, null, TestPhone.NewE164());
        pc.Customers.Insert(customer);
        return customer;
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
