using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using OrderDeck.App.Services.Sync;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Settings;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Licensing.Api;
using OrderDeck.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.Tests.Services.Sync;

public sealed class WpfCustomerProjectionSyncServiceTests
{
    private sealed class FakeLicenseProvider : ICurrentLicenseProvider
    {
        public string? CurrentLicenseKey { get; set; }
    }

    private static readonly Guid TestLicenseId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
    private const string TestLicenseKey        = "WPF-CUST-TEST-KEY";

    private static string LicensesJson() =>
        $"[{{\"id\":\"{TestLicenseId}\",\"licenseKey\":\"{TestLicenseKey}\"}}]";

    private static string SyncRespJson(int synced = 0, int retroMatches = 0) =>
        $"{{\"synced\":{synced},\"retroactiveMatches\":{retroMatches}}}";

    // Create a customer with a valid GUID-N style id.
    private static Customer MakeCustomer(long lastSeenAt, string? id = null, string? username = null) => new(
        Id:               id ?? Guid.NewGuid().ToString("N"),
        Platform:         "instagram",
        Username:         username ?? $"user_{lastSeenAt}",
        DisplayName:      $"User {lastSeenAt}",
        AvatarUrl:        null,
        FirstSeenAt:      lastSeenAt - 1,
        LastSeenAt:       lastSeenAt,
        IsBlacklisted:    false,
        BlacklistReason:  null,
        Notes:            null,
        TotalLabelsPrinted: 0,
        TotalAmount:      0m,
        BlacklistedAt:    null,
        Address:          null,
        Phone:            null);

    private sealed record Fixture(
        WpfCustomerProjectionSyncService Svc,
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

        var settingsPath = Path.Combine(Path.GetTempPath(), $"cust-settings-{Guid.NewGuid():N}.json");
        var store = new SettingsStore(settingsPath);

        var handler = new FakeHttpMessageHandler(responder);
        var http    = new HttpClient(handler) { BaseAddress = new Uri("https://test.local") };
        var api     = new LicenseApiClient(http, new LicenseTokenStore());

        var licenseProvider = new FakeLicenseProvider();
        if (seedLicense) licenseProvider.CurrentLicenseKey = TestLicenseKey;

        var svc = new WpfCustomerProjectionSyncService(
            api, customers, store, licenseProvider,
            NullLogger<WpfCustomerProjectionSyncService>.Instance);

        return new Fixture(svc, customers, store, licenseProvider, db);
    }

    private static HttpResponseMessage DefaultResponder(HttpRequestMessage req)
    {
        var path = req.RequestUri!.AbsolutePath;
        if (path == "/api/v1/me/licenses")
            return FakeHttpMessageHandler.Json(200, LicensesJson());
        if (path.Contains("/wpf-customers/sync"))
            return FakeHttpMessageHandler.Json(200, SyncRespJson(synced: 1));
        return FakeHttpMessageHandler.Empty(404);
    }

    [Fact]
    public async Task SyncOnce_no_license_returns_zero()
    {
        var fx = Build(_ => FakeHttpMessageHandler.Empty(200), seedLicense: false);
        using var _d = fx.Db;

        var result = await fx.Svc.SyncOnceAsync(CancellationToken.None);

        result.Should().Be(0, "no license key → nothing to sync");
    }

    [Fact]
    public async Task SyncOnce_no_customers_since_watermark_returns_zero()
    {
        // No customers inserted — repo returns empty → 0 synced, watermark unchanged
        var apiCallCount = 0;
        var fx = Build(req =>
        {
            Interlocked.Increment(ref apiCallCount);
            var path = req.RequestUri!.AbsolutePath;
            if (path == "/api/v1/me/licenses")
                return FakeHttpMessageHandler.Json(200, LicensesJson());
            return FakeHttpMessageHandler.Empty(404);
        });
        using var _d = fx.Db;

        var settingsBefore = fx.Store.Load();
        settingsBefore.LastCustomerProjectionSyncAt = 999L;
        fx.Store.Save(settingsBefore);

        var result = await fx.Svc.SyncOnceAsync(CancellationToken.None);

        result.Should().Be(0);
        var settingsAfter = fx.Store.Load();
        settingsAfter.LastCustomerProjectionSyncAt.Should().Be(999L, "watermark must not change when nothing to sync");
        // No sync call should have been made (only /me/licenses for resolve)
        apiCallCount.Should().BeLessOrEqualTo(1, "only the license resolution GET is allowed");
    }

    [Fact]
    public async Task SyncOnce_pushes_batch_and_advances_watermark()
    {
        // 3 customers with LastSeenAt 100, 200, 300; initial watermark = 0
        int syncPosts = 0;
        var fx = Build(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path == "/api/v1/me/licenses")
                return FakeHttpMessageHandler.Json(200, LicensesJson());
            if (path.Contains("/wpf-customers/sync"))
            {
                Interlocked.Increment(ref syncPosts);
                return FakeHttpMessageHandler.Json(200, SyncRespJson(synced: 3));
            }
            return FakeHttpMessageHandler.Empty(404);
        });
        using var _d = fx.Db;

        fx.Customers.Insert(MakeCustomer(100L));
        fx.Customers.Insert(MakeCustomer(200L));
        fx.Customers.Insert(MakeCustomer(300L));

        var result = await fx.Svc.SyncOnceAsync(CancellationToken.None);

        result.Should().Be(3);
        syncPosts.Should().Be(1, "all 3 fit in one batch");
        var settings = fx.Store.Load();
        settings.LastCustomerProjectionSyncAt.Should().Be(300L, "watermark advances to batch max");
    }

    [Fact]
    public async Task SyncOnce_invalid_guid_id_skipped_with_warning()
    {
        // One customer has a non-GUID id — should be skipped; valid one still synced
        List<string>? capturedIds = null;
        var fx = Build(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path == "/api/v1/me/licenses")
                return FakeHttpMessageHandler.Json(200, LicensesJson());
            if (path.Contains("/wpf-customers/sync"))
            {
                var body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                var doc  = JsonDocument.Parse(body);
                capturedIds = doc.RootElement.GetProperty("customers")
                    .EnumerateArray()
                    .Select(e => e.GetProperty("id").GetString()!)
                    .ToList();
                return FakeHttpMessageHandler.Json(200, SyncRespJson(synced: capturedIds.Count));
            }
            return FakeHttpMessageHandler.Empty(404);
        });
        using var _d = fx.Db;

        var validCustomer = MakeCustomer(100L);
        fx.Customers.Insert(validCustomer);

        // Insert customer with non-parseable GUID id using direct SQL via Dapper
        using var conn = fx.Db.Open();
        conn.Execute(
            @"INSERT INTO Customer (Id, Platform, Username, DisplayName, AvatarUrl,
                FirstSeenAt, LastSeenAt, IsBlacklisted, BlacklistReason, Notes,
                TotalLabelsPrinted, TotalAmount, BlacklistedAt, Address, Phone, RecipientPaysActive)
              VALUES ('not-a-guid', 'instagram', 'bad_user', 'Bad', NULL, 50, 50, 0, NULL, NULL, 0, 0, NULL, NULL, NULL, 0)");

        var result = await fx.Svc.SyncOnceAsync(CancellationToken.None);

        // The valid customer was synced; the invalid one was skipped
        capturedIds.Should().NotBeNull();
        capturedIds!.Should().HaveCount(1, "only the valid-GUID customer should be included in the POST");
    }

    /// <summary>
    /// Sunucu boş Username'li bir kaydı gördüğünde PARTİNİN TAMAMINI 400'le
    /// reddediyor; watermark da hatada ilerlemediği için tek bozuk satır boru
    /// hattını kalıcı kilitliyordu. 2026-08-14'te sahada yaşandı: Facebook App
    /// Review onayından önce açılmış, Username'i boş TEK kayıt 565 müşteriyi 12
    /// gün sunucuya ulaştırmadı.
    ///
    /// Bu test bozuk satırın POST'a hiç girmediğini VE geri kalanın akmaya devam
    /// ettiğini ölçüyor. Watermark kontrolü kritik: eleme çalışsa bile watermark
    /// bozuk satırın ötesine geçmezse kilit ertesi turda geri gelir.
    /// </summary>
    [Fact]
    public async Task SyncOnce_customer_the_server_would_reject_is_skipped()
    {
        List<string>? capturedIds = null;
        var fx = Build(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path == "/api/v1/me/licenses")
                return FakeHttpMessageHandler.Json(200, LicensesJson());
            if (path.Contains("/wpf-customers/sync"))
            {
                var body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                var doc  = JsonDocument.Parse(body);
                capturedIds = doc.RootElement.GetProperty("customers")
                    .EnumerateArray()
                    .Select(e => e.GetProperty("id").GetString()!)
                    .ToList();

                // Sunucunun gerçek davranışını taklit et: bir tanesi bile
                // geçersizse partinin TAMAMI 400. Eleme çalışmazsa bu test
                // "watermark ilerlemedi" diye patlar — asıl korunan davranış bu.
                var anyInvalid = doc.RootElement.GetProperty("customers")
                    .EnumerateArray()
                    .Any(e => string.IsNullOrWhiteSpace(e.GetProperty("username").GetString()));
                return anyInvalid
                    ? FakeHttpMessageHandler.Empty(400)
                    : FakeHttpMessageHandler.Json(200, SyncRespJson(synced: capturedIds.Count));
            }
            return FakeHttpMessageHandler.Empty(404);
        });
        using var _d = fx.Db;

        fx.Customers.Insert(MakeCustomer(100L));

        // Username'i boş kayıt: repo Insert'i böyle bir kaydı üretmiyor olabilir,
        // sahadaki satır da doğrudan ingestor'dan gelmişti — düz SQL ile kur.
        using var conn = fx.Db.Open();
        conn.Execute(
            @"INSERT INTO Customer (Id, Platform, Username, DisplayName, AvatarUrl,
                FirstSeenAt, LastSeenAt, IsBlacklisted, BlacklistReason, Notes,
                TotalLabelsPrinted, TotalAmount, BlacklistedAt, Address, Phone, RecipientPaysActive)
              VALUES (@id, 'facebook', '', NULL, NULL, 190, 200, 0, NULL, NULL, 7, 0, NULL, NULL, NULL, 0)",
            new { id = Guid.NewGuid().ToString("N") });

        fx.Customers.Insert(MakeCustomer(300L));

        var result = await fx.Svc.SyncOnceAsync(CancellationToken.None);

        capturedIds.Should().NotBeNull("parti gönderilmiş olmalı");
        capturedIds!.Should().HaveCount(2, "yalnız Username'i boş kayıt elenmeli");
        result.Should().Be(2);

        fx.Store.Load().LastCustomerProjectionSyncAt.Should().Be(300L,
            "watermark bozuk satırın ÖTESİNE geçmeli, yoksa kilit ertesi turda geri gelir");
    }

    [Fact]
    public async Task SyncOnce_api_failure_does_not_advance_watermark()
    {
        var fx = Build(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path == "/api/v1/me/licenses")
                return FakeHttpMessageHandler.Json(200, LicensesJson());
            if (path.Contains("/wpf-customers/sync"))
                return FakeHttpMessageHandler.Empty(500);
            return FakeHttpMessageHandler.Empty(404);
        });
        using var _d = fx.Db;

        fx.Customers.Insert(MakeCustomer(100L));
        var settingsBefore = fx.Store.Load();
        settingsBefore.LastCustomerProjectionSyncAt = 0L;
        fx.Store.Save(settingsBefore);

        var result = await fx.Svc.SyncOnceAsync(CancellationToken.None);

        result.Should().Be(0, "failed batch returns 0 synced");
        var settingsAfter = fx.Store.Load();
        settingsAfter.LastCustomerProjectionSyncAt.Should().Be(0L,
            "watermark must NOT advance when the API batch fails");
    }

    [Fact]
    public async Task SyncOnce_multi_batch_loops_until_exhausted()
    {
        // Batch size is 500; insert 700 customers → expect exactly 2 batch POSTs
        // and watermark advancing to the max LastSeenAt.
        const int total = 700;
        var postBodies = new List<string>();
        var fx = Build(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path == "/api/v1/me/licenses")
                return FakeHttpMessageHandler.Json(200, LicensesJson());
            if (path.Contains("/wpf-customers/sync"))
            {
                postBodies.Add(req.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                // Return synced = number of items in the batch
                var doc   = JsonDocument.Parse(postBodies.Last());
                var count = doc.RootElement.GetProperty("customers").GetArrayLength();
                return FakeHttpMessageHandler.Json(200, SyncRespJson(synced: count));
            }
            return FakeHttpMessageHandler.Empty(404);
        });
        using var _d = fx.Db;

        for (var i = 1; i <= total; i++)
            fx.Customers.Insert(MakeCustomer((long)i));

        var result = await fx.Svc.SyncOnceAsync(CancellationToken.None);

        result.Should().Be(total, "all 700 customers synced across two batches");
        postBodies.Should().HaveCount(2, "700 customers → batch1=500 + batch2=200");

        var settings = fx.Store.Load();
        settings.LastCustomerProjectionSyncAt.Should().Be(total,
            "watermark advances to max LastSeenAt after both batches");
    }

    /// <summary>
    /// F07 (2026-09-09 denetimi): aynı saniyeye BatchSize'dan (500) fazla satır
    /// düştüğünde eski yalnız-zaman imleci sayfa sınırındaki satırları SONSUZA
    /// DEK atlıyordu — ilk sayfa 500 döner, watermark o saniyeye ilerler, kalan
    /// satırlar <c>LastSeenAt &gt; @since</c> filtresine takılır (kanıt: 501
    /// satırda 1 kayıp, probe-results.txt CUSTOMER_CURSOR). Toplu içe aktarma
    /// ve saat düzeltmesi bu deseni gerçek hayatta üretir. Bileşik imleç
    /// (LastSeenAt, Id) ile hepsi akmalı.
    /// </summary>
    [Fact]
    public async Task SyncOnce_501_ayni_saniye_satirda_hicbiri_atlanmaz()
    {
        const int total = 501;
        const long sameSecond = 1_000L;
        var postedIds = new List<string>();
        var fx = Build(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path == "/api/v1/me/licenses")
                return FakeHttpMessageHandler.Json(200, LicensesJson());
            if (path.Contains("/wpf-customers/sync"))
            {
                var body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                var doc  = JsonDocument.Parse(body);
                var ids  = doc.RootElement.GetProperty("customers")
                    .EnumerateArray()
                    .Select(e => e.GetProperty("id").GetString()!)
                    .ToList();
                postedIds.AddRange(ids);
                return FakeHttpMessageHandler.Json(200, SyncRespJson(synced: ids.Count));
            }
            return FakeHttpMessageHandler.Empty(404);
        });
        using var _d = fx.Db;

        for (var i = 0; i < total; i++)
            fx.Customers.Insert(MakeCustomer(sameSecond, username: $"same_sec_{i}"));

        var result = await fx.Svc.SyncOnceAsync(CancellationToken.None);

        result.Should().Be(total, "aynı saniyedeki satırların HİÇBİRİ atlanmamalı");
        postedIds.Distinct().Should().HaveCount(total,
            "501 satır → sayfa1=500 + sayfa2=1; eski imleçte 501. satır kayboluyordu");

        var settings = fx.Store.Load();
        settings.LastCustomerProjectionSyncAt.Should().Be(sameSecond);
        settings.LastCustomerProjectionSyncId.Should().NotBeNullOrEmpty(
            "imlecin Id yarısı da kalıcılaşmalı — bir sonraki tur kalınan satırdan devam eder");
    }

    /// <summary>
    /// Eski (yalnız-zaman) imleçten yükseltme: Id ayarı boşken watermark &gt; 0
    /// ise geçmişte atlanmış satırlar watermark'ın ALTINDA kalmıştır — bileşik
    /// imleç ileriye dönük korur ama geçmişi kurtaramaz. Servis watermark'ı bir
    /// kez 0'a çekip tam tarama yapmalı (sunucu upsert'i idempotent).
    /// </summary>
    [Fact]
    public async Task SyncOnce_eski_imlecten_gecis_tam_tarama_yapar()
    {
        var postedIds = new List<string>();
        var fx = Build(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path == "/api/v1/me/licenses")
                return FakeHttpMessageHandler.Json(200, LicensesJson());
            if (path.Contains("/wpf-customers/sync"))
            {
                var body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                var doc  = JsonDocument.Parse(body);
                postedIds.AddRange(doc.RootElement.GetProperty("customers")
                    .EnumerateArray()
                    .Select(e => e.GetProperty("id").GetString()!));
                return FakeHttpMessageHandler.Json(200, SyncRespJson(synced: postedIds.Count));
            }
            return FakeHttpMessageHandler.Empty(404);
        });
        using var _d = fx.Db;

        // Eski imleç 500'de takılı, Id ayarı hiç yazılmamış — bu kurulum eski
        // sürümle çalışmış. 100'deki satır eski dünyada atlanmış bir satırı
        // temsil ediyor: watermark'ın altında ama sunucuya hiç gitmemiş.
        fx.Customers.Insert(MakeCustomer(100L));
        var settingsBefore = fx.Store.Load();
        settingsBefore.LastCustomerProjectionSyncAt = 500L;
        settingsBefore.LastCustomerProjectionSyncId = "";
        fx.Store.Save(settingsBefore);

        var result = await fx.Svc.SyncOnceAsync(CancellationToken.None);

        result.Should().Be(1, "tam tarama watermark altındaki kayıp satırı kurtarmalı");

        var settings = fx.Store.Load();
        settings.LastCustomerProjectionSyncId.Should().NotBeNullOrEmpty();
        settings.LastCustomerProjectionSyncAt.Should().Be(100L,
            "tarama sonrası imleç gerçek son satıra oturur; Id yarısı dolu olduğu için sıfırlama tekrarlanmaz");
    }

    /// <summary>Sıfırlamanın TEK SEFERLİK olduğunu sabitler: Id yarısı doluysa
    /// watermark altındaki satırlar yeniden gönderilMEZ (delta semantiği).</summary>
    [Fact]
    public async Task SyncOnce_id_imleci_doluysa_tam_tarama_tekrarlanmaz()
    {
        var syncPosts = 0;
        var fx = Build(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path == "/api/v1/me/licenses")
                return FakeHttpMessageHandler.Json(200, LicensesJson());
            if (path.Contains("/wpf-customers/sync"))
            {
                Interlocked.Increment(ref syncPosts);
                return FakeHttpMessageHandler.Json(200, SyncRespJson(synced: 1));
            }
            return FakeHttpMessageHandler.Empty(404);
        });
        using var _d = fx.Db;

        fx.Customers.Insert(MakeCustomer(100L));
        var settingsBefore = fx.Store.Load();
        settingsBefore.LastCustomerProjectionSyncAt = 500L;
        settingsBefore.LastCustomerProjectionSyncId = Guid.NewGuid().ToString("N");
        fx.Store.Save(settingsBefore);

        var result = await fx.Svc.SyncOnceAsync(CancellationToken.None);

        result.Should().Be(0, "imleç zaten yeni biçimde — watermark altı yeniden taranmaz");
        syncPosts.Should().Be(0);
        fx.Store.Load().LastCustomerProjectionSyncAt.Should().Be(500L);
    }
}
