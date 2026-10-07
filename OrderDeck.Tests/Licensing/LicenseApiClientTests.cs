using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using OrderDeck.Licensing.Api;
using OrderDeck.Licensing.Api.Models;
using OrderDeck.Tests.TestHelpers;

namespace OrderDeck.Tests.Licensing;

public sealed class LicenseApiClientTests
{
    private static readonly Guid TestLicenseId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    // ─── Factory ──────────────────────────────────────────────────────────

    private static LicenseApiClient BuildClient(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var handler = new FakeHttpMessageHandler(responder);
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://test.local") };
        return new LicenseApiClient(http, new LicenseTokenStore());
    }

    // ─── GetShopperCodeAsync ───────────────────────────────────────────────

    [Fact]
    public async Task GetShopperCodeAsync_calls_panel_endpoint_and_parses_response()
    {
        var json = $$"""
            {
              "code": "ornekmagaza",
              "updatedAt": "2026-05-20T10:00:00Z",
              "canChangeAt": "2026-05-27T10:00:00Z",
              "licenseId": "{{TestLicenseId}}"
            }
            """;

        var client = BuildClient(_ => FakeHttpMessageHandler.Json(200, json));

        var result = await client.GetShopperCodeAsync();

        result.Code.Should().Be("ornekmagaza");
        result.UpdatedAt.Should().Be(DateTimeOffset.Parse("2026-05-20T10:00:00Z"));
        result.CanChangeAt.Should().Be(DateTimeOffset.Parse("2026-05-27T10:00:00Z"));
        result.LicenseId.Should().Be(TestLicenseId);
    }

    [Fact]
    public async Task GetShopperCodeAsync_returns_null_code_for_first_time()
    {
        var json = $$"""{"code":null,"updatedAt":null,"canChangeAt":null,"licenseId":"{{TestLicenseId}}"}""";
        var client = BuildClient(_ => FakeHttpMessageHandler.Json(200, json));

        var result = await client.GetShopperCodeAsync();

        result.Code.Should().BeNull();
        result.UpdatedAt.Should().BeNull();
        result.CanChangeAt.Should().BeNull();
        result.LicenseId.Should().Be(TestLicenseId);
    }

    [Fact]
    public async Task GetShopperCodeAsync_throws_on_404()
    {
        var client = BuildClient(_ => FakeHttpMessageHandler.Empty(404));

        var act = () => client.GetShopperCodeAsync();

        await act.Should().ThrowAsync<Exception>("404 should propagate as an error");
    }

    // ─── SetShopperCodeAsync ───────────────────────────────────────────────

    [Fact]
    public async Task SetShopperCodeAsync_sends_correct_body_and_parses_response()
    {
        var responseJson = $$"""
            {
              "code": "ornekmagaza",
              "updatedAt": "2026-05-20T10:00:00Z",
              "canChangeAt": "2026-05-27T10:00:00Z",
              "licenseId": "{{TestLicenseId}}"
            }
            """;

        string? capturedBody = null;
        HttpMethod? capturedMethod = null;
        string? capturedPath = null;

        var client = BuildClient(req =>
        {
            capturedBody = req.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
            capturedMethod = req.Method;
            capturedPath = req.RequestUri?.PathAndQuery;
            return FakeHttpMessageHandler.Json(200, responseJson);
        });

        var result = await client.SetShopperCodeAsync("ornekmagaza");

        capturedMethod.Should().Be(HttpMethod.Put);
        capturedPath.Should().Be("/api/panel/shopper-code");
        capturedBody.Should().Contain("\"code\"");
        capturedBody.Should().Contain("ornekmagaza");

        result.Code.Should().Be("ornekmagaza");
        result.LicenseId.Should().Be(TestLicenseId);
    }

    [Fact]
    public async Task SetShopperCodeAsync_throws_validation_exception_on_400()
    {
        var client = BuildClient(_ =>
            FakeHttpMessageHandler.Problem(400, "format"));

        var act = () => client.SetShopperCodeAsync("bad code!!");

        var ex = await act.Should().ThrowAsync<ShopperCodeValidationException>();
        ex.Which.ErrorCode.Should().Be("format");
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("length")]
    [InlineData("format")]
    [InlineData("reserved")]
    [InlineData("profanity")]
    [InlineData("cooldown")]
    [InlineData("taken")]
    public async Task SetShopperCodeAsync_throws_validation_exception_for_each_errorCode(string errorCode)
    {
        var client = BuildClient(_ =>
            FakeHttpMessageHandler.Problem(400, errorCode));

        var act = () => client.SetShopperCodeAsync("any");

        var ex = await act.Should().ThrowAsync<ShopperCodeValidationException>();
        ex.Which.ErrorCode.Should().Be(errorCode);
    }

    // ─── SyncPaymentAccountAsync ───────────────────────────────────────────

    [Fact]
    public async Task SyncPaymentAccountAsync_sends_post_with_iban_and_account_holder()
    {
        string? capturedBody = null;
        string? capturedPath = null;

        var client = BuildClient(req =>
        {
            capturedBody = req.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
            capturedPath = req.RequestUri?.PathAndQuery;
            return FakeHttpMessageHandler.Empty(204);
        });

        await client.SyncPaymentAccountAsync(TestLicenseId, "TR330006100519786457841326", "Ornek Musteri");

        capturedPath.Should().Be($"/api/v1/licenses/{TestLicenseId}/payment-account");
        capturedBody.Should().Contain("TR330006100519786457841326");
        capturedBody.Should().Contain("Ornek Musteri");
    }

    // ─── SyncWpfCustomersAsync ─────────────────────────────────────────────

    [Fact]
    public async Task SyncWpfCustomersAsync_sends_batch_and_parses_response()
    {
        var responseJson = """{"synced":3,"retroactiveMatches":1}""";
        string? capturedBody = null;
        string? capturedPath = null;

        var client = BuildClient(req =>
        {
            capturedBody = req.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
            capturedPath = req.RequestUri?.PathAndQuery;
            return FakeHttpMessageHandler.Json(200, responseJson);
        });

        var customers = new List<WpfCustomerSyncItem>
        {
            new(Guid.NewGuid(), "youtube", "user1", "User One",   TestPhone.NewE164(), null,       DateTimeOffset.UtcNow),
            new(Guid.NewGuid(), "youtube", "user2", null,          null,                "Istanbul", DateTimeOffset.UtcNow),
            new(Guid.NewGuid(), "twitch",  "user3", "User Three",  TestPhone.NewE164(), "Ankara",   DateTimeOffset.UtcNow),
        };

        var result = await client.SyncWpfCustomersAsync(TestLicenseId, customers);

        capturedPath.Should().Be($"/api/v1/licenses/{TestLicenseId}/wpf-customers/sync");
        capturedBody.Should().Contain("\"customers\"");
        // All 3 usernames should appear in the serialized body
        capturedBody.Should().Contain("user1");
        capturedBody.Should().Contain("user2");
        capturedBody.Should().Contain("user3");

        result.Synced.Should().Be(3);
        result.RetroactiveMatches.Should().Be(1);
    }

    // ─── Çoklu bilgisayar müşteri senkronu (Bölüm C) ──────────────────────

    /// <summary>Sunucudaki LicensesWpfCustomersSyncController.SyncItem'ın alanları
    /// (PR-1 9687943f, :72-105). Biri eklenir/çıkarılırsa iki taraf birlikte değişmeli.</summary>
    private static readonly string[] ServerSyncItemFields =
    {
        "id", "platform", "username", "fullName", "phone", "address", "updatedAt", "format",
        "fullNameChangedAt", "displayName", "displayNameChangedAt", "groupId", "groupIdChangedAt",
        "city", "district", "addressChangedAt", "recipientPaysActive", "recipientPaysChangedAt",
        "phoneChangedAt", "email", "emailChangedAt", "tckn", "tcknChangedAt",
        "whatsAppConsent", "whatsAppConsentChangedAt", "smsConsent", "smsConsentChangedAt",
        "isBlacklisted", "blacklistReason", "blacklistedAt", "blacklistChangedAt",
        "notes", "notesChangedAt",
    };

    [Fact]
    public async Task SyncWpfCustomersAsync_alan_adlari_sunucu_SyncItem_ile_birebir_ve_bicim_2()
    {
        string? body = null;
        var client = BuildClient(req =>
        {
            body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return FakeHttpMessageHandler.Json(200, """{"synced":1,"retroactiveMatches":0,"redirects":[]}""");
        });

        await client.SyncWpfCustomersAsync(TestLicenseId, new[]
        {
            new WpfCustomerSyncItem(Guid.NewGuid(), "tiktok", "ornek.musteri", null, null, null, DateTimeOffset.UtcNow),
        });

        using var doc = JsonDocument.Parse(body!);
        var item = doc.RootElement.GetProperty("customers")[0];
        item.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(ServerSyncItemFields);
        item.GetProperty("format").GetInt32().Should().Be(2, "istemci yalnız biçim 2 gönderir");
    }

    [Fact]
    public async Task SyncWpfCustomersAsync_yonlendirmeleri_cozer_eski_sunucuda_null()
    {
        var copy = Guid.NewGuid();
        var canonical = Guid.NewGuid();
        var withRedirects = BuildClient(_ => FakeHttpMessageHandler.Json(200,
            $$"""{"synced":1,"retroactiveMatches":0,"redirects":[{"id":"{{copy}}","canonicalId":"{{canonical}}"}]}"""));
        var legacy = BuildClient(_ => FakeHttpMessageHandler.Json(200, """{"synced":1,"retroactiveMatches":0}"""));
        var one = new[] { new WpfCustomerSyncItem(Guid.NewGuid(), "tiktok", "a", null, null, null, DateTimeOffset.UtcNow) };

        (await withRedirects.SyncWpfCustomersAsync(TestLicenseId, one)).Redirects
            .Should().ContainSingle().Which.Should().Be(new WpfCustomerRedirect(copy, canonical));
        (await legacy.SyncWpfCustomersAsync(TestLicenseId, one)).Redirects.Should().BeNull();
    }

    [Fact]
    public async Task GetWpfCustomerChangesAsync_imleci_gonderir_sayfayi_cozer()
    {
        string? query = null;
        var id = Guid.NewGuid();
        var alias = Guid.NewGuid();
        var client = BuildClient(req =>
        {
            query = req.RequestUri!.PathAndQuery;
            return FakeHttpMessageHandler.Json(200, $$"""
                {"items":[
                  {"id":"{{id}}","platform":"tiktok","username":"ornek.musteri","mergedIntoId":null,"purgedAt":null,
                   "fullName":"Örnek Müşteri","fullNameChangedAt":"2026-10-05T10:00:00+00:00",
                   "city":"Örnekşehir","addressChangedAt":"2026-10-05T11:00:00.123+00:00",
                   "recipientPaysActive":false,"whatsAppConsent":true,"smsConsent":false,"isBlacklisted":false,
                   "tckn":null,"changeSeq":41,"createdByShopper":true},
                  {"id":"{{alias}}","platform":"tiktok","username":"Ornek.Musteri","mergedIntoId":"{{id}}","purgedAt":null,
                   "recipientPaysActive":false,"whatsAppConsent":false,"smsConsent":false,"isBlacklisted":false,
                   "changeSeq":42,"createdByShopper":false}],
                 "nextAfterSeq":42,"cursorReset":true}
                """);
        });

        var page = await client.GetWpfCustomerChangesAsync(TestLicenseId, afterSeq: 7, take: 500);

        query.Should().Be($"/api/v1/licenses/{TestLicenseId}/wpf-customers/changes?afterSeq=7&take=500");
        page.NextAfterSeq.Should().Be(42);
        page.CursorReset.Should().BeTrue();
        page.Items.Should().HaveCount(2);
        page.Items[0].CreatedByShopper.Should().BeTrue();
        page.Items[0].AddressChangedAt!.Value.ToUnixTimeMilliseconds()
            .Should().Be(DateTimeOffset.Parse("2026-10-05T11:00:00.123+00:00").ToUnixTimeMilliseconds(), "ms kaybolmamalı");
        page.Items[1].MergedIntoId.Should().Be(id);
    }

    [Fact]
    public async Task GetWpfCustomerChangesAsync_bozuk_govde_bos_sayfa_sayilmaz_firlatir()
    {
        var client = BuildClient(_ => FakeHttpMessageHandler.Json(200, "null"));
        var act = () => client.GetWpfCustomerChangesAsync(TestLicenseId, 0);
        await act.Should().ThrowAsync<LicenseApiUnknownException>();
    }

    [Fact]
    public async Task GetWpfCustomerChangesAsync_sinir_disi_take_firlatir()
    {
        var client = BuildClient(_ => FakeHttpMessageHandler.Json(200, """{"items":[],"nextAfterSeq":0}"""));
        var act = () => client.GetWpfCustomerChangesAsync(TestLicenseId, 0, take: 501);
        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task GetWpfCustomerChangesAsync_listedeki_null_ogeyi_bozuk_sayar_firlatir()
    {
        var client = BuildClient(_ => FakeHttpMessageHandler.Json(200, """{"items":[null],"nextAfterSeq":5}"""));
        var act = () => client.GetWpfCustomerChangesAsync(TestLicenseId, 0);
        await act.Should().ThrowAsync<LicenseApiUnknownException>();
    }

    [Fact]
    public async Task GetWpfCustomerChangesAsync_imlec_son_ogeden_eskiyse_kullanilamaz_sayar_firlatir()
    {
        // Sunucu NextAfterSeq'i normalde son öğenin ChangeSeq'i yapar
        // (LicensesWpfCustomersPullController.Changes). Ondan küçük bir imleç bu
        // sayfayı sonsuza dek yeniden istetirdi — bozuk gövde sayılıp fırlatılmalı.
        var id = Guid.NewGuid();
        var client = BuildClient(_ => FakeHttpMessageHandler.Json(200, $$"""
            {"items":[{"id":"{{id}}","platform":"tiktok","username":"ornek.musteri","mergedIntoId":null,"purgedAt":null,
             "recipientPaysActive":false,"whatsAppConsent":false,"smsConsent":false,"isBlacklisted":false,
             "changeSeq":50,"createdByShopper":false}],"nextAfterSeq":49}
            """));
        var act = () => client.GetWpfCustomerChangesAsync(TestLicenseId, 0);
        await act.Should().ThrowAsync<LicenseApiUnknownException>();
    }

    /// <summary>Sunucudaki LicensesWpfCustomersPullController.WpfCustomerChangeItem'ın
    /// alanları (PR-1/PR-2, `origin/master`). Biri eklenir/çıkarılırsa/yeniden adlandırılırsa
    /// iki taraf birlikte değişmeli.</summary>
    private static readonly string[] ServerChangeItemFields =
    {
        "id", "platform", "username", "mergedIntoId", "purgedAt",
        "fullName", "fullNameChangedAt",
        "displayName", "displayNameChangedAt",
        "groupId", "groupIdChangedAt",
        "address", "city", "district", "addressChangedAt",
        "recipientPaysActive", "recipientPaysChangedAt",
        "phone", "phoneChangedAt",
        "email", "emailChangedAt",
        "tckn", "tcknChangedAt",
        "whatsAppConsent", "whatsAppConsentChangedAt",
        "smsConsent", "smsConsentChangedAt",
        "isBlacklisted", "blacklistReason", "blacklistedAt", "blacklistChangedAt",
        "notes", "notesChangedAt",
        "changeSeq", "createdByShopper",
    };

    [Fact]
    public async Task GetWpfCustomerChangesAsync_degisiklik_satirinin_her_alani_sunucu_WpfCustomerChangeItem_ile_birebir()
    {
        // Her alan BİLEREK dolu ve varsayılan-olmayan: adı değişmiş, kaldırılmış ya
        // da tipi değişmiş bir alan burada sessizce null/false/0'a düşer ve testi
        // düşürür — isim kümesi karşılaştırması tek başına bunu yakalamaz.
        var id = Guid.NewGuid();
        var mergedIntoId = Guid.NewGuid();
        var groupId = Guid.NewGuid().ToString("N");
        var phone = TestPhone.NewE164();
        var tckn = TestTckn.NewValid();
        var json = $$"""
            {"items":[
              {"id":"{{id}}","platform":"tiktok","username":"ornek.musteri","mergedIntoId":"{{mergedIntoId}}","purgedAt":"2026-10-05T09:00:00.123+00:00",
               "fullName":"Örnek Müşteri","fullNameChangedAt":"2026-10-05T10:00:00.123+00:00",
               "displayName":"ornekmusteri","displayNameChangedAt":"2026-10-05T10:05:00.123+00:00",
               "groupId":"{{groupId}}","groupIdChangedAt":"2026-10-05T10:10:00.123+00:00",
               "address":"Örnek Mahallesi 1. Sokak No:1","city":"Örnekşehir","district":"Örnek","addressChangedAt":"2026-10-05T11:00:00.123+00:00",
               "recipientPaysActive":true,"recipientPaysChangedAt":"2026-10-05T11:05:00.123+00:00",
               "phone":"{{phone}}","phoneChangedAt":"2026-10-05T11:10:00.123+00:00",
               "email":"ornek.musteri@ornek.test","emailChangedAt":"2026-10-05T11:15:00.123+00:00",
               "tckn":"{{tckn}}","tcknChangedAt":"2026-10-05T11:20:00.123+00:00",
               "whatsAppConsent":true,"whatsAppConsentChangedAt":"2026-10-05T11:25:00.123+00:00",
               "smsConsent":true,"smsConsentChangedAt":"2026-10-05T11:30:00.123+00:00",
               "isBlacklisted":true,"blacklistReason":"örnek neden","blacklistedAt":"2026-10-05T11:35:00.123+00:00","blacklistChangedAt":"2026-10-05T11:40:00.123+00:00",
               "notes":"örnek not","notesChangedAt":"2026-10-05T11:45:00.123+00:00",
               "changeSeq":99,"createdByShopper":true}],
             "nextAfterSeq":99,"cursorReset":false}
            """;
        var client = BuildClient(_ => FakeHttpMessageHandler.Json(200, json));

        var page = await client.GetWpfCustomerChangesAsync(TestLicenseId, afterSeq: 0);
        var item = page.Items.Should().ContainSingle().Subject;

        var reserialized = JsonSerializer.SerializeToElement(item, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        reserialized.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(ServerChangeItemFields);
        foreach (var prop in reserialized.EnumerateObject())
        {
            var isNullFalseOrZero = prop.Value.ValueKind switch
            {
                JsonValueKind.Null => true,
                JsonValueKind.False => true,
                JsonValueKind.Number => prop.Value.GetDouble() == 0,
                _ => false,
            };
            isNullFalseOrZero.Should().BeFalse($"alan '{prop.Name}' null/false/0 olmamalı — adı/tipi değişmiş olabilir");
        }
    }

    [Fact]
    public async Task SyncWpfCustomersAsync_govde_tum_alanlar_dolu_ogede_tipleri_dogru_yazar()
    {
        string? body = null;
        var client = BuildClient(req =>
        {
            body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return FakeHttpMessageHandler.Json(200, """{"synced":1,"retroactiveMatches":0,"redirects":[]}""");
        });

        var now = DateTimeOffset.UtcNow;
        var item = new WpfCustomerSyncItem(
            Id: Guid.NewGuid(), Platform: "tiktok", Username: "ornek.musteri",
            FullName: "Örnek Müşteri", Phone: TestPhone.NewE164(), Address: "Örnek Mahallesi 1. Sokak No:1",
            UpdatedAt: now, Format: 2, FullNameChangedAt: now,
            DisplayName: "ornekmusteri", DisplayNameChangedAt: now,
            GroupId: Guid.NewGuid().ToString("N"), GroupIdChangedAt: now,
            City: "Örnekşehir", District: "Örnek", AddressChangedAt: now,
            RecipientPaysActive: true, RecipientPaysChangedAt: now,
            PhoneChangedAt: now, Email: "ornek.musteri@ornek.test", EmailChangedAt: now,
            Tckn: TestTckn.NewValid(), TcknChangedAt: now,
            WhatsAppConsent: true, WhatsAppConsentChangedAt: now,
            SmsConsent: true, SmsConsentChangedAt: now,
            IsBlacklisted: true, BlacklistReason: "örnek neden", BlacklistedAt: now, BlacklistChangedAt: now,
            Notes: "örnek not", NotesChangedAt: now);

        await client.SyncWpfCustomersAsync(TestLicenseId, new[] { item });

        using var doc = JsonDocument.Parse(body!);
        var sent = doc.RootElement.GetProperty("customers")[0];
        // Mevcut biçim-2 testi isim kümesini zaten doğruluyor; burada EK olarak
        // tel türleri de doğrulanır — Guid/DateTimeOffset dize, format sayı, bayrak true.
        sent.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(ServerSyncItemFields);

        foreach (var name in new[]
        {
            "id", "updatedAt", "fullNameChangedAt", "displayNameChangedAt",
            "groupIdChangedAt", "addressChangedAt", "recipientPaysChangedAt", "phoneChangedAt",
            "emailChangedAt", "tcknChangedAt", "whatsAppConsentChangedAt", "smsConsentChangedAt",
            "blacklistedAt", "blacklistChangedAt", "notesChangedAt",
        })
            sent.GetProperty(name).ValueKind.Should().Be(JsonValueKind.String, $"{name} dize (Guid/DateTimeOffset) olmalı");

        sent.GetProperty("format").ValueKind.Should().Be(JsonValueKind.Number);
        sent.GetProperty("format").GetInt32().Should().Be(2);

        foreach (var name in new[] { "recipientPaysActive", "whatsAppConsent", "smsConsent", "isBlacklisted" })
            sent.GetProperty(name).ValueKind.Should().Be(JsonValueKind.True, $"{name} true olmalı");
    }

    [Fact]
    public async Task SyncWpfCustomersAsync_bozuk_govde_null_firlatir()
    {
        // Redirects artık yerel yeniden anahtarlamayı tetikliyor: null gövdeyi
        // sessizce yutmak kopya satırı yerelde sonsuza dek bırakırdı.
        var client = BuildClient(_ => FakeHttpMessageHandler.Json(200, "null"));
        var one = new[] { new WpfCustomerSyncItem(Guid.NewGuid(), "tiktok", "ornek.musteri", null, null, null, DateTimeOffset.UtcNow) };
        var act = () => client.SyncWpfCustomersAsync(TestLicenseId, one);
        await act.Should().ThrowAsync<LicenseApiUnknownException>();
    }

    // ─── Katalog çekme (Stok Faz 1b) ───────────────────────────────────────

    [Fact]
    public async Task GetCatalogProductsAsync_passes_the_keyset_cursor()
    {
        HttpRequestMessage? seen = null;
        var client = BuildClient(req =>
        {
            seen = req;
            return FakeHttpMessageHandler.Json(200, """
                [{ "id":"11111111-1111-1111-1111-111111111111",
                   "code":"A1", "name":"Elbise", "nameSearch":"ELBISE",
                   "defaultPrice":199.90, "updatedAt":"2026-08-13T10:00:00Z",
                   "coverPhotoKey":"lic/products/p/k.img",
                   "coverPhotoUrl":"https://r2.local/k.img?sig=1",
                   "variants":[] }]
                """);
        });

        var licenseId = Guid.NewGuid();
        var after = Guid.Parse("22222222-2222-2222-2222-222222222222");

        var rows = await client.GetCatalogProductsAsync(licenseId, after, take: 200);

        seen!.RequestUri!.PathAndQuery.Should().Be(
            $"/api/v1/licenses/{licenseId}/catalog/products?after={after}&take=200");
        rows.Should().ContainSingle();
        rows[0].CoverPhotoKey.Should().Be("lic/products/p/k.img");
    }

    [Fact]
    public async Task GetCatalogProductsAsync_omits_the_cursor_on_the_first_page()
    {
        HttpRequestMessage? seen = null;
        var client = BuildClient(req => { seen = req; return FakeHttpMessageHandler.Json(200, "[]"); });

        var licenseId = Guid.NewGuid();
        // take, varsayılandan (200) FARKLI seçiliyor: aksi hâlde sorgu dizesine
        // 200 sabitlense de test yeşil kalır, parametre gerçekten sınanmaz.
        await client.GetCatalogProductsAsync(licenseId, after: null, take: 50);

        seen!.RequestUri!.PathAndQuery.Should().Be(
            $"/api/v1/licenses/{licenseId}/catalog/products?take=50");
    }

    [Fact]
    public async Task GetCatalogProductsAsync_sinir_icindeki_take_i_tele_aynen_yazar()
    {
        HttpRequestMessage? seen = null;
        var client = BuildClient(req => { seen = req; return FakeHttpMessageHandler.Json(200, "[]"); });

        var licenseId = Guid.NewGuid();
        await client.GetCatalogProductsAsync(licenseId, after: null, take: 500);

        // Üst sınırın kendisi GEÇERLİ: istemci onu kırpmadan, değiştirmeden geçirmeli.
        seen!.RequestUri!.PathAndQuery.Should().Be(
            $"/api/v1/licenses/{licenseId}/catalog/products?take=500");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(501)]
    [InlineData(1000)]
    public async Task GetCatalogProductsAsync_sinir_disi_take_degerini_reddeder(int take)
    {
        var client = BuildClient(_ => FakeHttpMessageHandler.Json(200, "[]"));

        // Sessizce kırpsaydık çağıran kendi take'iyle "eksik sayfa = son sayfa"
        // sanıp kataloğun kalanını sildirirdi. Yüksek sesle reddet.
        var act = () => client.GetCatalogProductsAsync(Guid.NewGuid(), after: null, take: take);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task GetCatalogProductsAsync_null_govdeyi_bos_katalog_saymaz()
    {
        var client = BuildClient(_ => FakeHttpMessageHandler.Json(200, "null"));

        // Bozuk gövde "katalog boş" demek DEĞİL. Sessizce boş liste dönseydi
        // senkron döngüsü bunu son sayfa sanıp replikayı komple silerdi.
        var act = () => client.GetCatalogProductsAsync(Guid.NewGuid(), after: null);

        await act.Should().ThrowAsync<LicenseApiUnknownException>();
    }

    [Fact]
    public async Task GetCatalogProductsAsync_bozuk_govdeyi_LicenseApi_hatasina_cevirir()
    {
        var client = BuildClient(_ => FakeHttpMessageHandler.Json(200, "{\"oops\":1}"));

        // Çağıran (senkron döngüsü) bu dosyadan LicenseApiException bekliyor.
        // Ham JsonException sızsaydı hosted service sessizce ölür, senkron durur.
        var act = () => client.GetCatalogProductsAsync(Guid.NewGuid(), after: null);

        await act.Should().ThrowAsync<LicenseApiUnknownException>();
    }

    [Fact]
    public async Task GetCatalogCategoriesAsync_parses_the_tree()
    {
        HttpRequestMessage? seen = null;
        var client = BuildClient(req => { seen = req; return FakeHttpMessageHandler.Json(200, """
            [{ "id":"33333333-3333-3333-3333-333333333333",
               "parentCategoryId":null, "name":"Erkek",
               "path":"/33/", "sortOrder":0, "isActive":true }]
            """); });

        var licenseId = Guid.NewGuid();
        var rows = await client.GetCatalogCategoriesAsync(licenseId);

        // İstek kaydedilmezse test HERHANGİ bir URL'e karşı yeşil kalırdı.
        seen!.RequestUri!.PathAndQuery.Should().Be(
            $"/api/v1/licenses/{licenseId}/catalog/categories");
        rows.Should().ContainSingle();
        rows[0].Name.Should().Be("Erkek");
        rows[0].ParentCategoryId.Should().BeNull();
    }

    [Fact]
    public async Task GetCatalogCategoriesAsync_null_govdeyi_bos_agac_saymaz()
    {
        var client = BuildClient(_ => FakeHttpMessageHandler.Json(200, "null"));

        // Ürün ucuyla aynı sınıf hata: bozuk gövde "kategori yok" demek değil.
        var act = () => client.GetCatalogCategoriesAsync(Guid.NewGuid());

        await act.Should().ThrowAsync<LicenseApiUnknownException>();
    }

    [Fact]
    public async Task GetCatalogProductsAsync_urun_alanlarini_tel_uzerinden_baglar()
    {
        var client = BuildClient(_ => FakeHttpMessageHandler.Json(200, """
            [{ "id":"11111111-1111-1111-1111-111111111111",
               "categoryId":"55555555-5555-5555-5555-555555555555",
               "code":"A1", "name":"Elbise", "nameSearch":"ELBISE",
               "defaultPrice":199.90, "shelfLocation":"R3-K2",
               "axis1Name":"Beden", "axis1Role":1,
               "axis2Name":"Renk",  "axis2Role":2,
               "updatedAt":"2026-08-13T10:00:00Z",
               "coverPhotoKey":"lic/products/p/k.img",
               "coverPhotoUrl":"https://r2.local/k.img?sig=1",
               "variants":[{ "id":"44444444-4444-4444-4444-444444444444",
                             "axis1Value":"M",
                             "axis2Value":"Kirmizi",
                             "barcode":"8690000000001",
                             "isActive":true }],
               "broadcastCodes":[
                    { "sellerAxisValue":"Kirmizi", "code":"ATES",
                      "codeNormalized":"ATES", "createdAt":"2026-08-13T10:00:00Z" },
                    { "sellerAxisValue":null, "code":"BUZ",
                      "codeNormalized":"BUZ", "createdAt":"2026-08-12T10:00:00Z" }] }]
            """));

        var p = (await client.GetCatalogProductsAsync(Guid.NewGuid(), after: null))[0];

        // Kod, yayında yorumla eşleşen TEK alan — bağlanmazsa yanlış ürün satılır.
        p.Code.Should().Be("A1");
        p.Id.Should().Be(Guid.Parse("11111111-1111-1111-1111-111111111111"));
        p.CategoryId.Should().Be(Guid.Parse("55555555-5555-5555-5555-555555555555"));
        p.Name.Should().Be("Elbise");
        p.NameSearch.Should().Be("ELBISE");
        // Para: 199.90 tam gelmeli, araya double girmemeli (bkz. CatalogReplicaRepository).
        p.DefaultPrice.Should().Be(199.90m);
        // decimal'e özgü: double olsaydı bu çarpım 599.6999999999999 düşerdi.
        (p.DefaultPrice * 3).Should().Be(599.70m);
        p.ShelfLocation.Should().Be("R3-K2");
        p.Axis1Name.Should().Be("Beden");
        p.Axis1Role.Should().Be(1);
        p.Axis2Name.Should().Be("Renk");
        p.Axis2Role.Should().Be(2);
        // Yerel replika unix SANİYE saklıyor; damganın UTC çözüldüğünü sabitle.
        p.UpdatedAt.Should().Be(DateTimeOffset.Parse("2026-08-13T10:00:00Z"));
        p.UpdatedAt.ToUnixTimeSeconds().Should().Be(1786615200);
        p.CoverPhotoKey.Should().Be("lic/products/p/k.img");
        // İmzalı adres de bağlanmalı: null gelmesi MEŞRU sayıldığı için (bkz.
        // CatalogPullDtos) bozuk bir bağlama sessizce "fotoğraf yok"a düşer.
        p.CoverPhotoUrl.Should().Be("https://r2.local/k.img?sig=1");

        // Varyant sırası = dizi sırası; 025'teki SortOrder sözleşmesi buna dayanıyor.
        p.Variants.Should().ContainSingle();
        p.Variants[0].Axis1Value.Should().Be("M");
        p.Variants[0].Axis2Value.Should().Be("Kirmizi");
        p.Variants[0].Barcode.Should().Be("8690000000001");
        p.Variants[0].IsActive.Should().BeTrue();

        // Yayın kodları da gömülü dizi; sırası (en yeni önce) SortOrder'a
        // dönüşüyor, o yüzden bağlamanın diziyi olduğu gibi taşıması şart.
        p.BroadcastCodes.Should().HaveCount(2);
        p.BroadcastCodes[0].Code.Should().Be("ATES");
        p.BroadcastCodes[0].CodeNormalized.Should().Be("ATES");
        p.BroadcastCodes[0].SellerAxisValue.Should().Be("Kirmizi");
        p.BroadcastCodes[0].CreatedAt.ToUnixTimeSeconds().Should().Be(1786615200);
        // Satıcı ekseni olmayan üründe null MEŞRU: kod ürünün tamamını gösterir.
        p.BroadcastCodes[1].SellerAxisValue.Should().BeNull();
        p.BroadcastCodes[1].Code.Should().Be("BUZ");
    }
}
