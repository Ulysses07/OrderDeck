using FluentAssertions;
using OrderDeck.Licensing.Api;
using OrderDeck.Licensing.Services;
using OrderDeck.Licensing.Storage;
using OrderDeck.Licensing.Tests.TestHelpers;
using OrderDeck.Licensing.Trial;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace OrderDeck.Licensing.Tests.Services;

public sealed class HeartbeatHostedServiceTests : IDisposable
{
    private readonly string _dir;
    private readonly AuthStore _authStore;
    private readonly LicenseStateStore _licenseStore;
    private int _validateCallCount;

    public HeartbeatHostedServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "OrderDeck.Licensing.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        var enc = new EncryptedStore();
        _authStore = new AuthStore(enc, Path.Combine(_dir, "auth.dat"));
        _licenseStore = new LicenseStateStore(enc, Path.Combine(_dir, "license.dat"));

        _authStore.Save(new AuthRecord(Guid.NewGuid(), "u@x", "u", "tok", DateTimeOffset.UtcNow.AddDays(7)));
        _licenseStore.Save(new LicenseRecord("LDK", "STD",
            DateTimeOffset.UtcNow.AddDays(365), 365,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "Active"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private (HeartbeatHostedService svc, LicenseService licSvc) Build(
        TimeSpan interval, Action? onValidateCall = null)
    {
        var handler = new FakeHttpMessageHandler(_ =>
        {
            Interlocked.Increment(ref _validateCallCount);
            onValidateCall?.Invoke();
            return FakeHttpMessageHandler.Json(200,
                """{"status":"active","expiresAt":"2027-01-01T00:00:00Z","remainingDays":365,"sku":"STD","slotInfo":{"used":1,"total":1,"thisDeviceActive":true}}""");
        });
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://test.local") };
        var api = new LicenseApiClient(http, new OrderDeck.Licensing.Api.LicenseTokenStore());
        var opts = Options.Create(new LicensingOptions { OfflineGraceDays = 14, HeartbeatIntervalHours = 24, TrialDurationDays = 14 });
        var hwId = new FakeHardwareIdProvider();
        var trialStorage = new NullTrialStorage();
        var trial = new TrialService(trialStorage, hwId, opts, () => DateTimeOffset.UtcNow, NullLogger<TrialService>.Instance);
        var licSvc = new LicenseService(api, _authStore, _licenseStore, hwId, opts, trial, NullLogger<LicenseService>.Instance);
        var hb = new HeartbeatHostedService(licSvc, NullLogger<HeartbeatHostedService>.Instance, interval);
        return (hb, licSvc);
    }

    [Fact]
    public async Task Heartbeat_calls_RefreshAsync_periodically()
    {
        // Deterministic TaskCompletionSource pattern (matches the
        // IntakeFormSyncHostedServiceTests fix from 2026-05-08, commit b2c8065).
        // Previous Task.Delay(250) was tight enough that a slow Windows GitHub
        // runner under contention could only observe 1 of the expected ≥2
        // additional calls before cancellation (CI-only flake reproed 2026-05-11
        // on PR #21). Now: wait until handler fires ≥2 additional times after
        // initialization, with a 5s watchdog catching a genuinely-broken service.
        var hbCallsObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool initPhaseDone = false;
        int hbCalls = 0;

        var (hb, licSvc) = Build(
            interval: TimeSpan.FromMilliseconds(50),
            onValidateCall: () =>
            {
                if (!Volatile.Read(ref initPhaseDone)) return;   // init call, skip
                if (Interlocked.Increment(ref hbCalls) >= 2)
                    hbCallsObserved.TrySetResult();
            });

        await licSvc.InitializeAsync();
        Volatile.Write(ref initPhaseDone, true);
        var initialCount = _validateCallCount;

        using var cts = new CancellationTokenSource();
        await hb.StartAsync(cts.Token);
        await hbCallsObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();
        try { await hb.StopAsync(CancellationToken.None); } catch { }

        (_validateCallCount - initialCount).Should().BeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public async Task Heartbeat_stops_cleanly_on_cancellation()
    {
        var (hb, _) = Build(interval: TimeSpan.FromSeconds(60));
        using var cts = new CancellationTokenSource();
        await hb.StartAsync(cts.Token);
        cts.Cancel();

        var stopAct = async () => await hb.StopAsync(CancellationToken.None);
        await stopAct.Should().NotThrowAsync();
    }

    private sealed class NullTrialStorage : ITrialStorage
    {
        public string Name => "null";
        public TrialRecord? TryRead() => null;
        public void Write(TrialRecord r) { }
        public void Clear() { }
    }
}
