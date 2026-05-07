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

public sealed class IntakeFormSyncServiceTests
{
    private sealed class FakeClock : IClock
    {
        public long UnixNow() => 1714521600L;
        public DateTimeOffset Now => DateTimeOffset.FromUnixTimeSeconds(1714521600L);
    }

    private static (IntakeFormSyncService svc, CustomerRepository repo, AppSettings settings, FakeHttpMessageHandler handler) Build(
        Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var repo = new CustomerRepository(db);

        var settingsPath = Path.Combine(Path.GetTempPath(), $"settings-{Guid.NewGuid():N}.json");
        var store = new SettingsStore(settingsPath);
        var settings = store.Load();

        var handler = new FakeHttpMessageHandler(responder);
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://test.local") };
        var api = new LicenseApiClient(http, new OrderDeck.Licensing.Api.LicenseTokenStore());

        var svc = new IntakeFormSyncService(api, repo, store, settings, new FakeClock(),
            NullLogger<IntakeFormSyncService>.Instance);
        return (svc, repo, settings, handler);
    }

    [Fact]
    public async Task SyncOnceAsync_returns_zero_when_server_returns_empty()
    {
        var (svc, repo, _, _) = Build(_ => FakeHttpMessageHandler.Json(200, "[]"));

        var count = await svc.SyncOnceAsync();

        count.Should().Be(0);
    }

    [Fact]
    public async Task SyncOnceAsync_creates_customer_with_form_platform()
    {
        var (svc, repo, _, _) = Build(_ => FakeHttpMessageHandler.Json(200,
            """[{"id":"00000000-0000-0000-0000-000000000001","username":"bilalcanli","fullName":"Bilal Canlı","address":"Atatürk Cad","submittedAt":"2026-04-30T12:00:00Z"}]"""));

        var count = await svc.SyncOnceAsync();

        count.Should().Be(1);
        var customers = repo.Search("bilalcanli", limit: 5);
        customers.Should().Contain(c => c.Platform == "form" && c.Username == "bilalcanli");
    }

    [Fact]
    public async Task SyncOnceAsync_updates_existing_form_customer_on_second_pull()
    {
        var (svc, repo, _, _) = Build(_ => FakeHttpMessageHandler.Json(200,
            "[{\"id\":\"00000000-0000-0000-0000-000000000001\",\"username\":\"u1\",\"fullName\":\"Eski Ad\",\"address\":\"Eski\",\"submittedAt\":\"2026-04-30T11:00:00Z\"},{\"id\":\"00000000-0000-0000-0000-000000000002\",\"username\":\"u1\",\"fullName\":\"Yeni Ad\",\"address\":\"Yeni\",\"submittedAt\":\"2026-04-30T12:00:00Z\"}]"));

        await svc.SyncOnceAsync();

        var customer = repo.Search("u1", limit: 5).Single(c => c.Platform == "form");
        customer.DisplayName.Should().Be("Yeni Ad");
        customer.Address.Should().Be("Yeni");
    }

    [Fact]
    public async Task SyncOnceAsync_advances_cursor_to_max_submittedAt()
    {
        var (svc, _, settings, handler) = Build(_ => FakeHttpMessageHandler.Json(200,
            """[{"id":"00000000-0000-0000-0000-000000000001","username":"u","fullName":"n","address":"a","submittedAt":"2026-04-30T12:00:00Z"}]"""));

        await svc.SyncOnceAsync();

        settings.LastIntakeFormSync.Should().Be(new DateTimeOffset(2026, 4, 30, 12, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task SyncOnceAsync_returns_zero_on_network_failure_and_does_not_advance_cursor()
    {
        var (svc, _, settings, _) = Build(_ => throw new HttpRequestException("dns fail"));
        settings.LastIntakeFormSync = new DateTimeOffset(2026, 4, 30, 10, 0, 0, TimeSpan.Zero);

        var count = await svc.SyncOnceAsync();

        count.Should().Be(0);
        settings.LastIntakeFormSync.Should().Be(new DateTimeOffset(2026, 4, 30, 10, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task SyncOnceAsync_propagates_phone_from_dto_to_customer()
    {
        var (svc, repo, _, _) = Build(_ => FakeHttpMessageHandler.Json(200,
            """[{"id":"00000000-0000-0000-0000-000000000001","username":"alice","fullName":"Alice","address":"Addr","phone":"+905551111111","submittedAt":"2026-04-30T12:00:00Z"}]"""));

        var count = await svc.SyncOnceAsync();

        count.Should().Be(1);
        var customer = repo.Search("alice", limit: 5).Single(c => c.Platform == "form");
        customer.Phone.Should().Be("+905551111111");
    }
}
