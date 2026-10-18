using System.Runtime.ExceptionServices;
using System.Windows.Threading;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using OrderDeck.App.Services.Sync;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Sales;
using OrderDeck.Core.Sessions;
using OrderDeck.Core.Settings;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Core.Time;
using OrderDeck.Licensing;
using OrderDeck.Licensing.Api;
using OrderDeck.Tests.Sync;
using OrderDeck.Tests.TestHelpers;

namespace OrderDeck.Tests.App;

/// <summary>
/// Off turu kabukta (spec 2026-10-13 §5.3 mode-off, §8.5; W2-11): komut ucunun <c>mode-off</c>'u "Senkron kapatıldı;
/// ekran yenileniyor." şeridini açar (açılır pencere yok, düğme yok); Off turu sonuçlanınca şerit kendiliğinden kalkar
/// ve satış satırı mod Off'taki gibi gizlenir. Uçtan uca: <see cref="FakeSalesServer"/> → satış akışı turu (kip adımı ve
/// Off turunun yerel adımlarıyla) → VM.
/// </summary>
public sealed class MainShellDisableRoundTests : IDisposable
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
    private readonly string _settingsPath = Path.Combine(Path.GetTempPath(), $"od-shell-disable-{Guid.NewGuid():N}.json");
    private SalesChangesPullService? _pull;
    private SalesSyncStateRepository? _state;

    public void Dispose()
    {
        _server.Dispose();
        foreach (var f in new[] { _settingsPath, _settingsPath + ".bak", _settingsPath + ".tmp" })
            try { File.Delete(f); } catch { /* en iyi çaba */ }
    }

    private MainShellTestHarness.Harness Build() =>
        MainShellTestHarness.Build(salesPull: db =>
        {
            var license = new License(_server.Lisans);
            var identity = new ReplicaIdentityRepository(db);
            _state = new SalesSyncStateRepository(db);
            var envelope = new SalesSyncEnvelope(identity, _state, new Hardware(Guid.NewGuid().ToString("N")), license,
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
            var store = new SettingsStore(_settingsPath);
            var settings = store.Load();
            var state = new SalesSyncStateService(api, envelope, identity, _state, license, () => settings,
                NullLogger<SalesSyncStateService>.Instance);
            var labels = new LabelRepository(db);
            var push = new SessionOrderSyncService(api, new SessionRepository(db), labels, license, envelope,
                new HandoverGate(), new SalesPushApply(db), clock, NullLogger<SessionOrderSyncService>.Instance);
            var shipments = new ShipmentSyncService(api, new ShipmentRepository(db), store, settings, license, envelope,
                clock, NullLogger<ShipmentSyncService>.Instance, salesState: _state);
            var payments = new PaymentSyncService(api, new PaymentRepository(db), cursors, license,
                clock, NullLogger<PaymentSyncService>.Instance);
            var feed = new SalesFeedRepository(db, busy);
            var round = new SalesModeOffRound(feed, _state, new PendingSalesCommandRepository(db),
                new LabelService(labels, new CustomerService(new CustomerRepository(db), new SessionRepository(db), labels,
                    clock), db, clock),
                labels, new SalesConflictLogRepository(db), store, settings, clock, NullLogger<SalesModeOffRound>.Instance);
            var coordinator = new SalesModeCoordinator(state, _state, envelope,
                new SalesOpeningFlush(new PaymentRecordSender(api, new PendingPaymentRecordRepository(db), envelope, license,
                    clock, NullLogger<PaymentRecordSender>.Instance), customerPush, push, shipments, payments, _state), clock,
                NullLogger<SalesModeCoordinator>.Instance, round);
            _pull = new SalesChangesPullService(api, state, _state, envelope, feed, new SalesFeedFailureRepository(db),
                new RemoteLiveSessionRepository(db), cursors, labels, customerPush, push,
                new CustomerChangesPullService(api, new CustomerRepository(db), customerSync, cursors, customerPush,
                    license, clock, new SyncStatusTracker(), NullLogger<CustomerChangesPullService>.Instance),
                license, clock, NullLogger<SalesChangesPullService>.Instance,
                sessions: new SessionRepository(db), modeCoordinator: coordinator);
            return _pull;
        });

    /// <summary>Gövdeyi KENDİ Dispatcher döngüsü olan STA iş parçacığında koşturur (bkz.
    /// <c>MainShellOpeningRoundTests.RunOnOwnDispatcher</c>).</summary>
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

    [Fact]
    public void Komut_ucunun_mode_offu_seridi_acar_Off_turu_sonuclaninca_serit_kalkar_satis_satiri_gizlenir()
        => RunOnOwnDispatcher(async () =>
    {
        using var h = Build();
        _server.Mode = 1;
        _server.ModeChangedAt = DateTimeOffset.UtcNow.AddDays(-1);
        (await Task.Run(() => _pull!.TourOnceAsync(CancellationToken.None))).Should().Be(SalesPullOutcome.CaughtUp);
        h.Vm.ApplySalesChanged(new SalesChangedEventArgs([], [], []));
        h.Vm.SalesSyncStatusText.Should().StartWith("Satış: güncel ✓", "ön koşul: mod On");
        _server.Disable(DateTimeOffset.UtcNow);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _pull!.ModeOffFinished += (_, _) => finished.TrySetResult();

        _pull.RequestModeOffRound();                                   // komut istemcisinin ModeOffDetected'ı

        h.Vm.SalesModeOffBanner.Should().Be("Senkron kapatıldı; ekran yenileniyor.");
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await Dispatcher.Yield(DispatcherPriority.Background);

        h.Vm.SalesModeOffBanner.Should().BeNull("Off turu sonuçlandı: şerit kendiliğinden kalkar");
        _state!.GetMode().Should().Be(0);
        h.Vm.SalesSyncStatusText.Should().BeEmpty("mod Off'ta satış satırı yok (bugünkü gibi)");
        h.Dialogs.Shown.Should().BeEmpty("açılır pencere yok");
        _server.Faults.Should().BeEmpty();
    });
}
