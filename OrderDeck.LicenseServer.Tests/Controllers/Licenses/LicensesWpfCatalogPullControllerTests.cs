using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Licenses;

public class LicensesWpfCatalogPullControllerTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public LicensesWpfCatalogPullControllerTests(ApiFactory factory) => _factory = factory;

    private async Task<(HttpClient Client, Guid LicenseId)> SeedAsync(
        int productCount, bool archiveFirst = false, bool withPhotos = false,
        bool photoOnEvenOnly = false, ApiFactory? overrideFactory = null)
    {
        var f = overrideFactory ?? _factory;
        var (client, customerId, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(f);
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();

        var license = new License
        {
            Id = Guid.NewGuid(), CustomerId = customerId,
            LicenseKey = "LDK-CATP-" + Guid.NewGuid().ToString("N"),
            SkuCode = "STD", ActivationSlots = 1,
            IssuedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddDays(30)
        };
        db.Licenses.Add(license);

        for (var i = 0; i < productCount; i++)
        {
            var product = new Product
            {
                Id = Guid.NewGuid(), LicenseId = license.Id,
                Code = "A" + (i + 1), Name = "Ürün " + (i + 1),
                DefaultPrice = 100m + i,
                IsArchived = archiveFirst && i == 0,
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
            };
            db.Products.Add(product);
            db.ProductVariants.Add(new ProductVariant
            {
                Id = Guid.NewGuid(), LicenseId = license.Id, ProductId = product.Id,
                Axis1Value = "M",
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
            });
            // withPhotos: tüm ürünlere fotoğraf; photoOnEvenOnly: yalnız çift indeksli
            // ürünlere fotoğraf koyar (tek indeksliler fotoğrafsız kalır). Bu sayede
            // "sayfanın ortasında fotoğrafsız ürün" dalı (continue) kapsanabilir.
            var addPhoto = withPhotos || (photoOnEvenOnly && i % 2 == 0);
            if (addPhoto)
            {
                // Kapak = en KÜÇÜK SortOrder. İki fotoğrafı bilerek ters sırada
                // ekliyoruz ki "ilk eklenen" ile "kapak" karışırsa test yakalasın.
                db.ProductPhotos.Add(new ProductPhoto
                {
                    Id = Guid.NewGuid(), LicenseId = license.Id, ProductId = product.Id,
                    ObjectKey = $"{license.Id:N}/products/{product.Id:N}/ikinci.img",
                    ContentType = "image/jpeg", SizeBytes = 2048, SortOrder = 1,
                    CreatedAt = DateTimeOffset.UtcNow
                });
                db.ProductPhotos.Add(new ProductPhoto
                {
                    Id = Guid.NewGuid(), LicenseId = license.Id, ProductId = product.Id,
                    ObjectKey = $"{license.Id:N}/products/{product.Id:N}/kapak.img",
                    ContentType = "image/jpeg", SizeBytes = 1024, SortOrder = 0,
                    CreatedAt = DateTimeOffset.UtcNow
                });
            }
        }

        await db.SaveChangesAsync();
        return (client, license.Id);
    }

    [Fact]
    public async Task Returns_products_with_their_variants()
    {
        var (client, licenseId) = await SeedAsync(1);

        var rows = await client.GetFromJsonAsync<List<JsonElement>>(
            $"/api/v1/licenses/{licenseId}/catalog/products");

        rows!.Should().ContainSingle();
        rows[0].GetProperty("code").GetString().Should().Be("A1");
        rows[0].GetProperty("nameSearch").GetString().Should().Be("ÜRÜN 1".Replace("Ü", "U"));
        rows[0].GetProperty("variants").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task Archived_products_are_excluded()
    {
        var (client, licenseId) = await SeedAsync(2, archiveFirst: true);

        var rows = await client.GetFromJsonAsync<List<JsonElement>>(
            $"/api/v1/licenses/{licenseId}/catalog/products");

        rows!.Should().ContainSingle();
        rows[0].GetProperty("code").GetString().Should().Be("A2");
    }

    [Fact]
    public async Task Pages_through_the_whole_catalog_with_the_after_cursor()
    {
        var (client, licenseId) = await SeedAsync(5);

        var seen = new List<Guid>();
        Guid? after = null;
        while (true)
        {
            var url = $"/api/v1/licenses/{licenseId}/catalog/products?take=2"
                      + (after is null ? "" : $"&after={after}");
            var page = await client.GetFromJsonAsync<List<JsonElement>>(url);
            if (page!.Count == 0) break;
            seen.AddRange(page.Select(p => p.GetProperty("id").GetGuid()));
            after = seen[^1];
        }

        seen.Should().HaveCount(5);
        seen.Should().OnlyHaveUniqueItems();
        seen.Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task Another_customers_license_is_not_readable()
    {
        var (client, _) = await SeedAsync(1);
        var (_, otherLicenseId) = await SeedAsync(1);

        var resp = await client.GetAsync($"/api/v1/licenses/{otherLicenseId}/catalog/products");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Cover_photo_is_the_smallest_sort_order()
    {
        var (client, licenseId) = await SeedAsync(1, withPhotos: true);

        var rows = await client.GetFromJsonAsync<List<JsonElement>>(
            $"/api/v1/licenses/{licenseId}/catalog/products");

        var key = rows![0].GetProperty("coverPhotoKey").GetString();
        key.Should().EndWith("/kapak.img");

        // URL imzalı ve kısa ömürlü; sözleşme "anahtarı içeren bir adres dönüyor".
        rows[0].GetProperty("coverPhotoUrl").GetString().Should().Contain(key!);
    }

    [Fact]
    public async Task Product_without_photos_reports_null_cover()
    {
        var (client, licenseId) = await SeedAsync(1);

        var rows = await client.GetFromJsonAsync<List<JsonElement>>(
            $"/api/v1/licenses/{licenseId}/catalog/products");

        rows![0].GetProperty("coverPhotoKey").ValueKind.Should().Be(JsonValueKind.Null);
        rows[0].GetProperty("coverPhotoUrl").ValueKind.Should().Be(JsonValueKind.Null);
    }

    /// <summary>
    /// Döngü doğruluğu: her satırın kendi coverPhotoKey/coverPhotoUrl ikilisi
    /// eşleşiyor; fotoğrafsız satırlarda ikisi de null. Tek ürünlü testlerde
    /// rows[i] yerine rows[0] yazılsa geçer — bu test birden çok ürünle
    /// gerçek indekslemeyi zorlar ve "continue" dalını kapsama alır.
    /// </summary>
    [Fact]
    public async Task Each_row_gets_its_own_cover_photo_url_and_photoless_rows_are_null()
    {
        // 4 ürün: 0. ve 2. (çift seed indeksi) fotoğraflı, 1. ve 3. fotoğrafsız.
        // API Id üstünde sıralıyor — seed sırası != API sırası olabilir. Bunun
        // için her satırı coverPhotoKey varlığına göre ayrı ayrı doğruluyoruz:
        // fotoğraflı satırda URL anahtarını içermeli, fotoğrafsızda ikisi de null.
        var (client, licenseId) = await SeedAsync(4, photoOnEvenOnly: true);

        var rows = await client.GetFromJsonAsync<List<JsonElement>>(
            $"/api/v1/licenses/{licenseId}/catalog/products");

        rows!.Should().HaveCount(4);

        // Tam 2 satır fotoğraflı, 2 satır fotoğrafsız olmalı.
        var withKey = rows.Where(r =>
            r.GetProperty("coverPhotoKey").ValueKind == JsonValueKind.String).ToList();
        var withoutKey = rows.Where(r =>
            r.GetProperty("coverPhotoKey").ValueKind == JsonValueKind.Null).ToList();

        withKey.Should().HaveCount(2, because: "çift-indeksli 2 ürün fotoğraflı");
        withoutKey.Should().HaveCount(2, because: "tek-indeksli 2 ürün fotoğrafsız");

        // Fotoğraflı satırlar: kapak anahtarı ve imzalı URL dolu, birbirine uygun.
        foreach (var row in withKey)
        {
            var key = row.GetProperty("coverPhotoKey").GetString()!;
            key.Should().EndWith("/kapak.img", because: "kapak en küçük SortOrder olmalı");
            var url = row.GetProperty("coverPhotoUrl");
            url.ValueKind.Should().Be(JsonValueKind.String, because: "imzalı URL almalı");
            url.GetString().Should().Contain(key, because: "URL kendi anahtarını içermeli");
        }

        // Fotoğrafsız satırlar: her ikisi de null.
        foreach (var row in withoutKey)
        {
            row.GetProperty("coverPhotoUrl").ValueKind.Should().Be(JsonValueKind.Null,
                because: "fotoğrafsız satırda coverPhotoUrl null olmalı");
        }
    }

    /// <summary>
    /// Hata dayanıklılığı: imzalama fırlattığında sayfa yine 200 döner,
    /// coverPhotoKey dolu ama coverPhotoUrl null kalır — katalog kurulabilir.
    /// </summary>
    [Fact]
    public async Task Signing_failure_keeps_page_200_and_sets_cover_url_to_null()
    {
        using var factory = new ThrowingStorageApiFactory();
        var (client, licenseId) = await SeedAsync(1, withPhotos: true, overrideFactory: factory);

        var rows = await client.GetFromJsonAsync<List<JsonElement>>(
            $"/api/v1/licenses/{licenseId}/catalog/products");

        rows!.Should().ContainSingle();
        rows[0].GetProperty("coverPhotoKey").GetString().Should().EndWith("/kapak.img");
        rows[0].GetProperty("coverPhotoUrl").ValueKind.Should().Be(JsonValueKind.Null);
    }

    /// <summary>
    /// CreateDownloadUrlAsync her çağrıda fırlatan storage. İmzalama hata
    /// dayanıklılığı testinde kullanılır.
    /// </summary>
    private sealed class ThrowingStorageApiFactory : ApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<OrderDeck.LicenseServer.Services.BroadcastPosts.IBroadcastMediaStorage>();
                services.AddSingleton<OrderDeck.LicenseServer.Services.BroadcastPosts.IBroadcastMediaStorage>(
                    new ThrowingBroadcastMediaStorage());
            });
        }
    }

    private sealed class ThrowingBroadcastMediaStorage :
        OrderDeck.LicenseServer.Services.BroadcastPosts.IBroadcastMediaStorage
    {
        public Task<string> CreateUploadUrlAsync(string objectKey, string contentType,
            long sizeBytes, CancellationToken ct = default)
            => Task.FromResult($"https://stub.local/{objectKey}?upload=1");

        public Task<string> CreateDownloadUrlAsync(string objectKey, CancellationToken ct = default)
            => throw new InvalidOperationException("Test: R2 kimlik bilgisi eksik — imzalama başarısız.");

        public Task<OrderDeck.LicenseServer.Services.BroadcastPosts.MediaObjectInfo?> HeadAsync(
            string objectKey, CancellationToken ct = default)
            => Task.FromResult<OrderDeck.LicenseServer.Services.BroadcastPosts.MediaObjectInfo?>(null);

        public Task<IReadOnlyList<OrderDeck.LicenseServer.Services.BroadcastPosts.MediaObjectListing>> ListAsync(
            string prefix, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<OrderDeck.LicenseServer.Services.BroadcastPosts.MediaObjectListing>>(
                Array.Empty<OrderDeck.LicenseServer.Services.BroadcastPosts.MediaObjectListing>());

        public Task DeleteAsync(string objectKey, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    [Fact]
    public async Task Categories_are_returned_ordered_by_path()
    {
        // Düzeltme 2: çocuğu önce AddRange'e koyuyor, adlar ters alfabetik seçildi.
        //   - Ekleme sırası yanlış cevabı verirdi (alt → kok).
        //   - Ad sıralaması da yanlış cevabı verirdi ("Ayakkabı" < "Üst Giyim" → alt önce).
        // Yalnız Path sıralaması doğru ata-önce sırayı üretebilir.
        //
        // Düzeltme 3: pasif (IsActive=false) üçüncü kategori eklendi ve döndüğü
        // doğrulandı; inceleyen'in .Where(c => c.IsActive) eklemesine karşı dayanıklı.
        // Ayrı test yerine burada tutuldu: kurgu zaten üç-kategori senaryosuna hazır
        // ve pasif sözleşme sıralama sözleşmesiyle aynı uçla korunuyor.
        var (client, licenseId) = await SeedAsync(0);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();

        var kok = new Category
        {
            Id = Guid.NewGuid(), LicenseId = licenseId,
            // "Üst Giyim" alfabetik olarak "Ayakkabı"dan SONRA gelir;
            // ad sıralaması kok'u sona iterdi — Path sıralaması önce koyuyor.
            Name = "Üst Giyim",
            SortOrder = 0, IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        };
        kok.Path = $"/{kok.Id:N}/";

        // Uç yalnız "ata önce, çocuk sonra" garantisi veriyor; kardeş sırası
        // Path içindeki kendi Id'ye göre lexicografik belirleniyor ve bu Id
        // Guid.NewGuid() olduğundan rastgele gelir. Testi deterministik kılmak
        // için iki GUID üretip küçük olanı "Ayakkabı"ya, büyük olanı "Atkı"ya
        // atıyoruz; böylece Ayakkabı < Atkı sırası Path üstünde garantileniyor.
        var childId1 = Guid.NewGuid();
        var childId2 = Guid.NewGuid();
        var (ayakkabiId, atkiId) = string.Compare(
            childId1.ToString("N"), childId2.ToString("N"),
            StringComparison.Ordinal) < 0
            ? (childId1, childId2)
            : (childId2, childId1);

        var alt = new Category
        {
            Id = ayakkabiId, LicenseId = licenseId,
            // "Ayakkabı" alfabetik olarak "Üst Giyim"den ÖNCE gelir;
            // ad sıralaması alt'ı başa iterdi — Path sıralaması çocuğu sonra koyuyor.
            Name = "Ayakkabı",
            ParentCategoryId = kok.Id, SortOrder = 0, IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        };
        alt.Path = $"/{kok.Id:N}/{alt.Id:N}/";

        // Düzeltme 3: pasif kategori — kökün ikinci çocuğu. "Atkı" alfabetik olarak
        // "Ayakkabı"dan ÖNCE gelir; ad sıralaması onu alt'ın da önüne koyardı.
        // Path sıralaması doğru sırayı (kok → alt → pasif) koruyor.
        var pasif = new Category
        {
            Id = atkiId, LicenseId = licenseId,
            Name = "Atkı",
            ParentCategoryId = kok.Id, SortOrder = 1, IsActive = false,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        };
        pasif.Path = $"/{kok.Id:N}/{pasif.Id:N}/";

        // Çocukları önce ekliyoruz — ekleme sırası yanlış cevabı verirdi.
        db.Categories.AddRange(alt, pasif, kok);
        await db.SaveChangesAsync();

        var rows = await client.GetFromJsonAsync<List<JsonElement>>(
            $"/api/v1/licenses/{licenseId}/catalog/categories");

        // Pasif kategori de dönmeli (Düzeltme 3).
        rows!.Should().HaveCount(3);
        // Path sıralaması: kok → alt → pasif (Düzeltme 2).
        rows[0].GetProperty("name").GetString().Should().Be("Üst Giyim");
        rows[1].GetProperty("name").GetString().Should().Be("Ayakkabı");
        rows[1].GetProperty("parentCategoryId").GetString()
               .Should().Be(kok.Id.ToString());
        rows[2].GetProperty("name").GetString().Should().Be("Atkı");
        rows[2].GetProperty("parentCategoryId").GetString()
               .Should().Be(kok.Id.ToString());
    }

    [Fact]
    public async Task Categories_of_a_foreign_license_are_not_returned()
    {
        // Düzeltme 1: rastgele Guid yerine gerçek yabancı lisans tohumlanıyor.
        // Önceki sürüm yalnız "var olmayan lisans 404 döner" diyordu.
        // Bu sürüm iki ayrı müşteri oluşturur; birincinin istemcisi ikincinin
        // lisansını isteyerek kiracı yalıtımını zorlar.
        var (client, _) = await SeedAsync(0);
        var (_, yabanciLicenseId) = await SeedAsync(0);

        var res = await client.GetAsync(
            $"/api/v1/licenses/{yabanciLicenseId}/catalog/categories");

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
