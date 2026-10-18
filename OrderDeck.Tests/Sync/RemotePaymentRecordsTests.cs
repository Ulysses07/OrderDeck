using System.Net;
using System.Net.Http;
using System.Text;
using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using OrderDeck.App.Services;
using OrderDeck.App.Services.PriceCorrection;
using OrderDeck.App.Services.Sync;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Sales;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Licensing.Api;
using OrderDeck.Licensing.Api.Models;
using OrderDeck.Tests.TestHelpers;

namespace OrderDeck.Tests.Sync;

/// <summary>
/// Başka bilgisayarların ödeme kayıtlarının önbelleği (spec 2026-10-13 §6.4 "Teslim kayıtlarının okunması"; W3-10): AppHost
/// gibi kurulmuş gerçek servislerle (<see cref="SalesPc"/>) ve sahte sunucunun liste ucuyla (<see cref="FakeSalesServer"/>).
/// Geçersizleşme (finish sunucuya geçince, satış akışından değişiklik inince, kip kapanınca), geçersizleşmeden önce başlamış
/// okumanın yazılmaması ve fiyat düzeltmesinin toplu gönderiminin mod On'da begin sorusunu kendiliğinden onaylaması.
/// </summary>
public sealed class RemotePaymentRecordsTests : IDisposable
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

    private static readonly DateTimeOffset TenOClock = new(2026, 10, 18, 10, 0, 0, TimeSpan.Zero);

    /// <summary>Bilgisayarlar mod Off'ta bir tur döner, sonra sunucu modu açar (lisans geneli kargo: eşik 1000, ücret 50) ve
    /// hepsi açılış turunu bitirir.</summary>
    private async Task<SalesPc[]> PcsAsync(params string[] names)
    {
        var pcs = names.Select(n => SalesPc.New(_server, n, _owned, _files)).ToArray();
        foreach (var pc in pcs) (await pc.TourAsync()).Should().Be(SalesPullOutcome.ModeOff);
        _server.Mode = 1;
        _server.ModeChangedAt = DateTimeOffset.UtcNow;
        _server.Shipping = new FakeSalesServer.ShippingBody(Version: 3, FreeShippingThreshold: 1000m, Fee: 50m);
        foreach (var pc in pcs)
        {
            (await pc.SettleAsync()).Should().Be(SalesPullOutcome.CaughtUp, $"{pc.Name} mod On'u açtı");
            pc.State.GetMode().Should().Be(1);
        }
        return pcs;
    }

    private static Customer WithPhone(SalesPc pc, string username)
    {
        var id = pc.Customers.FindByPlatformAndUsername("tiktok", username)!.Id;
        pc.Customers.UpdatePhone(id, TestPhone.NewE164()).Should().Be(1);
        return pc.Customers.GetById(id)!;
    }

    private static RemotePaymentKey Key(SalesPc pc, string scope, string customerId) =>
        new(scope, pc.Customers.GetPaymentPerson(customerId));

    private FakeSalesServer.PaymentPrior Seed(string customerId, string scope, decimal net, int minute,
        IEnumerable<string> covers, string machine = "PC-A")
    {
        var at = TenOClock.AddMinutes(minute);
        var prior = new FakeSalesServer.PaymentPrior(Guid.NewGuid(), machine, [customerId], scope, net, net, at,
            at.AddSeconds(15), Unconfirmed: false, OrderIds: covers.Select(Guid.Parse).ToList());
        _server.AddPaymentPrior(prior);
        return prior;
    }

    private List<System.Text.Json.JsonElement> Begins() =>
        _server.Calls(FakeSalesServer.PaymentRequestsBegin).Select(c => c.Json).ToList();

    // ── toplu gönderim (#508 planı) ─────────────────────────────────────────

    [Fact]
    public async Task Duzeltilmis_istek_mod_Onda_PC_Anin_netini_gosterir_begin_sorusu_kendiliginden_onaylanir_mesaj_bir_kez()
    {
        // PC-A bu kişiden 400 TL istedi (kaydı sunucuda, siparişleri K1+K2). Bu bilgisayarda K1 düzeltildi: plan "son istenen"i
        // PC-A'nın netiyle gösterir; toplu gönderimde begin'in sorusunun son öncülü de o kayıt — tutar eşit, listedeki onay
        // sorunun onayıdır: ikinci begin onayı taşır, mesaj bir kez gider.
        var pc = (await PcsAsync("PC-B"))[0];
        var live = pc.StartStream();
        var labels = new[] { pc.Sell(live, "alici_duzelt", "K1", 250m), pc.Sell(live, "alici_duzelt", "K2", 100m) };
        await pc.PrintAsync(labels.Select(l => l.Id).ToArray());
        var buyer = WithPhone(pc, "alici_duzelt");
        var scope = $"session:{live}";
        var prior = Seed(buyer.Id, scope, 400m, 0, labels.Select(l => l.Id));
        (await pc.PaymentFollowUp.PrefetchAsync(live, pc.LabelRepo.GetByIds(labels.Select(l => l.Id).ToList()), force: true))
            .Should().BeTrue();

        var result = pc.Labels.CorrectPrices([labels[0].Id], 205m);
        var plan = pc.PaymentFollowUp.Plan(live, result.Corrected.Select(c => (c.Before, c.Delta)));

        var row = plan.ToResend.Should().ContainSingle().Subject;
        row.PriorAmount.Should().Be(400m, "son istek PC-A'nın");
        row.NewTotal.Should().Be(355m, "205 + 100 + lisans geneli kargo 50");
        var outcomes = await pc.PaymentFollowUp.ResendAsync(live, DateTime.Now, plan.ToResend);

        outcomes.Should().ContainSingle().Which.Status.Should().Be(ResendStatus.Opened);
        var begins = Begins();
        begins.Should().HaveCount(2, "soru bir kez, onaylı begin bir kez");
        begins[1].GetProperty("confirmedPriorIds").EnumerateArray().Select(i => i.GetGuid()).Should().Equal(prior.Id);
        begins[1].GetProperty("confirmedPriorsDigest").GetString()
            .Should().Be(FakeSalesServer.PriorsDigest.Compute([prior.Id]));
        pc.Launcher.LaunchedUrls.Should().ContainSingle("mesaj bir kez").Which.Should().Contain("355");
        _server.Faults.Should().BeEmpty();
    }

    // ── geçersizleşme ─────────────────────────────────────────────────────

    [Fact]
    public async Task Finish_sunucuya_gecince_onbellek_bosalir_sonraki_okuma_bu_kopyanin_yeni_kaydini_gorur()
    {
        var pc = (await PcsAsync("PC-B"))[0];
        var live = pc.StartStream();
        var label = pc.Sell(live, "alici_finish", "K1", 250m);
        await pc.PrintAsync(label.Id);
        var buyer = WithPhone(pc, "alici_finish");
        var scope = $"session:{live}";
        Seed(buyer.Id, scope, 250m, 0, [label.Id]);
        (await pc.RemotePayments.RefreshAsync([Key(pc, scope, buyer.Id)])).Should().BeTrue();
        pc.RemotePayments.Snapshot()!.For(scope, pc.Customers.GetPaymentPerson(buyer.Id)).Should().NotBeNull();

        pc.PaymentRecords.EnqueueFinish(new PendingPaymentFinish(Guid.NewGuid(), PaymentRequestStates.Delivered, 250m,
            null, false)).Should().BeTrue();
        (await pc.PaymentRecords.FlushAsync(CancellationToken.None)).Sent.Should().Be(1);

        pc.RemotePayments.Snapshot()!.For(scope, pc.Customers.GetPaymentPerson(buyer.Id))
            .Should().BeNull("finish sunucuya geçti: bu kopyanın yeni kaydı listede yerel işin yerini değiştirir");
    }

    [Fact]
    public async Task Satis_akisindan_degisiklik_inince_onbellek_bosalir()
    {
        var pcs = await PcsAsync("PC-A", "PC-B");
        var (a, b) = (pcs[0], pcs[1]);
        var session = a.StartStream();
        var label = a.Sell(session, "alici_akis", "K1", 120m);
        await a.PrintAsync(label.Id);
        var scope = "cumulative";
        var customerId = a.Customers.FindByPlatformAndUsername("tiktok", "alici_akis")!.Id;
        // B'de henüz bu kişi yok; önbelleğe tek kişilik anahtarla girer (anahtar üyelerdir).
        var person = new PaymentPerson(customerId, [new PaymentPersonMember(customerId, null, false)]);
        (await b.RemotePayments.RefreshAsync([new RemotePaymentKey(scope, person)])).Should().BeTrue();
        b.RemotePayments.Snapshot()!.For(scope, person).Should().NotBeNull("ön koşul: önbellekte");

        a.EndStream(session);
        for (var i = 0; i < 2; i++)
        {
            await a.BackgroundRoundAsync();
            await b.BackgroundRoundAsync();
        }

        b.Rows().Select(r => r.Id).Should().Contain(label.Id, "devredilen yayın B'ye indi (SalesChanged)");
        b.RemotePayments.Snapshot()!.For(scope, person).Should().BeNull("satış akışı değişikliği önbelleği boşalttı");
    }

    [Fact]
    public async Task Kip_kapaninca_goruntu_yok_ve_onbellek_bosalir()
    {
        var pc = (await PcsAsync("PC-B"))[0];
        var live = pc.StartStream();
        var label = pc.Sell(live, "alici_kip", "K1", 250m);
        await pc.PrintAsync(label.Id);
        var buyer = WithPhone(pc, "alici_kip");
        (await pc.RemotePayments.RefreshAsync([Key(pc, "cumulative", buyer.Id)])).Should().BeTrue();

        pc.Exec("UPDATE SalesSyncState SET Mode = 0 WHERE Id = 1");
        pc.RemotePayments.Snapshot().Should().BeNull("mod Off: bugünkü davranış");
        pc.Exec("UPDATE SalesSyncState SET Mode = 1 WHERE Id = 1");

        pc.RemotePayments.Snapshot()!.For("cumulative", pc.Customers.GetPaymentPerson(buyer.Id))
            .Should().BeNull("önceki mod On döneminin kayıtları yeniden kullanılmaz");
    }

    // ── geçersizleşmeden önce başlamış okuma ────────────────────────────────

    /// <summary>Lisans listesi ve <c>GET payment-requests</c>: liste isteği gelince <see cref="OnList"/> koşar, yanıt tek
    /// kayıttır.</summary>
    private sealed class ListHandler(Guid licenseId, string licenseKey) : HttpMessageHandler
    {
        public Action? OnList { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            string json;
            if (path == "/api/v1/me/licenses")
                json = $"[{{\"id\":\"{licenseId}\",\"licenseKey\":\"{licenseKey}\"}}]";
            else
            {
                OnList?.Invoke();
                json = $"{{\"records\":[{{\"id\":\"{Guid.NewGuid()}\",\"replicaId\":\"{Guid.NewGuid()}\",\"machine\":\"PC-A\","
                     + "\"kind\":\"record\",\"state\":\"delivered\",\"netAmount\":250,\"appliedAmount\":null,"
                     + "\"fullyFromBalance\":false,\"productTotal\":null,\"totalBeforeBalance\":null,"
                     + $"\"startedAt\":\"{TenOClock:O}\",\"finishedAt\":\"{TenOClock:O}\",\"orderIds\":[]}}]}}";
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class FixedLicense(string key) : ICurrentLicenseProvider
    {
        public string? CurrentLicenseKey => key;
    }

    [Fact]
    public async Task Gecersizlesmeden_once_baslamis_okumanin_yaniti_yazilmaz()
    {
        var db = new InMemorySqlite();
        _owned.Add(db);
        new MigrationRunner(db).Run();
        using (var c = db.Open()) c.Execute("UPDATE SalesSyncState SET Mode = 1 WHERE Id = 1");
        var licenseKey = $"lisans-{Guid.NewGuid():N}";
        var handler = new ListHandler(Guid.NewGuid(), licenseKey);
        _owned.Add(handler);
        var remote = new RemotePaymentRecords(
            new LicenseApiClient(new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri("https://test.local") },
                new LicenseTokenStore()),
            new FixedLicense(licenseKey), new SalesSyncStateRepository(db), new ReplicaIdentityRepository(db),
            new PendingPaymentRecordRepository(db), NullLogger<RemotePaymentRecords>.Instance);
        var person = new PaymentPerson("c1", [new PaymentPersonMember("c1", null, false)]);
        handler.OnList = remote.Invalidate;                  // yanıt yolda iken satış akışı değişiklik indirdi

        (await remote.RefreshAsync([new RemotePaymentKey("cumulative", person)])).Should().BeFalse();

        remote.Snapshot()!.For("cumulative", person).Should().BeNull("bayat yanıt geçersizleşmeden sonra yazılmaz");
        handler.OnList = null;
        (await remote.RefreshAsync([new RemotePaymentKey("cumulative", person)])).Should().BeTrue();
        remote.Snapshot()!.For("cumulative", person)!.Newest.Should().NotBeNull();
    }
}
