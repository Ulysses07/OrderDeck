using System.Runtime.ExceptionServices;
using System.Windows.Threading;
using Dapper;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
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
using OrderDeck.Tests.Sync;
using OrderDeck.Tests.TestHelpers;

namespace OrderDeck.Tests.App;

/// <summary>
/// Mod On açılışı kabukta (spec 2026-10-13 §8.4, §5.4, §5.7; W2-10): açılış beklerken satış satırı "Senkron açılıyor;
/// bekleyen gönderim var" der; ilk yetişmede kendi etiketlerinde sunucunun değeri alındıysa gönderim şeridine bir kez not
/// düşer (açılır pencere yok); yayın başlatmanın Faz 0 sorusu mod On'da satış akışının ilk yetişmesini de bekler — aynı
/// soru. Uçtan uca: <see cref="FakeSalesServer"/> → satış akışı turu (kip adımıyla) → VM.
/// </summary>
public sealed class MainShellOpeningRoundTests : IDisposable
{
    private const string Faz0Title = "Güncelleniyor";

    private const string Faz0Message =
        "Müşteri bilgilerindeki son değişiklikler henüz inmedi (diğer bilgisayarlar / müşteri uygulaması). " +
        "İnternet yoksa yine de başlatabilirsin. Yayını başlatayım mı?";

    private sealed class License(string key) : ICurrentLicenseProvider
    {
        public string? CurrentLicenseKey { get; set; } = key;
    }

    private sealed class Hardware(string id) : IHardwareIdProvider
    {
        public string GetHardwareId() => id;
        public string? GetLegacyHardwareId() => null;
    }

    private readonly FakeSalesServer _server = new();
    private readonly string _settingsPath = Path.Combine(Path.GetTempPath(), $"od-shell-opening-{Guid.NewGuid():N}.json");
    private SalesChangesPullService? _pull;

    public void Dispose()
    {
        _server.Dispose();
        try { File.Delete(_settingsPath); } catch { /* en iyi çaba */ }
    }

    /// <summary>Kabuk, kip adımı bağlı gerçek satış akışı turuyla (açılışın gönderim servisleri dahil).</summary>
    private MainShellTestHarness.Harness Build(SyncStatusTracker? syncStatus = null) =>
        MainShellTestHarness.Build(syncStatus: syncStatus, salesPull: db =>
        {
            var license = new License(_server.Lisans);
            var identity = new ReplicaIdentityRepository(db);
            var stateRepo = new SalesSyncStateRepository(db);
            var envelope = new SalesSyncEnvelope(identity, stateRepo, new Hardware(Guid.NewGuid().ToString("N")), license,
                NullLogger<SalesSyncEnvelope>.Instance);
            var api = new LicenseApiClient(
                new HttpClient(_server, disposeHandler: false) { BaseAddress = new Uri("https://test.local") },
                new LicenseTokenStore());
            var clock = new SystemClock();
            var busy = new CustomerBusySet();
            var cursors = new SyncCursorRepository(db);
            var customerSync = new CustomerSyncRepository(db, busy);
            var customerPush = new WpfCustomerProjectionSyncService(api, customerSync, cursors, license, clock,
                NullLogger<WpfCustomerProjectionSyncService>.Instance);
            var state = new SalesSyncStateService(api, envelope, identity, stateRepo, license, () => new AppSettings(),
                NullLogger<SalesSyncStateService>.Instance);
            var push = new SessionOrderSyncService(api, new SessionRepository(db), new LabelRepository(db), license,
                envelope, new HandoverGate(), new SalesPushApply(db), clock, NullLogger<SessionOrderSyncService>.Instance);
            var store = new SettingsStore(_settingsPath);
            var shipments = new ShipmentSyncService(api, new ShipmentRepository(db), store, store.Load(), license,
                envelope, clock, NullLogger<ShipmentSyncService>.Instance, salesState: stateRepo);
            var payments = new PaymentSyncService(api, new PaymentRepository(db), cursors, license, clock,
                NullLogger<PaymentSyncService>.Instance);
            var coordinator = new SalesModeCoordinator(state, stateRepo, envelope,
                new SalesOpeningFlush(new PaymentRecordSender(api, new PendingPaymentRecordRepository(db), envelope, license,
                    clock, NullLogger<PaymentRecordSender>.Instance), customerPush, push, shipments, payments, stateRepo), clock,
                NullLogger<SalesModeCoordinator>.Instance);
            _pull = new SalesChangesPullService(api, state, stateRepo, envelope, new SalesFeedRepository(db, busy),
                new SalesFeedFailureRepository(db), new RemoteLiveSessionRepository(db), cursors, new LabelRepository(db),
                customerPush, push,
                new CustomerChangesPullService(api, new CustomerRepository(db), customerSync, cursors, customerPush,
                    license, clock, new SyncStatusTracker(), NullLogger<CustomerChangesPullService>.Instance),
                license, clock, NullLogger<SalesChangesPullService>.Instance,
                sessions: new SessionRepository(db), modeCoordinator: coordinator);
            return _pull;
        });

    /// <summary>Gövdeyi KENDİ Dispatcher döngüsü olan STA iş parçacığında koşturur (bkz.
    /// <c>MainShellSalesFeedTests.RunOnOwnDispatcher</c>): olay arka plandan dispatcher'a taşınır.</summary>
    private static void RunOnOwnDispatcher(Func<Task> body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try { await body(); }
                catch (Exception ex) { failure = ex; }
                finally { dispatcher.InvokeShutdown(); }
            }));
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(60)).Should().BeTrue("STA dispatcher koşusu zaman aşımına uğramamalı");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static void Exec(IDbConnectionFactory db, string sql, object? param = null)
    {
        using var c = db.Open();
        c.Execute(sql, param);
    }

    private static string Customer(IDbConnectionFactory db)
    {
        var id = Guid.NewGuid().ToString("N");
        new CustomerRepository(db).Insert(new Customer(id, "tiktok", "alici_" + id[..8], "Takma Ad", null, 1, 1, false,
            null, null, 0, 0m, null, null, null));
        return id;
    }

    private void ServerModeOn()
    {
        _server.Mode = 1;
        _server.ModeChangedAt = DateTimeOffset.UtcNow;
    }

    // ── durum satırı ──────────────────────────────────────────────────────

    [Fact]
    public async Task Acilis_beklerken_satis_satiri_senkron_aciliyor_der_acilinca_akisin_satirina_doner()
    {
        using var h = Build();
        var customer = Customer(h.Db);
        new ShipmentRepository(h.Db).Insert(new Shipment(Guid.NewGuid().ToString("N"), customer, ShipmentStatus.Pending,
            1000, null, null, 40m));
        ServerModeOn();
        _server.EnqueueStatus(FakeSalesServer.ShipmentsSync, 500);
        h.Vm.SalesSyncStatusText.Should().BeEmpty("ön koşul: mod Off'ta satır yok");

        (await Task.Run(() => _pull!.TourOnceAsync(CancellationToken.None))).Should().Be(SalesPullOutcome.Opening);
        h.Vm.ApplySalesChanged(new SalesChangedEventArgs([], [], []));

        h.Vm.SalesSyncStatusText.Should().Be("Senkron açılıyor; bekleyen gönderim var");
        h.Vm.IsSalesSyncHealthy.Should().BeFalse();
        h.Vm.SalesSyncStatusTooltip.Should().Contain(SalesCommandMessages.OpeningPendingAdvice);

        (await Task.Run(() => _pull!.TourOnceAsync(CancellationToken.None))).Should().Be(SalesPullOutcome.CaughtUp);
        h.Vm.ApplySalesChanged(new SalesChangedEventArgs([], [], []));

        h.Vm.SalesSyncStatusText.Should().StartWith("Satış: güncel ✓");
        h.Dialogs.Shown.Should().BeEmpty("açılır pencere yok");
    }

    // ── ilk yetişmenin şeridi ─────────────────────────────────────────────

    [Fact]
    public void Ilk_yetismede_kendi_etiketinde_sunucunun_degeri_alindiysa_serit_bir_kez_not_duser()
        => RunOnOwnDispatcher(async () =>
    {
        using var h = Build();
        var customer = Customer(h.Db);
        var session = Guid.NewGuid().ToString("N");
        new SessionRepository(h.Db).Insert(new StreamSession(session, "Önceki yayın", 100, 200, ["tiktok"], null,
            SyncedAt: 300));
        Exec(h.Db, "UPDATE StreamSession SET ReleasedAt = 250 WHERE Id = @session", new { session });
        var label = Guid.NewGuid().ToString("N");
        new LabelRepository(h.Db).Insert(new Label(label, session, customer, "tiktok", "alici", "yorum", "B2", 40m, 120,
            150, SyncedAt: 400));
        var replica = new ReplicaIdentityRepository(h.Db).GetReplicaId()!.Value;
        _server.SeedSession(Guid.ParseExact(session, "N"), DateTimeOffset.FromUnixTimeSeconds(100),
            DateTimeOffset.FromUnixTimeSeconds(200), owner: replica, releasedAt: DateTimeOffset.FromUnixTimeSeconds(250));
        _server.SeedOrder(new FakeSalesServer.OrderBody(Guid.ParseExact(label, "N"), Guid.ParseExact(session, "N"),
            customer, "tiktok", "alici", null, "yorum", "B2", 55m, DateTimeOffset.FromUnixTimeSeconds(120),
            DateTimeOffset.FromUnixTimeSeconds(150), null, null, false, false, false, null, null, null, null, null, null,
            null, null, null), version: 2, origin: replica);
        ServerModeOn();

        (await Task.Run(() => _pull!.TourOnceAsync(CancellationToken.None))).Should().Be(SalesPullOutcome.CaughtUp);
        await Dispatcher.Yield(DispatcherPriority.Background);

        h.Vm.SalesPushWarning.Should().Be(
            "1 etikette sunucudaki değer bu bilgisayardakinden farklıydı; sunucunun değeri alındı.");
        h.Dialogs.Shown.Should().BeEmpty("açılır pencere yok: şerit, Tamam ile kapanır");
        h.Vm.DismissSalesPushWarningCommand.Execute(null);

        (await Task.Run(() => _pull!.TourOnceAsync(CancellationToken.None))).Should().Be(SalesPullOutcome.CaughtUp);
        await Dispatcher.Yield(DispatcherPriority.Background);
        h.Vm.SalesPushWarning.Should().BeNull("açılış başına bir kez");
        _server.Faults.Should().BeEmpty();
    });

    // ── Faz 0 ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Mod_Onda_satis_akisinin_ilk_yetismesi_bitmeden_yayin_baslatmak_ayni_soruyu_sorar()
    {
        var tracker = new SyncStatusTracker();
        using var h = Build(tracker);
        tracker.MarkPullSucceeded(DateTimeOffset.UtcNow, h.LicenseKey!);       // müşteri akışı yetişti
        Exec(h.Db, "UPDATE SalesSyncState SET Mode = 1, FirstCatchUpAt = NULL WHERE Id = 1");
        h.Dialogs.ConfirmResult = _ => true;
        await h.Vm.EndStreamCommand.ExecuteAsync(null);
        h.Dialogs.ConfirmResult = title => title != Faz0Title;

        h.Vm.StartStreamCommand.Execute(null);

        h.Dialogs.Confirmations.Should().ContainSingle(c => c.Title == Faz0Title).Which.Message.Should().Be(Faz0Message,
            "mevcut soru (§5.7): satış akışının ilk yetişmesi de beklenir");
        h.Sessions.GetActive().Should().BeNull("operatör hayır dedi");

        Exec(h.Db, "UPDATE SalesSyncState SET FirstCatchUpAt = 123 WHERE Id = 1");
        h.Dialogs.Confirmations.Clear();
        h.Vm.StartStreamCommand.Execute(null);

        h.Dialogs.Confirmations.Should().NotContain(c => c.Title == Faz0Title);
        h.Sessions.GetActive().Should().NotBeNull();
    }

    [Fact]
    public async Task Mod_Offta_satis_akisi_yayin_baslatmayi_sorgulamaz()
    {
        var tracker = new SyncStatusTracker();
        using var h = Build(tracker);
        tracker.MarkPullSucceeded(DateTimeOffset.UtcNow, h.LicenseKey!);
        h.Dialogs.ConfirmResult = _ => true;
        await h.Vm.EndStreamCommand.ExecuteAsync(null);

        h.Vm.StartStreamCommand.Execute(null);

        h.Dialogs.Confirmations.Should().NotContain(c => c.Title == Faz0Title, "mod Off'ta ilk yetişme yok (bugünkü gibi)");
        h.Sessions.GetActive().Should().NotBeNull();
        _server.SalesRequestCount.Should().Be(0, "yayın başlatma ağı beklemez");
    }
}
