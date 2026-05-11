using FluentAssertions;
using OrderDeck.App.Services.Sync;
using OrderDeck.Core.Settings;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Core.Time;
using OrderDeck.Licensing.Api;
using OrderDeck.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace OrderDeck.Tests.Services.Sync;

public sealed class PaymentSyncHostedServiceTests
{
    private sealed class FakeClock : IClock
    {
        public long UnixNow() => 1714521600L;
        public DateTimeOffset Now => DateTimeOffset.FromUnixTimeSeconds(1714521600L);
    }

    private sealed class StubLicenseProvider : ICurrentLicenseProvider
    {
        public string? CurrentLicenseKey { get; set; } = "LDK-TEST";
    }

    // Deterministic TaskCompletionSource pattern (matches IntakeFormSyncHostedServiceTests
    // 2026-05-08 anti-flake fix): wait until the handler has fired N times instead of
    // sleeping for a fixed duration.

    [Fact]
    public async Task Hosted_service_calls_SyncOnceAsync_periodically()
    {
        int callCount = 0;
        var twoCallsObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new FakeHttpMessageHandler(_ =>
        {
            var c = Interlocked.Increment(ref callCount);
            if (c >= 2) twoCallsObserved.TrySetResult();
            return FakeHttpMessageHandler.Json(200, "[]");
        });

        var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var repo = new PaymentRepository(db);
        var settingsPath = Path.Combine(Path.GetTempPath(), $"settings-{Guid.NewGuid():N}.json");
        var store = new SettingsStore(settingsPath);
        var settings = store.Load();
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://test.local") };
        var api = new LicenseApiClient(http, new OrderDeck.Licensing.Api.LicenseTokenStore());
        var sync = new PaymentSyncService(api, repo, store, settings,
            new StubLicenseProvider(), new FakeClock(),
            NullLogger<PaymentSyncService>.Instance);
        var hosted = new PaymentSyncHostedService(sync,
            NullLogger<PaymentSyncHostedService>.Instance,
            TimeSpan.FromMilliseconds(50));

        using var cts = new CancellationTokenSource();
        await hosted.StartAsync(cts.Token);
        await twoCallsObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();
        try { await hosted.StopAsync(CancellationToken.None); } catch { }

        callCount.Should().BeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public async Task Hosted_service_continues_after_sync_throws()
    {
        int callCount = 0;
        var twoCallsObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new FakeHttpMessageHandler(_ =>
        {
            var c = Interlocked.Increment(ref callCount);
            if (c >= 2) twoCallsObserved.TrySetResult();
            if (c == 1) throw new HttpRequestException("fail once");
            return FakeHttpMessageHandler.Json(200, "[]");
        });

        var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var repo = new PaymentRepository(db);
        var settingsPath = Path.Combine(Path.GetTempPath(), $"settings-{Guid.NewGuid():N}.json");
        var store = new SettingsStore(settingsPath);
        var settings = store.Load();
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://test.local") };
        var api = new LicenseApiClient(http, new OrderDeck.Licensing.Api.LicenseTokenStore());
        var sync = new PaymentSyncService(api, repo, store, settings,
            new StubLicenseProvider(), new FakeClock(),
            NullLogger<PaymentSyncService>.Instance);
        var hosted = new PaymentSyncHostedService(sync,
            NullLogger<PaymentSyncHostedService>.Instance,
            TimeSpan.FromMilliseconds(50));

        using var cts = new CancellationTokenSource();
        await hosted.StartAsync(cts.Token);
        await twoCallsObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();
        try { await hosted.StopAsync(CancellationToken.None); } catch { }

        callCount.Should().BeGreaterThanOrEqualTo(2);
    }
}
