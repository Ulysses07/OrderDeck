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
/// Satış akışı kabukta (spec 2026-10-13 §5.4-6, §5.7 "Akış"; W2-03): arka plandaki akış turu
/// <see cref="SalesChangesPullService.SalesChanged"/> yayar; <c>MainShellViewModel</c> dispatcher üzerinden kuyruktaki
/// kopyayı, Basılanlar'ı ve sayaçları tazeler, satış akışı satırını yalnız mod On'da gösterir. Uçtan uca:
/// <see cref="FakeSalesServer"/> → servis → VM.
/// </summary>
public sealed class MainShellSalesFeedTests : IDisposable
{
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
    private SalesChangesPullService? _pull;
    private SalesSyncEnvelope? _envelope;

    public void Dispose() => _server.Dispose();

    private MainShellTestHarness.Harness Build() => MainShellTestHarness.Build(salesPull: db =>
    {
        var license = new License(_server.Lisans);
        var identity = new ReplicaIdentityRepository(db);
        var stateRepo = new SalesSyncStateRepository(db);
        _envelope = new SalesSyncEnvelope(identity, stateRepo, new Hardware(Guid.NewGuid().ToString("N")), license,
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
        _pull = new SalesChangesPullService(api,
            new SalesSyncStateService(api, _envelope, identity, stateRepo, license, () => new AppSettings(),
                NullLogger<SalesSyncStateService>.Instance),
            stateRepo, _envelope, new SalesFeedRepository(db, busy), new SalesFeedFailureRepository(db),
            new RemoteLiveSessionRepository(db), cursors, new LabelRepository(db), customerPush,
            new SessionOrderSyncService(api, new SessionRepository(db), new LabelRepository(db), license, _envelope,
                new HandoverGate(), new SalesPushApply(db), clock, NullLogger<SessionOrderSyncService>.Instance),
            new CustomerChangesPullService(api, new CustomerRepository(db), customerSync, cursors, customerPush, license,
                clock, new SyncStatusTracker(), NullLogger<CustomerChangesPullService>.Instance),
            license, clock, NullLogger<SalesChangesPullService>.Instance);
        return _pull;
    });

    /// <summary>Gövdeyi KENDİ Dispatcher döngüsü olan STA iş parçacığında koşturur (bkz.
    /// <c>MainShellSalesPushWarningTests.RunOnOwnDispatcher</c>): olay arka plandan dispatcher'a taşınır.</summary>
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

    [Fact]
    public void Akistan_inen_degisiklik_kuyruk_kopyasini_ve_satis_satirini_dispatcher_uzerinden_tazeler()
        => RunOnOwnDispatcher(async () =>
    {
        using var h = Build();
        // Bu bilgisayarın canlı yayını başka bilgisayardan bitirildi (force-end) ve orada kuyruktaki etiketin kod
        // fiyatı düzeltildi: akış etiketi yeni fiyatla indirir, yayın yerel kalır (M18).
        MainShellTestHarness.EnqueueLabel(h.Vm, "ornek_alici", 100m);
        var queued = h.Vm.PrintQueue.Single();
        var labels = new LabelRepository(h.Db);
        labels.MarkSynced(queued.Id, 1000, labels.GetById(queued.Id)!.Revision);
        var session = new SessionRepository(h.Db).GetActive()!;
        var sessionId = Guid.ParseExact(session.Id, "N");
        _server.SeedSession(sessionId, DateTimeOffset.FromUnixTimeSeconds(session.StartedAt), null,
            owner: _envelope!.ReplicaId);
        _server.ForceEnd(sessionId);
        var l = labels.GetById(queued.Id)!;
        _server.SeedOrder(new FakeSalesServer.OrderBody(Guid.ParseExact(l.Id, "N"), sessionId, l.CustomerId, l.Platform,
            l.Username, l.DisplayName, l.MessageText, l.Code, 150m, DateTimeOffset.FromUnixTimeSeconds(l.AddedAt), null,
            null, null, false, false, false, null, null, null, null, null, null,
            DateTimeOffset.FromUnixTimeSeconds(1500), null, null), version: 2);
        Exec(h.Db, "UPDATE SalesSyncState SET Mode = 1 WHERE Id = 1");
        h.Vm.SalesSyncStatusText.Should().BeEmpty("ön koşul: tur koşmadan satır yok");

        (await Task.Run(() => _pull!.TourOnceAsync(CancellationToken.None))).Should().Be(SalesPullOutcome.CaughtUp);
        await Dispatcher.Yield(DispatcherPriority.Background);

        h.Vm.PrintQueue.Single().Price.Should().Be(150m, "kuyruktaki kopya akıştan inen değerle tazelendi");
        h.Vm.PrintQueue.Single().Should().BeSameAs(queued, "kopya yerinde değişir (seçim ve sıra korunur)");
        new SessionRepository(h.Db).GetActive()!.Id.Should().Be(session.Id, "canlı yayın kesilmez");
        h.Vm.SalesSyncStatusText.Should().StartWith("Satış: güncel ✓");
        h.Vm.IsSalesSyncHealthy.Should().BeTrue();
        h.Dialogs.Shown.Should().BeEmpty("açılır pencere yok");
        _server.Faults.Should().BeEmpty();
    });

    [Fact]
    public void Mod_Offta_satis_satiri_gorunmez()
        => RunOnOwnDispatcher(async () =>
    {
        using var h = Build();

        (await Task.Run(() => _pull!.TourOnceAsync(CancellationToken.None))).Should().Be(SalesPullOutcome.ModeOff);
        h.Vm.ApplySalesChanged(new SalesChangedEventArgs([], [], []));

        h.Vm.SalesSyncStatusText.Should().BeEmpty("mod Off'ta (bugün her lisans) satış akışı satırı yok");
        _server.ChangesCalls.Should().BeEmpty();
    });
}
