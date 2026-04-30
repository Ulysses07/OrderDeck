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

    [Fact]
    public async Task Hosted_service_calls_SyncOnceAsync_periodically()
    {
        int callCount = 0;
        var handler = new FakeHttpMessageHandler(_ =>
        {
            Interlocked.Increment(ref callCount);
            return FakeHttpMessageHandler.Json(200, "[]");
        });

        var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var repo = new CustomerRepository(db);
        var settingsPath = Path.Combine(Path.GetTempPath(), $"settings-{Guid.NewGuid():N}.json");
        var store = new SettingsStore(settingsPath);
        var settings = store.Load();
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://test.local") };
        var api = new LicenseApiClient(http);
        var sync = new IntakeFormSyncService(api, repo, store, settings, new FakeClock(),
            NullLogger<IntakeFormSyncService>.Instance);
        var hosted = new IntakeFormSyncHostedService(sync,
            NullLogger<IntakeFormSyncHostedService>.Instance,
            TimeSpan.FromMilliseconds(50));

        using var cts = new CancellationTokenSource();
        await hosted.StartAsync(cts.Token);
        await Task.Delay(250);
        cts.Cancel();
        try { await hosted.StopAsync(CancellationToken.None); } catch { }

        callCount.Should().BeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public async Task Hosted_service_continues_after_sync_throws()
    {
        int callCount = 0;
        var handler = new FakeHttpMessageHandler(_ =>
        {
            var c = Interlocked.Increment(ref callCount);
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
        var api = new LicenseApiClient(http);
        var sync = new IntakeFormSyncService(api, repo, store, settings, new FakeClock(),
            NullLogger<IntakeFormSyncService>.Instance);
        var hosted = new IntakeFormSyncHostedService(sync,
            NullLogger<IntakeFormSyncHostedService>.Instance,
            TimeSpan.FromMilliseconds(50));

        using var cts = new CancellationTokenSource();
        await hosted.StartAsync(cts.Token);
        await Task.Delay(250);
        cts.Cancel();
        try { await hosted.StopAsync(CancellationToken.None); } catch { }

        // İlk call throw, 2. call success — toplam ≥2
        callCount.Should().BeGreaterThanOrEqualTo(2);
    }
}
