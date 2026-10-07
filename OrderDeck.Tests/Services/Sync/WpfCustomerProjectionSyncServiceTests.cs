using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using OrderDeck.App.Services.Sync;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Licensing.Api;
using OrderDeck.Licensing.Api.Models;
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
        CustomerSyncRepository Sync,
        SyncCursorRepository Cursors,
        FakeLicenseProvider License,
        InMemorySqlite Db)
    {
        /// <summary>R6-04: imlecin kalıcı evi SyncCursor tablosu.
        /// R9-D01: settings.json'daki eski alan artık tohum olarak da
        /// okunmuyor — tek kalıcı kaynak bu satır.</summary>
        public long CursorSeq(string licenseKey = TestLicenseKey) =>
            Cursors.Get(WpfCustomerProjectionSyncService.CursorName, licenseKey)?.Seq ?? 0L;

        /// <summary>Son satırın SyncSeq'i. Göç 045'ten beri ekleme satır başına İKİ numara
        /// alır (036'nın ekleme tetikleyicisi + damga tetikleyicisinin iç UPDATE'i): satır
        /// sayısı imleç değeri değildir.</summary>
        public long MaxSyncSeq()
        {
            using var conn = Db.Open();
            return conn.ExecuteScalar<long>("SELECT COALESCE(MAX(SyncSeq), 0) FROM Customer");
        }
    }

    private sealed class FixedClock : OrderDeck.Core.Time.IClock
    {
        public long UnixNow() => 1_791_000_000L;
    }

    private static Fixture Build(
        Func<HttpRequestMessage, HttpResponseMessage> responder,
        bool seedLicense = true,
        CustomerBusySet? busy = null)
        => Build(req => Task.FromResult(responder(req)), seedLicense, busy);

    /// <param name="busy">Ödeme akışındaki müşteriler (U13); null = hiçbir müşteri meşgul değil.</param>
    private static Fixture Build(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> responder,
        bool seedLicense = true,
        CustomerBusySet? busy = null)
    {
        var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var customers = new CustomerRepository(db);
        var cursors   = new SyncCursorRepository(db);
        var sync      = new CustomerSyncRepository(db, busy);

        var handler = new FakeHttpMessageHandler(responder);
        var http    = new HttpClient(handler) { BaseAddress = new Uri("https://test.local") };
        var api     = new LicenseApiClient(http, new LicenseTokenStore());

        var licenseProvider = new FakeLicenseProvider();
        if (seedLicense) licenseProvider.CurrentLicenseKey = TestLicenseKey;

        var svc = new WpfCustomerProjectionSyncService(
            api, sync, cursors, licenseProvider, new FixedClock(),
            NullLogger<WpfCustomerProjectionSyncService>.Instance);

        return new Fixture(svc, customers, sync, cursors, licenseProvider, db);
    }

    /// <summary>Ham varlık denetimi (C8'den sonra <c>GetById</c> yönlendirmeyi izler — U12).</summary>
    private static bool Exists(Fixture fx, string id)
    {
        using var c = fx.Db.Open();
        return c.ExecuteScalar<int>("SELECT COUNT(*) FROM Customer WHERE Id = @id", new { id }) == 1;
    }

    private static long Seq(Fixture fx, string id)
    {
        using var c = fx.Db.Open();
        return c.ExecuteScalar<long>("SELECT SyncSeq FROM Customer WHERE Id = @id", new { id });
    }

    private static long? PurgedAt(Fixture fx, string id)
    {
        using var c = fx.Db.Open();
        return c.ExecuteScalar<long?>("SELECT PurgedAt FROM Customer WHERE Id = @id", new { id });
    }

    /// <summary>Gönderilen partideki Id'ler (gönderim sırasıyla).</summary>
    private static List<Guid> PostedIds(HttpRequestMessage req)
        => JsonDocument.Parse(req.Content!.ReadAsStringAsync().GetAwaiter().GetResult())
            .RootElement.GetProperty("customers").EnumerateArray()
            .Select(e => Guid.Parse(e.GetProperty("id").GetString()!))
            .ToList();

    /// <summary>Sunucu yanıtı: <paramref name="copy"/> → <paramref name="canonical"/> yönlendirmesi (S6/S7).</summary>
    private static string RedirectRespJson(Guid copy, Guid canonical, int synced = 1) =>
        $$"""{"synced":{{synced}},"retroactiveMatches":0,"redirects":[{"id":"{{copy}}","canonicalId":"{{canonical}}"}]}""";

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

        fx.Cursors.Upsert(WpfCustomerProjectionSyncService.CursorName, TestLicenseKey, seq: 999L);

        var result = await fx.Svc.SyncOnceAsync(CancellationToken.None);

        result.Should().Be(0);
        fx.CursorSeq().Should().Be(999L, "imleç boş turda İLERLEMEZ");
        // No sync call should have been made (only /me/licenses for resolve)
        apiCallCount.Should().BeLessOrEqualTo(1, "only the license resolution GET is allowed");
    }

    [Fact]
    public async Task SyncOnce_pushes_batch_and_advances_watermark()
    {
        // 3 customers with LastSeenAt 100, 200, 300; initial watermark = 0
        int syncPosts = 0;
        var postedIds = new List<Guid>();
        var fx = Build(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path == "/api/v1/me/licenses")
                return FakeHttpMessageHandler.Json(200, LicensesJson());
            if (path.Contains("/wpf-customers/sync"))
            {
                Interlocked.Increment(ref syncPosts);
                var body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                postedIds.AddRange(JsonDocument.Parse(body).RootElement.GetProperty("customers")
                    .EnumerateArray()
                    .Select(e => Guid.Parse(e.GetProperty("id").GetString()!)));
                return FakeHttpMessageHandler.Json(200, SyncRespJson(synced: 3));
            }
            return FakeHttpMessageHandler.Empty(404);
        });
        using var _d = fx.Db;

        var customers = new[] { MakeCustomer(100L), MakeCustomer(200L), MakeCustomer(300L) };
        foreach (var c in customers) fx.Customers.Insert(c);
        var expectedCursor = fx.MaxSyncSeq();     // gönderimden ÖNCE: son satırın SyncSeq'i

        var result = await fx.Svc.SyncOnceAsync(CancellationToken.None);

        result.Should().Be(3);
        syncPosts.Should().Be(1, "all 3 fit in one batch");
        postedIds.Should().BeEquivalentTo(customers.Select(c => Guid.Parse(c.Id)),
            "the batch carries exactly the 3 customers");
        fx.CursorSeq().Should().Be(expectedCursor, "watermark advances to the batch max SyncSeq");
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

        var first = MakeCustomer(100L);
        fx.Customers.Insert(first);

        // Username'i boş kayıt: repo Insert'i böyle bir kaydı üretmiyor olabilir,
        // sahadaki satır da doğrudan ingestor'dan gelmişti — düz SQL ile kur.
        using var conn = fx.Db.Open();
        conn.Execute(
            @"INSERT INTO Customer (Id, Platform, Username, DisplayName, AvatarUrl,
                FirstSeenAt, LastSeenAt, IsBlacklisted, BlacklistReason, Notes,
                TotalLabelsPrinted, TotalAmount, BlacklistedAt, Address, Phone, RecipientPaysActive)
              VALUES (@id, 'facebook', '', NULL, NULL, 190, 200, 0, NULL, NULL, 7, 0, NULL, NULL, NULL, 0)",
            new { id = Guid.NewGuid().ToString("N") });

        var last = MakeCustomer(300L);
        fx.Customers.Insert(last);
        var expectedCursor = fx.MaxSyncSeq();     // gönderimden ÖNCE: son (geçerli) satırın SyncSeq'i

        var result = await fx.Svc.SyncOnceAsync(CancellationToken.None);

        capturedIds.Should().NotBeNull("parti gönderilmiş olmalı");
        capturedIds!.Should().HaveCount(2, "yalnız Username'i boş kayıt elenmeli");
        capturedIds!.Select(Guid.Parse).Should().BeEquivalentTo(
            new[] { Guid.Parse(first.Id), Guid.Parse(last.Id) }, "giden iki satır geçerli olanlar");
        result.Should().Be(2);

        fx.CursorSeq().Should().Be(expectedCursor,
            "watermark bozuk satırın ÖTESİNE geçmeli, yoksa kilit ertesi turda geri gelir");
    }

    /// <summary>
    /// Biçim 2 (Bölüm C, kural 5): ad ve takma ad AYRI birimler. R3-02'nin "FullName boşsa
    /// DisplayName" geri düşüşü yalnız eski (biçim 1) sözleşmeydi; biçim 2'de takma ad gerçek ad
    /// diye gitseydi sunucuda damgasız bir gerçek ad gibi boş ad birimini doldururdu. Takma ad
    /// kendi birimiyle ve damgasıyla gider.
    /// </summary>
    [Fact]
    public async Task SyncOnce_bicim_2_ad_ve_takma_ad_ayri_birim_takma_ad_FullName_diye_gitmez()
    {
        JsonElement? sent = null;
        var fx = Build(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path == "/api/v1/me/licenses") return FakeHttpMessageHandler.Json(200, LicensesJson());
            if (path.Contains("/wpf-customers/sync"))
            {
                sent = JsonDocument.Parse(req.Content!.ReadAsStringAsync().GetAwaiter().GetResult())
                    .RootElement.GetProperty("customers").Clone();
                return FakeHttpMessageHandler.Json(200, SyncRespJson(synced: 2));
            }
            return FakeHttpMessageHandler.Empty(404);
        });
        using var _d = fx.Db;
        fx.Customers.Insert(MakeCustomer(1000, username: "takma_ad") with { FullName = "Örnek Müşteri" });
        fx.Customers.Insert(MakeCustomer(1001, username: "sadece_takma") with { DisplayName = "Sadece Takma" });

        await fx.Svc.SyncOnceAsync(CancellationToken.None);

        var items = sent!.Value.EnumerateArray().ToDictionary(e => e.GetProperty("username").GetString()!);
        items["takma_ad"].GetProperty("format").GetInt32().Should().Be(2);
        items["takma_ad"].GetProperty("fullName").GetString().Should().Be("Örnek Müşteri");
        items["sadece_takma"].GetProperty("fullName").ValueKind.Should().Be(JsonValueKind.Null,
            "biçim 2'de takma ad gerçek ad diye gitmez (R3-02 geri düşüşü kalktı)");
        items["sadece_takma"].GetProperty("displayName").GetString().Should().Be("Sadece Takma");
        items["sadece_takma"].GetProperty("displayNameChangedAt").ValueKind.Should().Be(JsonValueKind.String);
    }

    [Fact]
    public async Task SyncOnce_tum_birimleri_ve_ms_damgalarini_gonderir()
    {
        JsonElement? item = null;
        var fx = Build(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path == "/api/v1/me/licenses") return FakeHttpMessageHandler.Json(200, LicensesJson());
            if (path.Contains("/wpf-customers/sync"))
            {
                item = JsonDocument.Parse(req.Content!.ReadAsStringAsync().GetAwaiter().GetResult())
                    .RootElement.GetProperty("customers")[0].Clone();
                return FakeHttpMessageHandler.Json(200, SyncRespJson(synced: 1));
            }
            return FakeHttpMessageHandler.Empty(404);
        });
        using var _d = fx.Db;
        var tckn = TestTckn.NewValid();
        var c = MakeCustomer(1000) with
        {
            City = "İzmir", District = "Bornova", Email = "ornek@example.test", Tckn = tckn,
            IsBlacklisted = true, BlacklistReason = "ödemedi", BlacklistedAt = 1_759_000_000, SmsConsent = true,
        };
        fx.Customers.Insert(c);
        fx.Customers.UpdateNotes(c.Id, "kapıya");
        using (var conn = fx.Db.Open())
            conn.Execute("UPDATE Customer SET NotesChangedAt = 1759312800123 WHERE Id = @id", new { id = c.Id });

        await fx.Svc.SyncOnceAsync(CancellationToken.None);

        var i = item!.Value;
        i.GetProperty("city").GetString().Should().Be("İzmir");
        i.GetProperty("tckn").GetString().Should().Be(tckn, "TCKN düz gider, sunucu şifreler");
        i.GetProperty("isBlacklisted").GetBoolean().Should().BeTrue();
        i.GetProperty("blacklistedAt").GetDateTimeOffset().ToUnixTimeSeconds().Should().Be(1_759_000_000);
        i.GetProperty("smsConsentChangedAt").ValueKind.Should().Be(JsonValueKind.String, "dolu birim eklemede damgalanır");
        i.GetProperty("notesChangedAt").GetDateTimeOffset().ToUnixTimeMilliseconds().Should().Be(1_759_312_800_123L);
        i.GetProperty("fullNameChangedAt").ValueKind.Should().Be(JsonValueKind.Null);
    }

    /// <summary>
    /// C5 incelemesi: <see cref="WpfCustomerSyncItem"/>'in 26 isteğe bağlı sondaki parametresi
    /// yüzünden öğe kurulumunda unutulan bir adlandırılmış argüman DERLENİR ve biçim 2'de sessiz
    /// hata olur — değeri olmadan giden damga sunucuda değeri SİLER (adres bloğunda il/ilçe dahil),
    /// damgası olmadan giden değer yalnız boşu doldurur, hiçbir bilgisayara yayılmaz. Tam dolu
    /// bir satırın gönderilen öğesinde 33 özelliğin HEPSİ dolu (null/varsayılan değil) ve doğru
    /// eşlenmiş olmalı. Her birimin damgası ayrı: iki birimin damgası karışırsa da test düşer.
    /// Öğe ağdan geçen gövdeden okunur — serileştirmede düşen alan da yakalanır.
    /// </summary>
    [Fact]
    public async Task SyncOnce_tam_dolu_satirin_33_ozelligi_eksiksiz_ve_dogru_eslenir()
    {
        string? body = null;
        var fx = Build(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path == "/api/v1/me/licenses") return FakeHttpMessageHandler.Json(200, LicensesJson());
            if (path.Contains("/wpf-customers/sync"))
            {
                body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return FakeHttpMessageHandler.Json(200, SyncRespJson(synced: 1));
            }
            return FakeHttpMessageHandler.Empty(404);
        });
        using var _d = fx.Db;
        var phone = TestPhone.NewE164();
        var tckn = TestTckn.NewValid();
        var groupId = Guid.NewGuid().ToString("N");
        const long blacklistedAtSeconds = 1_759_000_000L;
        var c = MakeCustomer(1_791_000_123L, username: "ornekmusteri") with
        {
            FullName = "Örnek Müşteri", DisplayName = "ornek_takma", GroupId = groupId,
            Address = "Deneme Mah. Örnek Sk. No 1", City = "İzmir", District = "Bornova",
            RecipientPaysActive = true, Phone = phone, Email = "ornek@example.test", Tckn = tckn,
            WhatsAppConsent = true, SmsConsent = true,
            IsBlacklisted = true, BlacklistReason = "ödemedi", BlacklistedAt = blacklistedAtSeconds,
            Notes = "kapıya bırak",
        };
        fx.Customers.Insert(c);
        // Yalnız damga kolonları yazılır: hiçbir birim değeri değişmediği için damga tetikleyicileri
        // çalışmaz, değerler aynen kalır (ms hassasiyetinde, birbirinden farklı).
        const long t = 1_759_312_800_000L;
        using (var conn = fx.Db.Open())
            conn.Execute(@"
                UPDATE Customer SET
                    FullNameChangedAt = @t + 1, DisplayNameChangedAt = @t + 2, GroupIdChangedAt = @t + 3,
                    AddressChangedAt = @t + 4, RecipientPaysChangedAt = @t + 5, PhoneChangedAt = @t + 6,
                    EmailChangedAt = @t + 7, TcknChangedAt = @t + 8, WhatsAppConsentChangedAt = @t + 9,
                    SmsConsentChangedAt = @t + 10, BlacklistChangedAt = @t + 11, NotesChangedAt = @t + 12
                WHERE Id = @id", new { t, id = c.Id });

        await fx.Svc.SyncOnceAsync(CancellationToken.None);

        body.Should().NotBeNull("parti gönderilmiş olmalı");
        var item = JsonSerializer.Deserialize<WpfCustomerSyncRequest>(
            body!, new JsonSerializerOptions(JsonSerializerDefaults.Web))!.Customers.Single();

        // 1) Hiçbir özellik boş ya da varsayılan değerde değil: unutulan argüman burada düşer.
        var props = typeof(WpfCustomerSyncItem).GetProperties(BindingFlags.Public | BindingFlags.Instance);
        props.Should().HaveCount(33, "sözleşme 33 alan (C5); alan eklenirse öğe kurulumu ve bu test birlikte güncellenir");
        foreach (var p in props)
        {
            var value = p.GetValue(item);
            value.Should().NotBeNull($"{p.Name} gönderilmeli");
            if (p.PropertyType.IsValueType)
                value.Should().NotBe(Activator.CreateInstance(p.PropertyType), $"{p.Name} varsayılan değerde kalmamalı");
        }

        // 2) Her özellik doğru kaynaktan: damgalar yerel unix MS ↔ DateTimeOffset, BlacklistedAt unix SANİYE.
        static DateTimeOffset Ms(long v) => DateTimeOffset.FromUnixTimeMilliseconds(v);
        item.Id.Should().Be(Guid.Parse(c.Id));
        item.Platform.Should().Be("instagram");
        item.Username.Should().Be("ornekmusteri");
        item.FullName.Should().Be("Örnek Müşteri");
        item.Phone.Should().Be(phone);
        item.Address.Should().Be("Deneme Mah. Örnek Sk. No 1");
        item.UpdatedAt.Should().Be(DateTimeOffset.FromUnixTimeSeconds(1_791_000_123L), "eski okuyucular için iş zamanı");
        item.Format.Should().Be(2);
        item.FullNameChangedAt.Should().Be(Ms(t + 1));
        item.DisplayName.Should().Be("ornek_takma");
        item.DisplayNameChangedAt.Should().Be(Ms(t + 2));
        item.GroupId.Should().Be(groupId);
        item.GroupIdChangedAt.Should().Be(Ms(t + 3));
        item.City.Should().Be("İzmir");
        item.District.Should().Be("Bornova");
        item.AddressChangedAt.Should().Be(Ms(t + 4));
        item.RecipientPaysActive.Should().BeTrue();
        item.RecipientPaysChangedAt.Should().Be(Ms(t + 5));
        item.PhoneChangedAt.Should().Be(Ms(t + 6));
        item.Email.Should().Be("ornek@example.test");
        item.EmailChangedAt.Should().Be(Ms(t + 7));
        item.Tckn.Should().Be(tckn, "TCKN düz gider, sunucu şifreler");
        item.TcknChangedAt.Should().Be(Ms(t + 8));
        item.WhatsAppConsent.Should().BeTrue();
        item.WhatsAppConsentChangedAt.Should().Be(Ms(t + 9));
        item.SmsConsent.Should().BeTrue();
        item.SmsConsentChangedAt.Should().Be(Ms(t + 10));
        item.IsBlacklisted.Should().BeTrue();
        item.BlacklistReason.Should().Be("ödemedi");
        item.BlacklistedAt.Should().Be(DateTimeOffset.FromUnixTimeSeconds(blacklistedAtSeconds),
            "yerelde unix SANİYE (Customer tablosuyla aynı), damgalar gibi ms değil");
        item.BlacklistChangedAt.Should().Be(Ms(t + 11));
        item.Notes.Should().Be("kapıya bırak");
        item.NotesChangedAt.Should().Be(Ms(t + 12));
    }

    /// <summary>
    /// C4 kalite incelemesi: yerelde silinmiş satır (<c>PurgedAt</c> dolu) gönderilmez. KVKK kararı
    /// inmiş bir kimliğe sohbetten gelen yorum satırı açar; mezar taşı boşaltması "[Silindi]"yi şimdi
    /// damgasıyla yazar — gitseydi sunucudaki asıl kayıt silinmemişse gerçek takma adı her
    /// bilgisayarda ezerdi. Atlama GUID olmayan satırlarınki gibi atlama döngüsünün İÇİNDE: imleç
    /// atlanan satırın üstünden geçer — geçmeseydi silinmiş bir kimlik sahibi akışı U5'te sonsuza
    /// dek durdururdu (gönderilmemiş sayılır).
    /// </summary>
    [Fact]
    public async Task SyncOnce_yerelde_silinmis_satir_gonderilmez_imlec_ustunden_gecer()
    {
        var postedIds = new List<Guid>();
        var fx = Build(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path == "/api/v1/me/licenses") return FakeHttpMessageHandler.Json(200, LicensesJson());
            if (path.Contains("/wpf-customers/sync"))
            {
                var ids = PostedIds(req);
                postedIds.AddRange(ids);
                return FakeHttpMessageHandler.Json(200, SyncRespJson(synced: ids.Count));
            }
            return FakeHttpMessageHandler.Empty(404);
        });
        using var _d = fx.Db;
        var live = MakeCustomer(100L);
        fx.Customers.Insert(live);
        fx.Customers.RecordPurge("instagram", "silinen_kisi", purgedAtUnix: 1_790_000_000L);
        var purged = MakeCustomer(200L, username: "silinen_kisi");
        fx.Customers.Insert(purged);                       // mezar taşı: satır boş ve PurgedAt'li doğar
        var expectedCursor = fx.MaxSyncSeq();
        PurgedAt(fx, purged.Id).Should().NotBeNull("ön koşul: satır yerelde silinmiş");
        Seq(fx, purged.Id).Should().Be(expectedCursor, "ön koşul: silinmiş satır partinin son satırı");

        var result = await fx.Svc.SyncOnceAsync(CancellationToken.None);

        postedIds.Should().Equal(new[] { Guid.Parse(live.Id) }, "silinmiş satır partiye girmez");
        result.Should().Be(1);
        fx.CursorSeq().Should().Be(expectedCursor, "imleç atlanan silinmiş satırın üstünden geçer");
    }

    /// <summary>Partide yalnız silinmiş satır kaldıysa istek hiç atılmaz, imleç yine ilerler
    /// (atlama döngüsünün "hepsi elendi" dalı). Satır, henüz gönderilmemişken akıştan inen KVKK
    /// silmesiyle boşaltıldı: silme kilit altında yazıldığı için SyncSeq'i eklemedeki değerinde,
    /// imlecin üstünde kalır.</summary>
    [Fact]
    public async Task SyncOnce_partide_yalniz_silinmis_satir_varsa_istek_atilmaz_imlec_ilerler()
    {
        var syncPosts = 0;
        var fx = Build(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path == "/api/v1/me/licenses") return FakeHttpMessageHandler.Json(200, LicensesJson());
            if (path.Contains("/wpf-customers/sync"))
            {
                Interlocked.Increment(ref syncPosts);
                return FakeHttpMessageHandler.Json(200, SyncRespJson(synced: 1));
            }
            return FakeHttpMessageHandler.Empty(404);
        });
        using var _d = fx.Db;
        var c = MakeCustomer(100L, username: "silinen_kisi");
        fx.Customers.Insert(c);
        fx.Customers.RecordPurge("instagram", "silinen_kisi", purgedAtUnix: 1_790_000_000L);
        var expectedCursor = fx.MaxSyncSeq();
        PurgedAt(fx, c.Id).Should().NotBeNull("ön koşul: satır yerelde silinmiş");

        var result = await fx.Svc.SyncOnceAsync(CancellationToken.None);

        result.Should().Be(0);
        syncPosts.Should().Be(0, "gönderilecek satır yok");
        fx.CursorSeq().Should().Be(expectedCursor, "imleç atlanan satırın üstünden geçer, bir sonraki tur onu yeniden okumaz");
    }

    /// <summary>U15: biçim-2 gönderimi önceki sürümün imlecini ne tohum olarak okur ne de yazar.
    /// Okusaydı önceki sürümün biçim 1 ile (damgasız) gönderdiği satırlar biçim 2 ile hiç gitmez,
    /// eski sürümün aradaki düzenlemeleri sunucuda damgasız kalırdı; yazsaydı önceki sürüme dönüşte
    /// o sürüm kaldığı yeri kaybederdi. Eski ad bu yüzden burada bilerek düz metin.</summary>
    [Fact]
    public async Task SyncOnce_onceki_surumun_imlecini_okumaz_ve_bozmaz()
    {
        WpfCustomerProjectionSyncService.CursorName.Should().Be("customer-projection-out-v2");
        var postedIds = new List<Guid>();
        var fx = Build(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path == "/api/v1/me/licenses") return FakeHttpMessageHandler.Json(200, LicensesJson());
            if (path.Contains("/wpf-customers/sync"))
            {
                var ids = PostedIds(req);
                postedIds.AddRange(ids);
                return FakeHttpMessageHandler.Json(200, SyncRespJson(synced: ids.Count));
            }
            return FakeHttpMessageHandler.Empty(404);
        });
        using var _d = fx.Db;
        var c = MakeCustomer(100L);
        fx.Customers.Insert(c);
        const string previousVersionCursor = "customer-projection-out";
        fx.Cursors.Upsert(previousVersionCursor, TestLicenseKey, seq: 999L);
        var expectedCursor = fx.MaxSyncSeq();

        await fx.Svc.SyncOnceAsync(CancellationToken.None);

        postedIds.Should().Equal(new[] { Guid.Parse(c.Id) }, "biçim-2 imleci yok → tam tarama");
        fx.CursorSeq().Should().Be(expectedCursor);
        fx.Cursors.Get(previousVersionCursor, TestLicenseKey)!.Seq.Should().Be(999L, "önceki sürümün imleci yerinde kalır");
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

        var result = await fx.Svc.SyncOnceAsync(CancellationToken.None);

        result.Should().Be(0, "failed batch returns 0 synced");
        fx.Cursors.Get(WpfCustomerProjectionSyncService.CursorName, TestLicenseKey).Should().BeNull(
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
        var expectedCursor = fx.MaxSyncSeq();     // gönderimden ÖNCE: son satırın SyncSeq'i

        var result = await fx.Svc.SyncOnceAsync(CancellationToken.None);

        result.Should().Be(total, "all 700 customers synced across two batches");
        postBodies.Should().HaveCount(2, "700 customers → batch1=500 + batch2=200");
        postBodies
            .SelectMany(b => JsonDocument.Parse(b).RootElement.GetProperty("customers")
                .EnumerateArray()
                .Select(e => e.GetProperty("id").GetString()!))
            .Distinct().Should().HaveCount(total, "every customer goes exactly once — none skipped");

        fx.CursorSeq().Should().Be(expectedCursor,
            "watermark advances to the last row's SyncSeq after both batches");
    }

    /// <summary>
    /// F07 (2026-09-09 denetimi): aynı saniyeye BatchSize'dan (500) fazla satır
    /// düştüğünde eski yalnız-zaman imleci sayfa sınırındaki satırları SONSUZA
    /// DEK atlıyordu — ilk sayfa 500 döner, watermark o saniyeye ilerler, kalan
    /// satırlar <c>LastSeenAt &gt; @since</c> filtresine takılır (kanıt: 501
    /// satırda 1 kayıp, probe-results.txt CUSTOMER_CURSOR). Toplu içe aktarma
    /// ve saat düzeltmesi bu deseni gerçek hayatta üretir. N03-g'den sonra imleç
    /// SyncSeq ve SyncSeq benzersiz olduğu için "aynı değere sahip iki satır"
    /// hâli yok — sebep yapısal olarak ortadan kalktı, test nöbette kalıyor.
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
        var expectedCursor = fx.MaxSyncSeq();     // gönderimden ÖNCE: son satırın SyncSeq'i

        var result = await fx.Svc.SyncOnceAsync(CancellationToken.None);

        result.Should().Be(total, "aynı saniyedeki satırların HİÇBİRİ atlanmamalı");
        postedIds.Distinct().Should().HaveCount(total,
            "501 satır → sayfa1=500 + sayfa2=1; eski imleçte 501. satır kayboluyordu");

        fx.CursorSeq().Should().Be(expectedCursor,
            "imleç son satırın SyncSeq'ine oturmalı — bir sonraki tur oradan devam eder");
    }

    /// <summary>
    /// R9-D01: settings.json'dan imleç tohumlama TAMAMEN kalktı — SyncCursor
    /// satırı yoksa tam tarama yapılır. Bilerek: eski dünyada imlecin ALTINDA
    /// kalıp kalıcı kaybedilmiş satırlar ancak tam taramayla kurtulur. Sunucu
    /// upsert'i idempotent ve <c>PurgedAt</c> kapılı olduğu için bir tur fazla
    /// trafikten başka maliyeti yok; silinmiş kişisel veri geri gelmez.
    /// </summary>
    [Fact]
    public async Task SyncOnce_imlec_satiri_yoksa_tam_tarama_yapar()
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

        // 100'deki satır eski dünyada atlanmış bir satırı temsil ediyor: eski
        // watermark'ın altında ama sunucuya hiç gitmemiş. İmleç satırı YOK →
        // watermark 0'dan tam tarama, satır kurtulur.
        var lost = MakeCustomer(100L);
        fx.Customers.Insert(lost);
        var expectedCursor = fx.MaxSyncSeq();     // gönderimden ÖNCE: satırın SyncSeq'i

        var result = await fx.Svc.SyncOnceAsync(CancellationToken.None);

        result.Should().Be(1, "tam tarama eski imlecin altındaki kayıp satırı kurtarmalı");
        postedIds.Select(Guid.Parse).Should().Equal(Guid.Parse(lost.Id));
        fx.CursorSeq().Should().Be(expectedCursor,
            "tarama sonrası imleç gerçek son satırın SyncSeq'ine oturur");
    }

    /// <summary>Tam taramanın TEK SEFERLİK olduğunu sabitler: imleç bir kez
    /// yazıldıktan sonra altındaki satırlar yeniden gönderilMEZ (delta semantiği).</summary>
    [Fact]
    public async Task SyncOnce_imlec_yazildiktan_sonra_tam_tarama_tekrarlanmaz()
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
        fx.Cursors.Upsert(WpfCustomerProjectionSyncService.CursorName, TestLicenseKey, seq: 500L);

        var result = await fx.Svc.SyncOnceAsync(CancellationToken.None);

        result.Should().Be(0, "imleç satırın üzerinde — altı yeniden taranmaz");
        syncPosts.Should().Be(0);
        fx.CursorSeq().Should().Be(500L, "boş turda imleç yerinde kalır");
    }

    // ─── R6-04: imleç ↔ veri nesli bağı ──────────────────────────────────────

    /// <summary>
    /// R6-04'ün asıl senaryosu (denetim deneyi: watermark 21, verideki en
    /// büyük SyncSeq 2 → sonsuza dek 0 satır). Eski yedek geri yüklendiğinde
    /// imleç veriyle AYNI dosyada döner — sync kaldığı yerden devam eder.
    /// R9-D01: settings.json artık hiç okunmadığı için diskte kalan bayat-ileri
    /// değerin kazanma ihtimali yapısal olarak yok.
    /// </summary>
    [Fact]
    public async Task SyncOnce_geri_yukleme_sonrasi_db_imleci_kazanir_settings_yok_sayilir()
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

        // Geri yüklenen veritabanı: 3 müşteri + kendi imleci (ikinci satırın SyncSeq'i).
        fx.Customers.Insert(MakeCustomer(100L));
        var second = MakeCustomer(200L);
        fx.Customers.Insert(second);
        var third = MakeCustomer(300L);
        fx.Customers.Insert(third);
        fx.Cursors.Upsert(WpfCustomerProjectionSyncService.CursorName, TestLicenseKey,
            seq: fx.Customers.GetById(second.Id)!.SyncSeq);
        var expectedCursor = fx.MaxSyncSeq();     // gönderimden ÖNCE: üçüncü satırın SyncSeq'i

        var result = await fx.Svc.SyncOnceAsync(CancellationToken.None);

        result.Should().Be(1, "DB imleci ikinci satırda → yalnız üçüncü satır gider; " +
            "settings'teki 999 kazansaydı hiçbir şey gitmezdi (denetim deneyi)");
        postedIds.Should().HaveCount(1);
        postedIds.Select(Guid.Parse).Should().Equal(Guid.Parse(third.Id));
        fx.CursorSeq().Should().Be(expectedCursor);
    }

    /// <summary>R6-04 hedef ekseni: lisans değişince imleç yeni lisans için
    /// sıfırdan başlar (ilk gönderim atlanamaz), eski lisansın imleci yerinde
    /// kalır (geri dönüşte kaldığı yerden devam).</summary>
    [Fact]
    public async Task SyncOnce_lisans_degisince_imlec_yeni_lisans_icin_sifirdan_baslar()
    {
        const string otherKey = "WPF-CUST-OTHER-KEY";
        var otherId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
        var syncedCount = 0;
        var postedIds = new List<Guid>();
        var fx = Build(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path == "/api/v1/me/licenses")
                return FakeHttpMessageHandler.Json(200,
                    $"[{{\"id\":\"{TestLicenseId}\",\"licenseKey\":\"{TestLicenseKey}\"}}," +
                    $"{{\"id\":\"{otherId}\",\"licenseKey\":\"{otherKey}\"}}]");
            if (path.Contains("/wpf-customers/sync"))
            {
                Interlocked.Increment(ref syncedCount);
                var body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                postedIds.AddRange(JsonDocument.Parse(body).RootElement.GetProperty("customers")
                    .EnumerateArray()
                    .Select(e => Guid.Parse(e.GetProperty("id").GetString()!)));
                return FakeHttpMessageHandler.Json(200, SyncRespJson(synced: 2));
            }
            return FakeHttpMessageHandler.Empty(404);
        });
        using var _d = fx.Db;

        var customers = new[] { MakeCustomer(100L), MakeCustomer(200L) };
        foreach (var c in customers) fx.Customers.Insert(c);
        // Eski lisansın imleci her şeyin ötesinde — eski davranışta bu imleç
        // yeni lisansa da uygulanır ve B'nin ilk gönderimi tamamen atlanırdı.
        fx.Cursors.Upsert(WpfCustomerProjectionSyncService.CursorName, TestLicenseKey, seq: 999L);
        var expectedCursor = fx.MaxSyncSeq();     // gönderimden ÖNCE: son satırın SyncSeq'i

        fx.License.CurrentLicenseKey = otherKey;
        var result = await fx.Svc.SyncOnceAsync(CancellationToken.None);

        result.Should().Be(2, "yeni lisans kendi imleciyle (0) tam tarama yapmalı");
        postedIds.Should().BeEquivalentTo(customers.Select(c => Guid.Parse(c.Id)),
            "tam tarama iki satırın ikisini de gönderir");
        fx.CursorSeq(otherKey).Should().Be(expectedCursor);
        fx.CursorSeq(TestLicenseKey).Should().Be(999L, "eski lisansın imleci bozulmamalı");
    }

    // ─── Yönlendirmeler (S6/S7, U4, U13; C4 incelemesi M-7) ──────────────────

    [Fact]
    public async Task SyncOnce_yonlendirme_yerel_kopyayi_yereldeki_asil_kayda_tasir()
    {
        var copy = Guid.NewGuid();
        var canonical = Guid.NewGuid();
        var fx = Build(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path == "/api/v1/me/licenses") return FakeHttpMessageHandler.Json(200, LicensesJson());
            if (path.Contains("/wpf-customers/sync"))
                return FakeHttpMessageHandler.Json(200, RedirectRespJson(copy, canonical, synced: 2));
            return FakeHttpMessageHandler.Empty(404);
        });
        using var _d = fx.Db;
        fx.Customers.Insert(MakeCustomer(1000, id: canonical.ToString("N"), username: "OrnekMusteri"));
        fx.Customers.Insert(MakeCustomer(1001, id: copy.ToString("N"), username: "ornekmusteri"));
        var batchMax = fx.MaxSyncSeq();

        await fx.Svc.SyncOnceAsync(CancellationToken.None);

        Exists(fx, copy.ToString("N")).Should().BeFalse();
        Exists(fx, canonical.ToString("N")).Should().BeTrue();
        fx.CursorSeq().Should().Be(batchMax, "imleç gönderilen partinin sonuna oturur");
        Seq(fx, canonical.ToString("N")).Should().BeGreaterThan(batchMax,
            "taşıma asıl kaydı yeniden gönderime koyar (U2) — taşınan birimler sunucuya gitsin");
    }

    [Fact]
    public async Task SyncOnce_yonlendirmenin_hedefi_yerelde_yoksa_kopya_yerinde_kalir_imlec_ilerler()
    {
        var copy = Guid.NewGuid();
        var fx = Build(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path == "/api/v1/me/licenses") return FakeHttpMessageHandler.Json(200, LicensesJson());
            if (path.Contains("/wpf-customers/sync"))
                return FakeHttpMessageHandler.Json(200, RedirectRespJson(copy, Guid.NewGuid()));
            return FakeHttpMessageHandler.Empty(404);
        });
        using var _d = fx.Db;
        fx.Customers.Insert(MakeCustomer(1000, id: copy.ToString("N"), username: "ornekmusteri"));

        await fx.Svc.SyncOnceAsync(CancellationToken.None);

        fx.Customers.GetById(copy.ToString("N")).Should().NotBeNull("asıl kayıt akışla gelince taşınır (U4)");
        fx.CursorSeq().Should().Be(fx.Customers.GetById(copy.ToString("N"))!.SyncSeq);
    }

    /// <summary>
    /// C4 incelemesi M-7: yönlendirme, partinin OKUNDUĞU andaki satır için gelir; servis taşımaya
    /// gönderilen partinin en büyük SyncSeq'ini verir. Kopya gönderim sürerken değiştiyse yeni hâli
    /// sunucuya gitmedi: taşınmaz (Deferred), imlecin üstünde kalır; sonraki tur yeni hâliyle gönderir,
    /// sunucu yönlendirmeyi yeniden söyler (S7) ve taşıma o zaman olur — değişiklik asıl kayda geçer.
    /// </summary>
    [Fact]
    public async Task SyncOnce_gonderim_surerken_degisen_kopya_tasinmaz_sonraki_turda_tasinir()
    {
        var copy = Guid.NewGuid();
        var canonical = Guid.NewGuid();
        var posted = new List<List<Guid>>();
        CustomerRepository? customers = null;
        var fx = Build(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path == "/api/v1/me/licenses") return FakeHttpMessageHandler.Json(200, LicensesJson());
            if (path.Contains("/wpf-customers/sync"))
            {
                posted.Add(PostedIds(req));
                // İlk gönderim sürerken operatör kopyanın notunu düzenliyor.
                if (posted.Count == 1) customers!.UpdateNotes(copy.ToString("N"), "gönderim sürerken");
                return FakeHttpMessageHandler.Json(200, RedirectRespJson(copy, canonical));
            }
            return FakeHttpMessageHandler.Empty(404);
        });
        customers = fx.Customers;
        using var _d = fx.Db;
        fx.Customers.Insert(MakeCustomer(1000, id: canonical.ToString("N"), username: "OrnekMusteri"));
        fx.Customers.Insert(MakeCustomer(1001, id: copy.ToString("N"), username: "ornekmusteri"));
        var batchMax = fx.MaxSyncSeq();

        await fx.Svc.SyncOnceAsync(CancellationToken.None);

        Exists(fx, copy.ToString("N")).Should().BeTrue("yeni hâli gönderilmeden taşınmaz");
        fx.CursorSeq().Should().Be(batchMax);
        Seq(fx, copy.ToString("N")).Should().BeGreaterThan(batchMax, "düzenleme onu imlecin üstüne taşıdı");

        await fx.Svc.SyncOnceAsync(CancellationToken.None);

        posted.Should().HaveCount(2);
        posted[1].Should().Equal(new[] { copy }, "sonraki tur yalnız değişen kopyayı götürür");
        Exists(fx, copy.ToString("N")).Should().BeFalse("yeni hâli de gitti: yeniden gelen yönlendirme taşır");
        fx.Customers.GetById(canonical.ToString("N"))!.Notes.Should().Be("gönderim sürerken",
            "kopyanın damgalı notu asıl kayda taşındı");
    }

    /// <summary>
    /// U13: ödeme akışı süren müşteri taşınmaz. Depo kaynağı kararla aynı işlemde yeniden gönderime
    /// koyar (Busy); servis yalnız bekleyen sayar. Sonraki tur kopyayı yeniden gönderir, sunucu
    /// yönlendirmeyi yeniden söyler (S7) ve kira bittiyse taşıma olur.
    /// </summary>
    [Fact]
    public async Task SyncOnce_odeme_akisindaki_kopya_tasinmaz_yeniden_gonderilir_kira_bitince_tasinir()
    {
        var copy = Guid.NewGuid();
        var canonical = Guid.NewGuid();
        var posted = new List<List<Guid>>();
        var busy = new CustomerBusySet();
        var fx = Build(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path == "/api/v1/me/licenses") return FakeHttpMessageHandler.Json(200, LicensesJson());
            if (path.Contains("/wpf-customers/sync"))
            {
                posted.Add(PostedIds(req));
                return FakeHttpMessageHandler.Json(200, RedirectRespJson(copy, canonical));
            }
            return FakeHttpMessageHandler.Empty(404);
        }, busy: busy);
        using var _d = fx.Db;
        fx.Customers.Insert(MakeCustomer(1000, id: canonical.ToString("N"), username: "OrnekMusteri"));
        fx.Customers.Insert(MakeCustomer(1001, id: copy.ToString("N"), username: "ornekmusteri"));
        var batchMax = fx.MaxSyncSeq();

        using (await busy.EnterAsync(copy.ToString("N")))
            await fx.Svc.SyncOnceAsync(CancellationToken.None);

        Exists(fx, copy.ToString("N")).Should().BeTrue("ödeme akışındaki müşteri taşınmaz");
        fx.CursorSeq().Should().Be(batchMax);
        Seq(fx, copy.ToString("N")).Should().BeGreaterThan(batchMax, "depo kopyayı yeniden gönderime koydu");

        await fx.Svc.SyncOnceAsync(CancellationToken.None);

        posted.Should().HaveCount(2);
        posted[1].Should().Equal(new[] { copy }, "yalnız yeniden kuyruğa alınan kopya gider");
        Exists(fx, copy.ToString("N")).Should().BeFalse("kira bitti: yeniden gelen yönlendirme taşır");
        Exists(fx, canonical.ToString("N")).Should().BeTrue();
    }

    // ─── İmleç geri sarma ve tek tur kilidi ───────────────────────────────────

    [Fact]
    public async Task RewindAsync_sonraki_tur_tum_musterileri_yeniden_gonderir()
    {
        var posts = 0;
        var fx = Build(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path == "/api/v1/me/licenses") return FakeHttpMessageHandler.Json(200, LicensesJson());
            if (path.Contains("/wpf-customers/sync")) { posts++; return FakeHttpMessageHandler.Json(200, SyncRespJson(synced: 1)); }
            return FakeHttpMessageHandler.Empty(404);
        });
        using var _d = fx.Db;
        fx.Customers.Insert(MakeCustomer(1000));
        await fx.Svc.SyncOnceAsync(CancellationToken.None);
        await fx.Svc.SyncOnceAsync(CancellationToken.None);
        posts.Should().Be(1);

        await fx.Svc.RewindAsync(TestLicenseKey, CancellationToken.None);
        await fx.Svc.SyncOnceAsync(CancellationToken.None);

        posts.Should().Be(2);
        fx.Svc.Watermark(TestLicenseKey).Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task SyncOnce_es_zamanli_cagrilar_ayni_partiyi_iki_kez_gondermez()
    {
        var posts = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fx = Build(async req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path == "/api/v1/me/licenses") return FakeHttpMessageHandler.Json(200, LicensesJson());
            if (path.Contains("/wpf-customers/sync"))
            {
                Interlocked.Increment(ref posts);
                await release.Task;                             // ilk tur partiyi tutuyor
                return FakeHttpMessageHandler.Json(200, SyncRespJson(synced: 1));
            }
            return FakeHttpMessageHandler.Empty(404);
        });
        using var _d = fx.Db;
        fx.Customers.Insert(MakeCustomer(1000));

        var first = fx.Svc.SyncOnceAsync(CancellationToken.None);
        var second = fx.Svc.SyncOnceAsync(CancellationToken.None);   // kilitte bekler
        release.SetResult();
        await Task.WhenAll(first, second);

        posts.Should().Be(1, "ikinci tur ilerlemiş imleci okur");
    }
}
