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
    public async Task SyncOnceAsync_cursor_carries_the_last_row_id_not_just_the_timestamp()
    {
        // Aynı damgayı paylaşan iki kayıt. İmleç yalnız damga olsaydı, sunucu
        // bir sonraki turda `> damga` sorulduğu için ikisini de bir daha hiç
        // döndürmezdi — ve atlanan satır bir müşteri KAYDI.
        var (svc, _, settings, _) = Build(_ => FakeHttpMessageHandler.Json(200,
            """
            [{"id":"00000000-0000-0000-0000-0000000000bb","username":"b","fullName":"B","address":"a","submittedAt":"2026-04-30T12:00:00Z"},
             {"id":"00000000-0000-0000-0000-0000000000aa","username":"a","fullName":"A","address":"a","submittedAt":"2026-04-30T12:00:00Z"}]
            """));

        await svc.SyncOnceAsync();

        settings.LastIntakeFormSync.Should().Be(new DateTimeOffset(2026, 4, 30, 12, 0, 0, TimeSpan.Zero));
        // Sıra (SubmittedAt, Id) → son satır büyük olan Id.
        settings.LastIntakeFormSyncId.Should()
            .Be(Guid.Parse("00000000-0000-0000-0000-0000000000bb"));
    }

    [Fact]
    public async Task SyncOnceAsync_sends_both_halves_of_the_cursor()
    {
        var (svc, _, settings, handler) = Build(_ => FakeHttpMessageHandler.Json(200, "[]"));
        settings.LastIntakeFormSync = new DateTimeOffset(2026, 4, 30, 12, 0, 0, TimeSpan.Zero);
        settings.LastIntakeFormSyncId = Guid.Parse("00000000-0000-0000-0000-0000000000cc");

        await svc.SyncOnceAsync();

        handler.Requests[0].RequestUri!.Query.Should()
            .Contain("since=")
            .And.Contain("sinceId=00000000-0000-0000-0000-0000000000cc");
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

    [Fact]
    public async Task SyncOnceAsync_youtube_channelId_merges_into_existing_chat_customer()
    {
        // Chat'ten kaydedilmiş YouTube müşterisi: Username=channelId.
        var (svc, repo, _, _) = Build(_ => FakeHttpMessageHandler.Json(200,
            """[{"id":"00000000-0000-0000-0000-000000000001","username":"UCabc123","fullName":"Sibel G","address":"Ankara","phone":"+905559998877","submittedAt":"2026-04-30T12:00:00Z","youTubeUsername":"sibelg","youTubeChannelId":"UCabc123"}]"""));
        repo.Insert(new OrderDeck.Core.Customers.Customer(
            "yt1", "youtube", "UCabc123", "@sibelg", null,
            100, 100, false, null, null, 2, 180m, null, null, null));

        var count = await svc.SyncOnceAsync();

        count.Should().Be(1);
        // channelId ile birebir eşleşti → AYRI satır açılmadı, geçmiş korundu.
        var yts = repo.GetAll().Where(c => c.Platform == "youtube").ToList();
        yts.Should().HaveCount(1);
        yts[0].Id.Should().Be("yt1");
        yts[0].Phone.Should().Be("+905559998877");
        yts[0].TotalAmount.Should().Be(180m);
    }

    // ── FullName backfill (tek seferlik geriye-dönük düzeltme) ────────────

    [Fact]
    public async Task BackfillFullNamesOnceAsync_fills_missing_fullname_from_server()
    {
        var (svc, repo, settings, _) = Build(_ => FakeHttpMessageHandler.Json(200,
            """[{"id":"00000000-0000-0000-0000-000000000001","username":"musaa.sevinc","fullName":"Musa Sevinç","address":"Adr","submittedAt":"2026-04-30T12:00:00Z","instagramUsername":"musaa.sevinc"}]"""));
        // Chat'ten gelmiş IG satırı: DisplayName = takma ad, FullName boş.
        repo.Insert(new OrderDeck.Core.Customers.Customer(
            "ig1", "instagram", "musaa.sevinc", "musaa.sevinc", null,
            100, 100, false, null, null, 0, 0m, null, null, null));

        var updated = await svc.BackfillFullNamesOnceAsync();

        updated.Should().Be(1);
        repo.GetById("ig1")!.FullName.Should().Be("Musa Sevinç");
        repo.GetById("ig1")!.DisplayName.Should().Be("musaa.sevinc"); // dokunulmadı
        settings.FullNameBackfillDone.Should().BeTrue();
    }

    [Fact]
    public async Task BackfillFullNamesOnceAsync_is_noop_when_already_done()
    {
        bool called = false;
        var (svc, _, settings, _) = Build(_ => { called = true; return FakeHttpMessageHandler.Json(200, "[]"); });
        settings.FullNameBackfillDone = true;

        var updated = await svc.BackfillFullNamesOnceAsync();

        updated.Should().Be(0);
        called.Should().BeFalse(); // sunucuya gitmedi
    }

    // ── UI freeze fix #1 (2026-05-13): auth failure flag ──────────────────

    [Fact]
    public async Task SyncOnceAsync_sets_LastSyncWasAuthFailure_on_401()
    {
        var (svc, _, _, _) = Build(_ => FakeHttpMessageHandler.Json(401,
            "{\"error\":\"invalid_credentials\",\"message\":\"E-posta veya şifre yanlış\"}"));

        await svc.SyncOnceAsync();

        svc.LastSyncWasAuthFailure.Should().BeTrue();
    }

    [Fact]
    public async Task SyncOnceAsync_clears_LastSyncWasAuthFailure_on_success()
    {
        bool firstCall = true;
        Func<HttpRequestMessage, HttpResponseMessage> responder = _ =>
        {
            if (firstCall)
            {
                firstCall = false;
                return FakeHttpMessageHandler.Json(401,
                    "{\"error\":\"invalid_credentials\",\"message\":\"err\"}");
            }
            return FakeHttpMessageHandler.Json(200, "[]");
        };
        var (svc, _, _, _) = Build(responder);

        await svc.SyncOnceAsync();
        svc.LastSyncWasAuthFailure.Should().BeTrue();

        await svc.SyncOnceAsync();
        svc.LastSyncWasAuthFailure.Should().BeFalse();
    }

    [Fact]
    public async Task SyncOnceAsync_does_not_set_auth_flag_on_network_failure()
    {
        // Generic HttpRequestException ≠ auth failure; flag false kalmalı
        var (svc, _, _, _) = Build(_ => throw new HttpRequestException("dns fail"));

        try { await svc.SyncOnceAsync(); }
        catch { /* network errors propagate, OK */ }

        svc.LastSyncWasAuthFailure.Should().BeFalse();
    }
}
