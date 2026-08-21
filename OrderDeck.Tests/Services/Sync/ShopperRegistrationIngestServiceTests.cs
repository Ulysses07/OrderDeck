using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using OrderDeck.App.Services.Sync;
using OrderDeck.Core.Settings;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Core.Time;
using OrderDeck.Licensing.Api;
using OrderDeck.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.Tests.Services.Sync;

public sealed class ShopperRegistrationIngestServiceTests
{
    private sealed class FakeLicenseProvider : ICurrentLicenseProvider
    {
        public string? CurrentLicenseKey { get; set; }
    }

    private sealed class FakeClock : IClock
    {
        public long UnixNow() => 1_716_000_000L; // fixed timestamp for tests
    }

    private static readonly Guid TestLicenseId = Guid.Parse("aaaabbbb-cccc-dddd-eeee-ffffaaaabbbb");
    private const string TestLicenseKey = "INGEST-TEST-KEY";

    private static string LicensesJson() =>
        $"[{{\"id\":\"{TestLicenseId}\",\"licenseKey\":\"{TestLicenseKey}\"}}]";

    private static string PullJson(params (Guid id, string platform, string username, string? fullName, string? phone, string? address, DateTimeOffset updatedAt)[] items)
    {
        var entries = new List<string>();
        foreach (var (id, platform, username, fullName, phone, address, updatedAt) in items)
        {
            var fn = fullName == null ? "null" : $"\"{fullName}\"";
            var ph = phone == null ? "null" : $"\"{phone}\"";
            var addr = address == null ? "null" : $"\"{address}\"";
            entries.Add(
                $"{{\"id\":\"{id}\",\"platform\":\"{platform}\",\"username\":\"{username}\"," +
                $"\"fullName\":{fn},\"phone\":{ph},\"address\":{addr}," +
                $"\"updatedAt\":\"{updatedAt:O}\"}}");
        }
        return "[" + string.Join(",", entries) + "]";
    }

    private sealed record Fixture(
        ShopperRegistrationIngestService Svc,
        CustomerRepository Customers,
        SettingsStore Store,
        FakeLicenseProvider License,
        InMemorySqlite Db);

    private static Fixture Build(
        Func<HttpRequestMessage, HttpResponseMessage> responder,
        bool seedLicense = true)
    {
        var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var customers = new CustomerRepository(db);

        var settingsPath = Path.Combine(Path.GetTempPath(), $"ingest-settings-{Guid.NewGuid():N}.json");
        var store = new SettingsStore(settingsPath);

        var handler = new FakeHttpMessageHandler(responder);
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://test.local") };
        var api = new LicenseApiClient(http, new LicenseTokenStore());

        var licenseProvider = new FakeLicenseProvider();
        if (seedLicense) licenseProvider.CurrentLicenseKey = TestLicenseKey;

        var svc = new ShopperRegistrationIngestService(
            api, customers, store, licenseProvider,
            new FakeClock(),
            NullLogger<ShopperRegistrationIngestService>.Instance);

        return new Fixture(svc, customers, store, licenseProvider, db);
    }

    // ── No license → returns 0, no HTTP calls ─────────────────────────────────

    [Fact]
    public async Task IngestOnce_no_license_returns_zero()
    {
        var apiCalls = 0;
        var fx = Build(_ =>
        {
            Interlocked.Increment(ref apiCalls);
            return FakeHttpMessageHandler.Empty(200);
        }, seedLicense: false);
        using var _d = fx.Db;

        var result = await fx.Svc.IngestOnceAsync(CancellationToken.None);

        result.Should().Be(0, "no license key → nothing to ingest");
        apiCalls.Should().Be(0, "no HTTP calls when no license key");
    }

    // ── No new items → returns 0, watermark unchanged ─────────────────────────

    [Fact]
    public async Task IngestOnce_no_new_items_returns_zero_and_watermark_unchanged()
    {
        var fx = Build(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path == "/api/v1/me/licenses")
                return FakeHttpMessageHandler.Json(200, LicensesJson());
            if (path.Contains("/wpf-customers/since"))
                return FakeHttpMessageHandler.Json(200, "[]");
            return FakeHttpMessageHandler.Empty(404);
        });
        using var _d = fx.Db;

        var settings = fx.Store.Load();
        settings.LastShopperIngestAt = 999L;
        fx.Store.Save(settings);

        var result = await fx.Svc.IngestOnceAsync(CancellationToken.None);

        result.Should().Be(0);
        fx.Store.Load().LastShopperIngestAt.Should().Be(999L, "watermark must not change when nothing to ingest");
    }

    // ── New shopper registration → inserts Customer + advances watermark ───────

    [Fact]
    public async Task IngestOnce_new_shopper_inserts_customer_and_advances_watermark()
    {
        var shopperId = Guid.NewGuid();
        var updatedAt = DateTimeOffset.UtcNow;

        var fx = Build(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path == "/api/v1/me/licenses")
                return FakeHttpMessageHandler.Json(200, LicensesJson());
            if (path.Contains("/wpf-customers/since"))
                return FakeHttpMessageHandler.Json(200, PullJson(
                    (shopperId, "youtube", "newuser", "Yeni Kullanıcı", "+905001112233", "İstanbul", updatedAt)));
            return FakeHttpMessageHandler.Empty(404);
        });
        using var _d = fx.Db;

        var result = await fx.Svc.IngestOnceAsync(CancellationToken.None);

        result.Should().Be(1);

        // Customer inserted
        var customer = fx.Customers.FindByPlatformAndUsername("youtube", "newuser");
        customer.Should().NotBeNull("shopper must be inserted as local Customer");
        customer!.Id.Should().Be(shopperId.ToString("N"));
        customer.DisplayName.Should().Be("Yeni Kullanıcı");
        customer.Phone.Should().Be("+905001112233");
        customer.Address.Should().Be("İstanbul");
        customer.Platform.Should().Be("youtube");
        customer.Username.Should().Be("newuser");
        customer.IsBlacklisted.Should().BeFalse();

        // Watermark advanced — tam hassasiyetle, (UpdatedAt, Id) çifti olarak
        var saved = fx.Store.Load();
        saved.LastShopperIngestUpdatedAt.Should().Be(updatedAt);
        saved.LastShopperIngestId.Should().Be(shopperId);
    }

    // ── Already-exists by (Platform, Username) → skipped, watermark still advances ─

    [Fact]
    public async Task IngestOnce_already_existing_customer_skipped_but_watermark_advances()
    {
        var shopperId = Guid.NewGuid();
        var updatedAt = DateTimeOffset.UtcNow;

        var fx = Build(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path == "/api/v1/me/licenses")
                return FakeHttpMessageHandler.Json(200, LicensesJson());
            if (path.Contains("/wpf-customers/since"))
                return FakeHttpMessageHandler.Json(200, PullJson(
                    (shopperId, "instagram", "existinguser", "Old Name", null, null, updatedAt)));
            return FakeHttpMessageHandler.Empty(404);
        });
        using var _d = fx.Db;

        // Pre-seed a customer with the same (Platform, Username)
        fx.Customers.Insert(new OrderDeck.Core.Customers.Customer(
            Id: Guid.NewGuid().ToString("N"),
            Platform: "instagram",
            Username: "existinguser",
            DisplayName: "Pre-existing",
            AvatarUrl: null,
            FirstSeenAt: 1000L,
            LastSeenAt: 1000L,
            IsBlacklisted: false,
            BlacklistReason: null,
            Notes: null,
            TotalLabelsPrinted: 0,
            TotalAmount: 0m,
            BlacklistedAt: null,
            Address: null,
            Phone: null));

        var settings = fx.Store.Load();
        settings.LastShopperIngestAt = 0L;
        fx.Store.Save(settings);

        var result = await fx.Svc.IngestOnceAsync(CancellationToken.None);

        result.Should().Be(0, "already-existing customer must be skipped");

        // Watermark still advances to the item's (UpdatedAt, Id)
        var saved = fx.Store.Load();
        saved.LastShopperIngestUpdatedAt.Should().Be(updatedAt,
            "watermark must advance even when all items were skipped (idempotent)");
        saved.LastShopperIngestId.Should().Be(shopperId);
    }

    // ── API failure → returns 0, watermark NOT advanced ───────────────────────

    [Fact]
    public async Task IngestOnce_api_failure_returns_zero_and_watermark_unchanged()
    {
        var fx = Build(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path == "/api/v1/me/licenses")
                return FakeHttpMessageHandler.Json(200, LicensesJson());
            if (path.Contains("/wpf-customers/since"))
                return FakeHttpMessageHandler.Empty(500);
            return FakeHttpMessageHandler.Empty(404);
        });
        using var _d = fx.Db;

        var settings = fx.Store.Load();
        settings.LastShopperIngestAt = 123L;
        fx.Store.Save(settings);

        var result = await fx.Svc.IngestOnceAsync(CancellationToken.None);

        result.Should().Be(0, "API failure must return 0");
        fx.Store.Load().LastShopperIngestAt.Should().Be(123L, "watermark must NOT advance on failure");
    }

    // ── Multiple items: some new, some skipped ────────────────────────────────

    [Fact]
    public async Task IngestOnce_mixed_new_and_existing_inserts_only_new()
    {
        var newId = Guid.NewGuid();
        var existingId = Guid.NewGuid();
        var t1 = DateTimeOffset.UtcNow.AddMinutes(-2);
        var t2 = DateTimeOffset.UtcNow.AddMinutes(-1);

        var fx = Build(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path == "/api/v1/me/licenses")
                return FakeHttpMessageHandler.Json(200, LicensesJson());
            if (path.Contains("/wpf-customers/since"))
                return FakeHttpMessageHandler.Json(200, PullJson(
                    (newId, "tiktok", "brandnew", "Yeni", "+905001112233", null, t1),
                    (existingId, "tiktok", "alreadyhere", "Old", null, null, t2)));
            return FakeHttpMessageHandler.Empty(404);
        });
        using var _d = fx.Db;

        // Pre-seed the "already here" user
        fx.Customers.Insert(new OrderDeck.Core.Customers.Customer(
            Id: existingId.ToString("N"),
            Platform: "tiktok",
            Username: "alreadyhere",
            DisplayName: "Old",
            AvatarUrl: null,
            FirstSeenAt: 500L,
            LastSeenAt: 500L,
            IsBlacklisted: false,
            BlacklistReason: null,
            Notes: null,
            TotalLabelsPrinted: 0,
            TotalAmount: 0m,
            BlacklistedAt: null,
            Address: null,
            Phone: null));

        var result = await fx.Svc.IngestOnceAsync(CancellationToken.None);

        result.Should().Be(1, "only the new customer should be inserted");

        var newCustomer = fx.Customers.FindByPlatformAndUsername("tiktok", "brandnew");
        newCustomer.Should().NotBeNull();

        // Watermark advances to the last row in (UpdatedAt, Id) order = (t2, existingId)
        var saved = fx.Store.Load();
        saved.LastShopperIngestUpdatedAt.Should().Be(t2);
        saved.LastShopperIngestId.Should().Be(existingId);
    }

    // ── since query param is built from watermark ─────────────────────────────

    [Fact]
    public async Task IngestOnce_passes_watermark_as_since_query_param()
    {
        string? capturedQuery = null;
        var fx = Build(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path == "/api/v1/me/licenses")
                return FakeHttpMessageHandler.Json(200, LicensesJson());
            if (path.Contains("/wpf-customers/since"))
            {
                capturedQuery = req.RequestUri.Query;
                return FakeHttpMessageHandler.Json(200, "[]");
            }
            return FakeHttpMessageHandler.Empty(404);
        });
        using var _d = fx.Db;

        var knownTimestamp = 1_715_000_000L;
        var settings = fx.Store.Load();
        settings.LastShopperIngestAt = knownTimestamp;
        fx.Store.Save(settings);

        await fx.Svc.IngestOnceAsync(CancellationToken.None);

        capturedQuery.Should().NotBeNullOrEmpty();
        // The query string must contain the expected ISO-8601 watermark
        var expectedDate = DateTimeOffset.FromUnixTimeSeconds(knownTimestamp).ToString("O");
        capturedQuery!.Should().Contain(Uri.EscapeDataString(expectedDate));
        capturedQuery.Should().Contain($"sinceId={Guid.Empty:D}",
            "eski kurulumda id imleci yok — boş Guid ile başlanmalı");
    }

    // ── Yeni (UpdatedAt, Id) imleci varsa eski Unix alanı yok sayılır ─────────

    [Fact]
    public async Task IngestOnce_prefers_composite_cursor_over_legacy_unix_watermark()
    {
        string? capturedQuery = null;
        var fx = Build(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path == "/api/v1/me/licenses")
                return FakeHttpMessageHandler.Json(200, LicensesJson());
            if (path.Contains("/wpf-customers/since"))
            {
                capturedQuery = req.RequestUri.Query;
                return FakeHttpMessageHandler.Json(200, "[]");
            }
            return FakeHttpMessageHandler.Empty(404);
        });
        using var _d = fx.Db;

        // Aynı saniyeyi paylaşan bir küme ortasında kalmış imleç: milisaniye
        // hassasiyeti korunmazsa aşağıdaki beklenti tutmaz.
        var cursor = new DateTimeOffset(2026, 8, 21, 10, 30, 15, 750, TimeSpan.Zero);
        var cursorId = Guid.Parse("11112222-3333-4444-5555-666677778888");

        var settings = fx.Store.Load();
        settings.LastShopperIngestAt = 1_715_000_000L; // eski alan — yok sayılmalı
        settings.LastShopperIngestUpdatedAt = cursor;
        settings.LastShopperIngestId = cursorId;
        fx.Store.Save(settings);

        await fx.Svc.IngestOnceAsync(CancellationToken.None);

        capturedQuery.Should().NotBeNullOrEmpty();
        capturedQuery!.Should().Contain(Uri.EscapeDataString(cursor.ToString("O")),
            "imleç saniyeye yuvarlanmadan gönderilmeli");
        capturedQuery.Should().Contain($"sinceId={cursorId:D}");
        capturedQuery.Should().NotContain(
            Uri.EscapeDataString(DateTimeOffset.FromUnixTimeSeconds(1_715_000_000L).ToString("O")),
            "yeni imleç varken eski Unix alanı kullanılmamalı");
    }
}
