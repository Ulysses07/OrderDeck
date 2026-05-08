using FluentAssertions;
using OrderDeck.App.Services.IntakeForm;
using OrderDeck.Core.Settings;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Core.Time;
using OrderDeck.Licensing.Api;
using OrderDeck.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace OrderDeck.Tests.Services.IntakeForm;

public sealed class IntakeFormSyncHostedServiceTests
{
    private sealed class FakeClock : IClock
    {
        public long UnixNow() => 1714521600L;
        public DateTimeOffset Now => DateTimeOffset.FromUnixTimeSeconds(1714521600L);
    }

    // Tests use a deterministic TaskCompletionSource ("twoCallsObserved")
    // instead of the previous Task.Delay(250). The fixed delay was tight
    // enough that a slow Windows GitHub runner under resource contention
    // could trigger only 1 of the ≥2 expected callbacks before cancellation
    // (CI-only flake reproed on 2026-05-08). Now: each test waits until the
    // handler has actually fired ≥2 times, with a 5s watchdog timeout that
    // catches a genuinely-broken hosted service (vs. just a slow runner).

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
        var repo = new CustomerRepository(db);
        var settingsPath = Path.Combine(Path.GetTempPath(), $"settings-{Guid.NewGuid():N}.json");
        var store = new SettingsStore(settingsPath);
        var settings = store.Load();
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://test.local") };
        var api = new LicenseApiClient(http, new OrderDeck.Licensing.Api.LicenseTokenStore());
        var sync = new IntakeFormSyncService(api, repo, store, settings, new FakeClock(),
            NullLogger<IntakeFormSyncService>.Instance);
        var hosted = new IntakeFormSyncHostedService(sync,
            NullLogger<IntakeFormSyncHostedService>.Instance,
            TimeSpan.FromMilliseconds(50));

        using var cts = new CancellationTokenSource();
        await hosted.StartAsync(cts.Token);
        // Wait for the handler to have fired ≥2 times instead of a fixed
        // delay. 5s watchdog covers slow CI runners under contention.
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
        var repo = new CustomerRepository(db);
        var settingsPath = Path.Combine(Path.GetTempPath(), $"settings-{Guid.NewGuid():N}.json");
        var store = new SettingsStore(settingsPath);
        var settings = store.Load();
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://test.local") };
        var api = new LicenseApiClient(http, new OrderDeck.Licensing.Api.LicenseTokenStore());
        var sync = new IntakeFormSyncService(api, repo, store, settings, new FakeClock(),
            NullLogger<IntakeFormSyncService>.Instance);
        var hosted = new IntakeFormSyncHostedService(sync,
            NullLogger<IntakeFormSyncHostedService>.Instance,
            TimeSpan.FromMilliseconds(50));

        using var cts = new CancellationTokenSource();
        await hosted.StartAsync(cts.Token);
        // İlk call throw, 2. call success — wait for both deterministically.
        await twoCallsObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();
        try { await hosted.StopAsync(CancellationToken.None); } catch { }

        callCount.Should().BeGreaterThanOrEqualTo(2);
    }
}
