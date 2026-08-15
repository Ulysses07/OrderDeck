using System.Net;
using System.Text;
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
              "code": "royal",
              "updatedAt": "2026-05-20T10:00:00Z",
              "canChangeAt": "2026-05-27T10:00:00Z",
              "licenseId": "{{TestLicenseId}}"
            }
            """;

        var client = BuildClient(_ => FakeHttpMessageHandler.Json(200, json));

        var result = await client.GetShopperCodeAsync();

        result.Code.Should().Be("royal");
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
              "code": "royal",
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

        var result = await client.SetShopperCodeAsync("royal");

        capturedMethod.Should().Be(HttpMethod.Put);
        capturedPath.Should().Be("/api/panel/shopper-code");
        capturedBody.Should().Contain("\"code\"");
        capturedBody.Should().Contain("royal");

        result.Code.Should().Be("royal");
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

        await client.SyncPaymentAccountAsync(TestLicenseId, "TR330006100519786457841326", "Ahmet Yilmaz");

        capturedPath.Should().Be($"/api/v1/licenses/{TestLicenseId}/payment-account");
        capturedBody.Should().Contain("TR330006100519786457841326");
        capturedBody.Should().Contain("Ahmet Yilmaz");
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
            new(Guid.NewGuid(), "youtube", "user1", "User One",   "+905001111111", null,           DateTimeOffset.UtcNow),
            new(Guid.NewGuid(), "youtube", "user2", null,          null,           "Istanbul",     DateTimeOffset.UtcNow),
            new(Guid.NewGuid(), "twitch",  "user3", "User Three",  "+905002222222", "Ankara",      DateTimeOffset.UtcNow),
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
