using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using OrderDeck.App.Services;
using OrderDeck.App.Services.Sync;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Sales;
using OrderDeck.Core.Sessions;
using OrderDeck.Core.Settings;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Core.Time;
using OrderDeck.Licensing;
using OrderDeck.Licensing.Api;
using OrderDeck.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.Tests.Sync;

/// <summary>
/// Kopya veritabanı ve yedekten geri yükleme (spec 2026-10-13 §8.7; W2-10): geri yüklenmiş veritabanının
/// <c>SalesSyncState</c>'i yedekten gelir — istemci <c>hello</c> ve <c>sales/state</c> ile kipi tazelemeden ve kip adımı
/// (<see cref="SalesModeCoordinator"/>) o kiple koşmadan hiçbir satış gönderimi yapmaz: oturum, etiket, kargo, geri
/// doldurma ve komut. Geri yüklenmemiş süreçte bu kapı yoktur. Sunucu <see cref="FakeSalesServer"/>; servisler gerçek.
/// </summary>
public sealed class RestoreSalesModeRefreshTests : IDisposable
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.FromUnixTimeSeconds(1_760_000_000L);

    private sealed class License(string key) : ICurrentLicenseProvider
    {
        public string? CurrentLicenseKey { get; set; } = key;
    }

    private sealed class Hardware(string id) : IHardwareIdProvider
    {
        public string GetHardwareId() => id;
        public string? GetLegacyHardwareId() => null;
    }

    private sealed class Clock : IClock
    {
        public long UnixNow() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    }

    private sealed class NoPrompt : IAppUpdatePrompt
    {
        public void CheckAndPrompt() { }
    }

    /// <summary>Bir bilgisayarın yığını (bir süreç): satış akışı turu (state + kip adımı), gönderim, geri doldurma, komut.</summary>
    private sealed class Pc
    {
        public SalesSyncEnvelope Envelope { get; }
        public SessionOrderSyncService Push { get; }
        public ShipmentSyncService Shipments { get; }
        public SalesBackfillService Backfill { get; }
        public SalesCommandClient Commands { get; }
        public SalesChangesPullService Pull { get; }

        public Pc(FakeSalesServer server, IDbConnectionFactory db, string settingsPath, string fingerprint)
        {
            var license = new License(server.Lisans);
            var identity = new ReplicaIdentityRepository(db);
            var stateRepo = new SalesSyncStateRepository(db);
            Envelope = new SalesSyncEnvelope(identity, stateRepo, new Hardware(fingerprint), license,
                NullLogger<SalesSyncEnvelope>.Instance);
            var http = new HttpClient(server, disposeHandler: false) { BaseAddress = new Uri("https://test.local") };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("OrderDeck-WPF/0.9.11");
            var api = new LicenseApiClient(http, new LicenseTokenStore());
            var clock = new Clock();
            var busy = new CustomerBusySet();
            var cursors = new SyncCursorRepository(db);
            var customerSync = new CustomerSyncRepository(db, busy);
            var gate = new HandoverGate();
            var state = new SalesSyncStateService(api, Envelope, identity, stateRepo, license, () => new AppSettings(),
                NullLogger<SalesSyncStateService>.Instance);
            Push = new SessionOrderSyncService(api, new SessionRepository(db), new LabelRepository(db), license, Envelope,
                gate, new SalesPushApply(db), clock, NullLogger<SessionOrderSyncService>.Instance);
            var store = new SettingsStore(settingsPath);
            Shipments = new ShipmentSyncService(api, new ShipmentRepository(db), store, store.Load(), license, Envelope,
                clock, NullLogger<ShipmentSyncService>.Instance, salesState: stateRepo);
            var payments = new PaymentSyncService(api, new PaymentRepository(db), cursors, license, clock,
                NullLogger<PaymentSyncService>.Instance);
            var customerPush = new WpfCustomerProjectionSyncService(api, customerSync, cursors, license, clock,
                NullLogger<WpfCustomerProjectionSyncService>.Instance);
            var customerPull = new CustomerChangesPullService(api, new CustomerRepository(db), customerSync, cursors,
                customerPush, license, clock, new SyncStatusTracker(), NullLogger<CustomerChangesPullService>.Instance);
            // AppHost gibi tek gönderici: geri doldurma turu ve açılışın 1. adımı aynı kapıdan.
            var paymentRecords = new PaymentRecordSender(api, new PendingPaymentRecordRepository(db), Envelope, license,
                clock, NullLogger<PaymentRecordSender>.Instance);
            Backfill = new SalesBackfillService(api, Envelope, state, Push, Shipments, paymentRecords,
                new SalesBackfillRepository(db), cursors, gate, license, clock, NullLogger<SalesBackfillService>.Instance);
            Commands = new SalesCommandClient(api, Envelope, stateRepo, new LabelRepository(db), new ShipmentRepository(db),
                new SalesFeedRepository(db, busy), new SalesConflictLogRepository(db), new NoPrompt(), license, clock,
                NullLogger<SalesCommandClient>.Instance);
            var coordinator = new SalesModeCoordinator(state, stateRepo, Envelope,
                new SalesOpeningFlush(paymentRecords, customerPush, Push, Shipments, payments, stateRepo), clock,
                NullLogger<SalesModeCoordinator>.Instance);
            Pull = new SalesChangesPullService(api, state, stateRepo, Envelope, new SalesFeedRepository(db, busy),
                new SalesFeedFailureRepository(db), new RemoteLiveSessionRepository(db), cursors, new LabelRepository(db),
                customerPush, Push, customerPull, license, clock, NullLogger<SalesChangesPullService>.Instance,
                sessions: new SessionRepository(db), modeCoordinator: coordinator);
        }

        /// <summary>Satış gönderimlerinin hepsi bir kez: oturum ve etiket, kargo, geri doldurma.</summary>
        public async Task PushEverythingAsync()
        {
            await Push.SyncOnceAsync();
            await Shipments.SyncOnceAsync();
            await Backfill.TourOnceAsync(CancellationToken.None);
        }
    }

    private static readonly string[] SalesWrites =
        ["/sessions/sync", "/orders/sync", "/shipments/sync", "/sales/backfill", "/sales/commands"];

    private readonly FakeSalesServer _server = new();
    private readonly InMemorySqlite _db = new();
    private readonly string _settingsPath = Path.Combine(Path.GetTempPath(), $"od-restore-{Guid.NewGuid():N}.json");
    private readonly string _fingerprint = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");

    public RestoreSalesModeRefreshTests() => new MigrationRunner(_db).Run();

    public void Dispose()
    {
        _db.Dispose();
        _server.Dispose();
        SqliteConnection.ClearAllPools();
        try { File.Delete(_settingsPath); } catch { /* en iyi çaba */ }
    }

    /// <summary>Geri yüklemeden sonraki açılış: kimlik döndü (önceki kimlik bekliyor), AppHost "bu süreçte geri
    /// yüklendi"yi okur.</summary>
    private Pc RestoredPc()
    {
        new ReplicaIdentityRepository(_db).Rotate();
        var pc = new Pc(_server, _db, _settingsPath, _fingerprint);
        pc.Envelope.CaptureRestoreAtBoot(markerApplied: false);
        return pc;
    }

    private void Exec(string sql, object? param = null)
    {
        using var c = _db.Open();
        c.Execute(sql, param);
    }

    private string LocalCustomer()
    {
        var id = Guid.NewGuid().ToString("N");
        new CustomerRepository(_db).Insert(new Customer(id, "tiktok", "alici_" + id[..8], "Takma Ad", null, 1, 1, false,
            null, null, 0, 0m, null, null, null));
        return id;
    }

    private int[] Indices(Func<string, bool> match) =>
        _server.RequestLog.Select((r, i) => (r, i)).Where(x => match(x.r)).Select(x => x.i).ToArray();

    private int[] SalesWriteIndices() => Indices(r => SalesWrites.Any(w => r.EndsWith(w, StringComparison.Ordinal)));

    // ── (g) state yanıtından ve kip adımından önce gönderim yok ──────────

    [Fact]
    public async Task Geri_yuklemeden_sonra_state_yaniti_ve_kip_adimi_gelmeden_oturum_etiket_kargo_ve_geri_doldurma_gitmez()
    {
        var customer = LocalCustomer();
        var session = Guid.NewGuid().ToString("N");
        new SessionRepository(_db).Insert(new StreamSession(session, "Yayın", T0.ToUnixTimeSeconds(),
            T0.ToUnixTimeSeconds() + 3600, ["tiktok"], null));
        new LabelRepository(_db).Insert(new Label(Guid.NewGuid().ToString("N"), session, customer, "tiktok", "alici",
            "yorum", "B2", 40m, T0.ToUnixTimeSeconds() + 60, T0.ToUnixTimeSeconds() + 120));
        new ShipmentRepository(_db).Insert(new Shipment(Guid.NewGuid().ToString("N"), customer, ShipmentStatus.Pending,
            T0.ToUnixTimeSeconds(), null, null, 40m));
        var pc = RestoredPc();
        pc.Envelope.IsRestoredInThisProcess.Should().BeTrue("ön koşul: bekleyen önceki kimlik açılışta okundu");

        await pc.PushEverythingAsync();
        SalesWriteIndices().Should().BeEmpty("hello'dan önce hiçbir satış gönderimi yok");

        // hello gider, state düşer: kip tazelenmedi.
        _server.EnqueueStatus(FakeSalesServer.State, 500);
        (await pc.Pull.TourOnceAsync(CancellationToken.None)).Should().Be(SalesPullOutcome.Failed);
        _server.Hellos.Should().ContainSingle();
        await pc.PushEverythingAsync();
        SalesWriteIndices().Should().BeEmpty("hello yetmez: state yanıtı ve kip adımı beklenir (§8.7)");
        pc.Envelope.ForPush().Should().BeNull();

        // Tam tur: state + kip adımı. Ondan sonra gönderimler gider.
        (await pc.Pull.TourOnceAsync(CancellationToken.None)).Should().Be(SalesPullOutcome.ModeOff);
        await pc.PushEverythingAsync();

        var writes = SalesWriteIndices();
        writes.Should().NotBeEmpty("kip tazelendikten sonra kuyruk gider");
        var okState = Indices(r => r.EndsWith("/sales/state", StringComparison.Ordinal)).Last();
        writes.Min().Should().BeGreaterThan(okState, "ilk satış gönderimi başarılı state yanıtından sonra");
        _server.Calls(FakeSalesServer.SessionsSync).Should().NotBeEmpty();
        _server.Calls(FakeSalesServer.OrdersSync).Should().NotBeEmpty();
        _server.Calls(FakeSalesServer.ShipmentsSync).Should().NotBeEmpty();
        _server.BackfillCalls.Should().NotBeEmpty();
        _server.Faults.Should().BeEmpty();
    }

    [Fact]
    public async Task Geri_yuklemeden_sonra_komut_kip_adimindan_once_gitmez()
    {
        // Yedek mod On döneminden: yerel kip 1, ilk yetişme bitmiş.
        Exec("UPDATE SalesSyncState SET Mode = 1, FirstCatchUpAt = @at WHERE Id = 1", new { at = T0.ToUnixTimeSeconds() });
        _server.Mode = 1;
        _server.MinClientVersion = "0.9.11";
        var session = Guid.NewGuid();
        _server.SeedSession(session, T0, T0.AddHours(1), owner: Guid.NewGuid(), ownerMachineName: "PC-B");
        _server.ReleaseSession(session);
        var customer = Guid.NewGuid();
        _server.SeedCustomer(customer, "tiktok", "uzak_alici", "Takma Ad");
        var order = Guid.NewGuid();
        _server.SeedOrder(new FakeSalesServer.OrderBody(order, session, customer.ToString("N"), "tiktok", "uzak_alici",
            "Takma Ad", "aldım", "A1", 150m, T0.AddMinutes(1), T0.AddMinutes(2), null, null, false, false, false, null,
            null, null, null, null, null, null, null, null), version: 1);
        var pc = RestoredPc();

        (await pc.Commands.SendAsync(pc.Commands.Cancel([order.ToString("N")], "musteri"))).Status
            .Should().Be(SalesCommandStatus.NotReady, "hello, state ve kip adımından önce komut gitmez");
        _server.EnqueueStatus(FakeSalesServer.State, 500);
        (await pc.Pull.TourOnceAsync(CancellationToken.None)).Should().Be(SalesPullOutcome.Failed);
        _server.Hellos.Should().ContainSingle("hello gitti, state düştü");
        (await pc.Commands.SendAsync(pc.Commands.Cancel([order.ToString("N")], "musteri"))).Status
            .Should().Be(SalesCommandStatus.NotReady, "hello yetmez: kip tazelenmedi (§8.7)");
        _server.CommandCalls.Should().BeEmpty();

        (await pc.Pull.TourOnceAsync(CancellationToken.None)).Should().Be(SalesPullOutcome.CaughtUp);
        var outcome = await pc.Commands.SendAsync(pc.Commands.Cancel([order.ToString("N")], "musteri"));

        outcome.Status.Should().Be(SalesCommandStatus.Applied);
        _server.CommandCalls.Should().ContainSingle();
        var okState = Indices(r => r.EndsWith("/sales/state", StringComparison.Ordinal)).Last();
        Indices(r => r.EndsWith("/sales/commands", StringComparison.Ordinal)).Single().Should().BeGreaterThan(okState);
        _server.Faults.Should().BeEmpty();
    }

    [Fact]
    public async Task Geri_yuklenmemis_surecte_gonderim_state_beklemez()
    {
        // Kapı yalnız geri yüklenmiş süreçte: bugünkü bilgisayar açılışta kuyruğunu hello'yu beklemeden gönderir.
        var session = Guid.NewGuid().ToString("N");
        new SessionRepository(_db).Insert(new StreamSession(session, "Yayın", T0.ToUnixTimeSeconds(), null, ["tiktok"],
            null));
        var pc = new Pc(_server, _db, _settingsPath, _fingerprint);
        pc.Envelope.CaptureRestoreAtBoot(markerApplied: false);

        pc.Envelope.IsRestoredInThisProcess.Should().BeFalse();
        await pc.Push.SyncOnceAsync();

        _server.Calls(FakeSalesServer.SessionsSync).Should().ContainSingle();
        _server.States.Should().BeEmpty();
    }
}
