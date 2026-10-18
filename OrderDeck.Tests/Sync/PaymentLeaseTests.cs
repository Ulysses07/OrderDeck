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
    public async Task On_gonderim_sirasi_bekleyen_komut_finish_musteri_satis_ve_sonra_odeme_akisi()
    {
        var pc = await PcAsync();
        UseCloudApi(pc);
        await HandedOverWithPendingPrintAsync(pc, "alici_kalinti");     // başka kişinin bekleyen komutu
        pc.PaymentRecords.EnqueueFinish(new PendingPaymentFinish(Guid.NewGuid(), PaymentRequestStates.Abandoned, 0m,
            null, false)).Should().BeTrue();
        var live = pc.StartStream();
        var buyer = await BuyerAsync(pc, live, "alici_canli", 200m);    // müşteri ve etiket kuyrukta
        var mark = _server.RequestLog.Count;

        (await RequestAsync(pc, buyer.Customer, Scope(live))).Result.Should().Be(PaymentRequestResult.Sent);

        Log(mark).Should().StartWith(new[] { "komut", "finish", "müşteri", "satış" },
            "§6.4-1: önce bekleyen komutlar, sonra bekleyen finish'ler, müşteri, oturum ve etiket");
        Log(mark).Should().EndWith(new[] { "bakiye", "mesaj" });
        pc.PendingCount.Should().Be(0);
        pc.PendingPaymentRecords.Count().Should().Be(0, "bekleyen finish ön gönderimde gitti");
        _server.Faults.Should().BeEmpty();
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
