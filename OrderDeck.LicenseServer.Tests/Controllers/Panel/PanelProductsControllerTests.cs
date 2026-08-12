using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Catalog;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Panel;

public class PanelProductsControllerTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public PanelProductsControllerTests(ApiFactory f) => _factory = f;

    private sealed record VariantDto(
        Guid Id, string? Axis1Value, string? Axis1Code,
        string? Axis2Value, string? Axis2Code,
        string VariantCode, string? Barcode, bool IsActive);

    private sealed record ProductDto(
        Guid Id, Guid? CategoryId, string Code, string Name,
        decimal DefaultPrice, decimal? Cost,
        string? ShelfLocation,
        string? Axis1Name, int? Axis1Role,
        string? Axis2Name, int? Axis2Role,
        bool IsArchived, List<VariantDto> Variants);

    private sealed record ProductRow(
        Guid Id, Guid? CategoryId, string Code, string Name,
        decimal DefaultPrice, bool IsArchived, int VariantCount);

    private sealed record ProductPage(List<ProductRow> Items, int Total);

    private sealed record CategoryDto(
        Guid Id, Guid? ParentCategoryId, string Name, string Path,
        int Depth, int SortOrder, bool IsActive);

    private sealed record NextCodeDto(string Code);

    private static async Task<string?> TitleAsync(HttpResponseMessage resp)
    {
        var json = await resp.Content.ReadAsStringAsync();
        if (string.IsNullOrWhiteSpace(json)) return null;
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("title", out var t) ? t.GetString() : null;
    }

    /// <summary>
    /// <c>[MaxLength]</c> ihlalini [ApiController] standart
    /// <c>ValidationProblemDetails</c> ile döner — <c>Problem(title: "…")</c>
    /// slug'ıyla değil. Bu yüzden başlık yerine <c>errors</c> sözlüğüne bakılır.
    /// </summary>
    private static async Task<bool> HasValidationErrorAsync(
        HttpResponseMessage resp, string field)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(
            await resp.Content.ReadAsStringAsync());
        return doc.RootElement.TryGetProperty("errors", out var errors)
               && errors.TryGetProperty(field, out _);
    }

    private async Task<(HttpClient client, Guid licenseId)> SeedAsync()
    {
        var (client, customerId, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var license = new License
        {
            Id = Guid.NewGuid(), CustomerId = customerId,
            LicenseKey = "LDK-PROD-" + Guid.NewGuid().ToString("N"),
            SkuCode = "STD", ActivationSlots = 1,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
        };
        db.Licenses.Add(license);
        await db.SaveChangesAsync();
        return (client, license.Id);
    }

    private static async Task<CategoryDto> CreateCategoryAsync(
        HttpClient client, string name, Guid? parentId = null)
    {
        var resp = await client.PostAsJsonAsync("/api/panel/categories",
            new { name, parentCategoryId = parentId, sortOrder = 0 });
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await resp.Content.ReadFromJsonAsync<CategoryDto>())!;
    }

    private static Task<HttpResponseMessage> PostProductAsync(
        HttpClient client, string name, string? code = null, Guid? categoryId = null,
        decimal price = 100m, decimal? cost = null,
        string? axis1Name = null, int? axis1Role = null,
        string? axis2Name = null, int? axis2Role = null)
        => client.PostAsJsonAsync("/api/panel/products", new
        {
            name, code, categoryId, defaultPrice = price, cost,
            axis1Name, axis1Role, axis2Name, axis2Role,
        });

    private static async Task<ProductDto> CreateProductAsync(
        HttpClient client, string name, string? code = null, Guid? categoryId = null,
        decimal price = 100m, string? axis1Name = null, int? axis1Role = null,
        string? axis2Name = null, int? axis2Role = null)
    {
        var resp = await PostProductAsync(client, name, code, categoryId, price,
            null, axis1Name, axis1Role, axis2Name, axis2Role);
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await resp.Content.ReadFromJsonAsync<ProductDto>())!;
    }

    private static Task<HttpResponseMessage> PutProductAsync(
        HttpClient client, ProductDto product, string? code = null,
        string? axis1Name = null, int? axis1Role = null,
        string? axis2Name = null, int? axis2Role = null)
        => client.PutAsJsonAsync($"/api/panel/products/{product.Id}", new
        {
            name = product.Name,
            code = code ?? product.Code,
            categoryId = product.CategoryId,
            defaultPrice = product.DefaultPrice,
            cost = product.Cost,
            axis1Name, axis1Role, axis2Name, axis2Role,
        });

    private static async Task<VariantDto> PostVariantAsync(
        HttpClient client, Guid productId,
        string? axis1Value = null, string? axis2Value = null)
    {
        var resp = await client.PostAsJsonAsync(
            $"/api/panel/products/{productId}/variants",
            new
            {
                axis1Value, axis1Code = (string?)null,
                axis2Value, axis2Code = (string?)null, isActive = true,
            });
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await resp.Content.ReadFromJsonAsync<VariantDto>())!;
    }

    /// <summary>
    /// Değişmez kural: bir üründeki HER varyantın kodu, güncel ürün kodu ile o
    /// varyantın eksen kod parçalarından üretilene birebir eşit olmalı — hangi
    /// uç noktanın yazdığı fark etmez. Yeni bir yazma yolu türetmeyi unutursa
    /// bu doğrulama düşer.
    /// </summary>
    private void AssertVariantCodesAreDerived(Guid productId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var product = db.Products.Include(p => p.Variants).Single(p => p.Id == productId);

        foreach (var variant in product.Variants)
        {
            variant.VariantCode.Should().Be(
                VariantCodeBuilder.Build(product.Code, variant.Axis1Code, variant.Axis2Code),
                "'{0}' varyantının kodu güncel ürün kodundan türetilmiş olmalı",
                variant.Id);
        }
    }

    [Fact]
    public async Task Requires_authentication()
    {
        var client = _factory.CreateClient();

        var resp = await client.GetAsync("/api/panel/products");

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Create_assigns_A1_to_the_first_product()
    {
        var (client, _) = await SeedAsync();

        var product = await CreateProductAsync(client, "Basic tişört");

        product.Code.Should().Be("A1");
    }

    [Fact]
    public async Task Create_assigns_A2_to_the_second_product()
    {
        var (client, _) = await SeedAsync();
        await CreateProductAsync(client, "Birinci");

        var second = await CreateProductAsync(client, "İkinci");

        second.Code.Should().Be("A2");
    }

    [Fact]
    public async Task Create_normalizes_the_manual_code_and_rejects_the_duplicate()
    {
        var (client, _) = await SeedAsync();

        var first = await CreateProductAsync(client, "Elle kodlu", code: "  a5 ");
        first.Code.Should().Be("A5");

        var resp = await PostProductAsync(client, "Aynı kod", code: "A5");

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await TitleAsync(resp)).Should().Be("duplicate-code");
    }

    [Fact]
    public async Task Create_400_on_empty_name()
    {
        var (client, _) = await SeedAsync();

        var resp = await PostProductAsync(client, "   ");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await TitleAsync(resp)).Should().Be("missing-name");
    }

    /// <summary>
    /// Kolonlar sınırlı (<c>Name</c> 200, <c>Code</c> 32, eksen adı 40); sınır
    /// DTO'da duyurulmazsa taşan girdi prod'da kesme hatasına, yani 500'e düşer.
    /// InMemory bunu göremediği için sınır sunucu tarafında kapatılmalı.
    /// </summary>
    [Theory]
    [InlineData("Name")]
    [InlineData("Code")]
    [InlineData("Axis1Name")]
    [InlineData("Axis2Name")]
    public async Task Create_400_when_a_field_exceeds_its_column_limit(string field)
    {
        var (client, _) = await SeedAsync();

        var resp = await PostProductAsync(client,
            name: field == "Name" ? new string('A', CatalogLimits.ProductName + 1) : "Uzun alan",
            code: field == "Code" ? new string('A', CatalogLimits.ProductCode + 1) : null,
            axis1Name: field is "Axis1Name" or "Axis2Name"
                ? new string('A', CatalogLimits.AxisName + 1) : null,
            axis1Role: field is "Axis1Name" or "Axis2Name" ? 1 : null,
            axis2Name: field == "Axis2Name"
                ? new string('B', CatalogLimits.AxisName + 1) : null,
            axis2Role: field == "Axis2Name" ? 2 : null);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await HasValidationErrorAsync(resp, field)).Should().BeTrue();
    }

    [Fact]
    public async Task Update_400_when_the_name_exceeds_its_column_limit()
    {
        var (client, _) = await SeedAsync();
        var product = await CreateProductAsync(client, "Kısa ad");

        var resp = await client.PutAsJsonAsync($"/api/panel/products/{product.Id}", new
        {
            name = new string('A', CatalogLimits.ProductName + 1),
            code = product.Code,
            categoryId = (Guid?)null,
            defaultPrice = product.DefaultPrice,
            cost = (decimal?)null,
            axis1Name = (string?)null, axis1Role = (int?)null,
            axis2Name = (string?)null, axis2Role = (int?)null,
        });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await HasValidationErrorAsync(resp, "Name")).Should().BeTrue();
    }

    /// <summary>
    /// <c>page * pageSize</c> int aritmetiğinde taşıyordu: 2147483647 negatif
    /// bir atlamaya dönüşüp <c>OFFSET -N ROWS</c> üretiyor, SQL Server hata
    /// veriyordu. Atlama hiçbir girdide negatif olmamalı.
    /// </summary>
    [Fact]
    public async Task List_does_not_overflow_on_an_absurd_page_number()
    {
        var (client, _) = await SeedAsync();
        await CreateProductAsync(client, "Tek ürün");

        var page = await client.GetFromJsonAsync<ProductPage>(
            $"/api/panel/products?page={int.MaxValue}&pageSize=50");

        page!.Items.Should().BeEmpty();
        page.Total.Should().Be(1);
    }

    [Fact]
    public async Task List_treats_a_negative_page_as_the_first_page()
    {
        var (client, _) = await SeedAsync();
        await CreateProductAsync(client, "Tek ürün");

        var page = await client.GetFromJsonAsync<ProductPage>(
            $"/api/panel/products?page={int.MinValue}&pageSize=50");

        page!.Items.Should().ContainSingle();
    }

    [Fact]
    public async Task Create_400_on_negative_price()
    {
        var (client, _) = await SeedAsync();

        var resp = await PostProductAsync(client, "Eksi fiyat", price: -1m);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await TitleAsync(resp)).Should().Be("invalid-price");
    }

    [Fact]
    public async Task Create_400_on_unknown_category()
    {
        var (client, _) = await SeedAsync();

        var resp = await PostProductAsync(client, "Kayıp kategori", categoryId: Guid.NewGuid());

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await TitleAsync(resp)).Should().Be("category-not-found");
    }

    [Fact]
    public async Task Create_400_when_axis2_is_set_without_axis1()
    {
        var (client, _) = await SeedAsync();

        var resp = await PostProductAsync(client, "Ters eksen",
            axis2Name: "Beden", axis2Role: 1);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await TitleAsync(resp)).Should().Be("axis-order");
    }

    [Fact]
    public async Task Create_400_when_an_axis_has_no_role()
    {
        var (client, _) = await SeedAsync();

        var resp = await PostProductAsync(client, "Rolsüz eksen", axis1Name: "Renk");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await TitleAsync(resp)).Should().Be("missing-axis-role");
    }

    [Fact]
    public async Task Create_400_when_both_axes_share_a_role()
    {
        var (client, _) = await SeedAsync();

        var resp = await PostProductAsync(client, "Çift satıcı",
            axis1Name: "Renk", axis1Role: 1, axis2Name: "Beden", axis2Role: 1);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await TitleAsync(resp)).Should().Be("duplicate-axis-role");
    }

    [Fact]
    public async Task Axisless_product_gets_exactly_one_auto_variant()
    {
        var (client, _) = await SeedAsync();

        var product = await CreateProductAsync(client, "Tek kalem");

        product.Variants.Should().HaveCount(1);
        product.Variants[0].VariantCode.Should().Be(product.Code);
        product.Variants[0].Axis1Value.Should().BeNull();
    }

    [Fact]
    public async Task Product_with_an_axis_starts_with_no_variants()
    {
        var (client, _) = await SeedAsync();

        var product = await CreateProductAsync(client, "Renkli",
            axis1Name: "Renk", axis1Role: 2);

        product.Variants.Should().BeEmpty();
    }

    [Fact]
    public async Task List_filters_by_the_category_subtree()
    {
        var (client, _) = await SeedAsync();
        var erkek = await CreateCategoryAsync(client, "Erkek");
        var tisort = await CreateCategoryAsync(client, "Tişört", erkek.Id);
        var kadin = await CreateCategoryAsync(client, "Kadın");
        await CreateProductAsync(client, "Erkek tişört", categoryId: tisort.Id);
        await CreateProductAsync(client, "Kadın elbise", categoryId: kadin.Id);

        var page = await client.GetFromJsonAsync<ProductPage>(
            $"/api/panel/products?categoryId={erkek.Id}");

        page!.Items.Should().ContainSingle(p => p.Name == "Erkek tişört");
        page.Total.Should().Be(1);
    }

    [Fact]
    public async Task List_filters_by_name_or_code()
    {
        var (client, _) = await SeedAsync();
        await CreateProductAsync(client, "Keten gömlek");
        var pantolon = await CreateProductAsync(client, "Kot pantolon");

        var byName = await client.GetFromJsonAsync<ProductPage>("/api/panel/products?q=pantolon");
        byName!.Items.Should().ContainSingle(p => p.Name == "Kot pantolon");

        var byCode = await client.GetFromJsonAsync<ProductPage>(
            $"/api/panel/products?q={pantolon.Code.ToLowerInvariant()}");
        byCode!.Items.Should().ContainSingle(p => p.Id == pantolon.Id);
    }

    /// <summary>
    /// Alt ağaç filtresi tek bir ilişkilendirilmiş alt sorguya indi (id listesini
    /// istemciye çekip <c>IN (...)</c> göndermek yerine). Kiracı sınırı bu
    /// dönüşümde kaybolmamalı: başka lisansın ürünü hiçbir koşulda sızmamalı.
    /// </summary>
    [Fact]
    public async Task List_never_leaks_another_tenants_products_through_the_subtree_filter()
    {
        var (clientA, _) = await SeedAsync();
        var erkek = await CreateCategoryAsync(clientA, "Erkek");
        var tisort = await CreateCategoryAsync(clientA, "Tişört", erkek.Id);
        var mine = await CreateProductAsync(clientA, "A alt ürünü", categoryId: tisort.Id);

        var (clientB, _) = await SeedAsync();
        var theirs = await CreateProductAsync(
            clientB, "B ürünü",
            categoryId: (await CreateCategoryAsync(clientB, "Erkek")).Id);

        var page = await clientA.GetFromJsonAsync<ProductPage>(
            $"/api/panel/products?categoryId={erkek.Id}");

        page!.Items.Should().ContainSingle(p => p.Id == mine.Id,
            "alt kategorideki ürün üst kategori filtresiyle gelmeli");
        page.Items.Should().NotContain(p => p.Id == theirs.Id);
        page.Total.Should().Be(1);
    }

    /// <summary>
    /// Arama artık veritabanı collation'ına değil, saklanan türetilmiş kolona
    /// dayanıyor: kullanıcının nasıl yazdığı (büyük/küçük harf, Türkçe harf)
    /// sonucu değiştirmemeli. PostgreSQL göçünde bu davranış aynı kalır.
    /// </summary>
    [Theory]
    [InlineData("tisort")]
    [InlineData("TİŞÖRT")]
    [InlineData("kirmizi")]
    [InlineData("  Kırmızı Tişört  ")]
    public async Task List_search_is_case_and_turkish_letter_insensitive(string typed)
    {
        var (client, _) = await SeedAsync();
        var product = await CreateProductAsync(client, "Kırmızı Tişört");

        var page = await client.GetFromJsonAsync<ProductPage>(
            "/api/panel/products?q=" + Uri.EscapeDataString(typed));

        page!.Items.Should().ContainSingle(p => p.Id == product.Id,
            "'{0}' araması ürünü bulmalı", typed);
    }

    [Fact]
    public async Task List_reports_the_variant_count()
    {
        var (client, _) = await SeedAsync();
        await CreateProductAsync(client, "Tek kalem");

        var page = await client.GetFromJsonAsync<ProductPage>("/api/panel/products");

        page!.Items.Single().VariantCount.Should().Be(1);
    }

    [Fact]
    public async Task Get_of_another_tenants_product_returns_404()
    {
        var (clientA, _) = await SeedAsync();
        var product = await CreateProductAsync(clientA, "A ürünü");
        var (clientB, _) = await SeedAsync();

        var resp = await clientB.GetAsync($"/api/panel/products/{product.Id}");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// Kiracı sınırı okumada olduğu gibi YAZMADA da geçerli. Kural 404 (403
    /// değil): başka kiracının kaydı bizim için var olmamalı, varlığını durum
    /// koduyla ele vermek sızıntıdır.
    /// </summary>
    [Fact]
    public async Task Update_404_for_another_tenants_product()
    {
        var (clientA, _) = await SeedAsync();
        var product = await CreateProductAsync(clientA, "A ürünü");
        var (clientB, _) = await SeedAsync();

        var resp = await clientB.PutAsJsonAsync($"/api/panel/products/{product.Id}", new
        {
            name = "B ele geçirdi", code = product.Code, categoryId = (Guid?)null,
            defaultPrice = 999m, cost = (decimal?)null,
            axis1Name = (string?)null, axis1Role = (int?)null,
            axis2Name = (string?)null, axis2Role = (int?)null,
        });

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);

        // 404 dönüp yine de yazmış olmak yalnız duruma bakan bir testten kaçar.
        var after = await clientA.GetFromJsonAsync<ProductDto>(
            $"/api/panel/products/{product.Id}");
        after!.Name.Should().Be("A ürünü");
        after.DefaultPrice.Should().Be(100m);
    }

    /// <summary>
    /// Silmede 404 tek başına yetmez: kayıt sahibinin gözünden HÂLÂ DURUYOR
    /// olmalı. 404 dönüp yine de silen bir uç, yalnız durum kodu kontrol eden
    /// bir testin gözünden kaçar.
    /// </summary>
    [Fact]
    public async Task Delete_404_for_another_tenants_product()
    {
        var (clientA, _) = await SeedAsync();
        var product = await CreateProductAsync(clientA, "Silinemeyecek");
        var (clientB, _) = await SeedAsync();

        var resp = await clientB.DeleteAsync($"/api/panel/products/{product.Id}");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await clientA.GetAsync($"/api/panel/products/{product.Id}"))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        db.Products.Any(p => p.Id == product.Id).Should().BeTrue();
    }

    [Fact]
    public async Task Update_renames_and_moves_the_product()
    {
        var (client, _) = await SeedAsync();
        var category = await CreateCategoryAsync(client, "Ayakkabı");
        var product = await CreateProductAsync(client, "Eski ad");

        var resp = await client.PutAsJsonAsync($"/api/panel/products/{product.Id}", new
        {
            name = "Yeni ad", code = product.Code, categoryId = category.Id,
            defaultPrice = 250m, cost = 120m,
            axis1Name = (string?)null, axis1Role = (int?)null,
            axis2Name = (string?)null, axis2Role = (int?)null,
        });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = (await resp.Content.ReadFromJsonAsync<ProductDto>())!;
        dto.Name.Should().Be("Yeni ad");
        dto.CategoryId.Should().Be(category.Id);
        dto.DefaultPrice.Should().Be(250m);
        dto.Cost.Should().Be(120m);
    }

    [Fact]
    public async Task Update_409_when_the_code_is_taken_by_a_sibling()
    {
        var (client, _) = await SeedAsync();
        await CreateProductAsync(client, "Birinci", code: "K1");
        var second = await CreateProductAsync(client, "İkinci", code: "K2");

        var resp = await PutProductAsync(client, second, code: "k1");

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await TitleAsync(resp)).Should().Be("duplicate-code");
    }

    /// <summary>
    /// Kart kendi koduyla kaydedilince kendi kendiyle çakışmamalı — benzersizlik
    /// kontrolü kendi satırını dışlamazsa her sıradan güncelleme 409 yerdi.
    /// </summary>
    [Fact]
    public async Task Update_200_when_the_product_keeps_its_own_code()
    {
        var (client, _) = await SeedAsync();
        var product = await CreateProductAsync(client, "Kendi kodu", code: "K9");

        var resp = await PutProductAsync(client, product, code: "K9");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await resp.Content.ReadFromJsonAsync<ProductDto>())!.Code.Should().Be("K9");
    }

    [Fact]
    public async Task Update_rewrites_the_auto_variant_code_when_the_product_code_changes()
    {
        var (client, _) = await SeedAsync();
        var product = await CreateProductAsync(client, "Tek kalem");

        var resp = await client.PutAsJsonAsync($"/api/panel/products/{product.Id}", new
        {
            name = product.Name, code = "B7", categoryId = (Guid?)null,
            defaultPrice = product.DefaultPrice, cost = (decimal?)null,
            axis1Name = (string?)null, axis1Role = (int?)null,
            axis2Name = (string?)null, axis2Role = (int?)null,
        });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = (await resp.Content.ReadFromJsonAsync<ProductDto>())!;
        dto.Code.Should().Be("B7");
        dto.Variants.Single().VariantCode.Should().Be("B7");
    }

    [Fact]
    public async Task Update_rewrites_the_variant_codes_of_an_axis_product_when_the_code_changes()
    {
        var (client, _) = await SeedAsync();
        var product = await CreateProductAsync(client, "Renkli",
            axis1Name: "Renk", axis1Role: 2);
        await PostVariantAsync(client, product.Id, axis1Value: "Siyah");

        var resp = await PutProductAsync(client, product, code: "B7",
            axis1Name: "Renk", axis1Role: 2);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = (await resp.Content.ReadFromJsonAsync<ProductDto>())!;
        dto.Code.Should().Be("B7");
        dto.Variants.Single().VariantCode.Should().Be("B7-SIYA");
        AssertVariantCodesAreDerived(product.Id);
    }

    [Fact]
    public async Task Update_never_touches_the_barcode_when_the_product_code_changes()
    {
        var (client, _) = await SeedAsync();
        var product = await CreateProductAsync(client, "Barkotlu",
            axis1Name: "Renk", axis1Role: 2);
        var variant = await PostVariantAsync(client, product.Id, axis1Value: "Siyah");

        // Faz 1c'yi taklit et: rafta duran etiket zaten basılmış.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            db.ProductVariants.Single(v => v.Id == variant.Id).Barcode = "8690000000017";
            await db.SaveChangesAsync();
        }

        var resp = await PutProductAsync(client, product, code: "C4",
            axis1Name: "Renk", axis1Role: 2);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = (await resp.Content.ReadFromJsonAsync<ProductDto>())!;
        dto.Variants.Single().VariantCode.Should().Be("C4-SIYA");
        dto.Variants.Single().Barcode.Should().Be("8690000000017");
    }

    [Fact]
    public async Task Variant_codes_stay_derived_across_the_whole_product_lifecycle()
    {
        var (client, _) = await SeedAsync();
        var product = await CreateProductAsync(client, "Çok eksenli",
            axis1Name: "Renk", axis1Role: 2, axis2Name: "Beden", axis2Role: 1);

        var siyah = await PostVariantAsync(client, product.Id, "Siyah", "38");
        var beyaz = await PostVariantAsync(client, product.Id, "Beyaz", "40");
        AssertVariantCodesAreDerived(product.Id);

        // 1) Ürün kodu değişti — iki varyant da yenilenmeli.
        (await PutProductAsync(client, product, code: "Z9",
            axis1Name: "Renk", axis1Role: 2, axis2Name: "Beden", axis2Role: 1))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        AssertVariantCodesAreDerived(product.Id);

        var afterRename = await client.GetFromJsonAsync<ProductDto>(
            $"/api/panel/products/{product.Id}");
        afterRename!.Variants.Select(v => v.VariantCode)
            .Should().BeEquivalentTo(["Z9-SIYA-38", "Z9-BEYA-40"]);

        // 2) Varyantın kendi eksen değeri değişti.
        (await client.PutAsJsonAsync(
            $"/api/panel/products/{product.Id}/variants/{beyaz.Id}",
            new
            {
                axis1Value = "Mavi", axis1Code = (string?)null,
                axis2Value = "42", axis2Code = (string?)null, isActive = true,
            })).StatusCode.Should().Be(HttpStatusCode.OK);
        AssertVariantCodesAreDerived(product.Id);

        // 3) Eksenler kapandı — geriye tek otomatik varyant kalır.
        foreach (var id in new[] { siyah.Id, beyaz.Id })
            (await client.DeleteAsync($"/api/panel/products/{product.Id}/variants/{id}"))
                .StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await PutProductAsync(client, afterRename, code: "Z9"))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        AssertVariantCodesAreDerived(product.Id);

        // 4) Eksensiz ürünün kodu bir kez daha değişti.
        (await PutProductAsync(client, afterRename, code: "Z8"))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        AssertVariantCodesAreDerived(product.Id);

        var final = await client.GetFromJsonAsync<ProductDto>(
            $"/api/panel/products/{product.Id}");
        final!.Variants.Single().VariantCode.Should().Be("Z8");
    }

    [Fact]
    public async Task Update_drops_the_auto_variant_when_an_axis_is_switched_on()
    {
        var (client, _) = await SeedAsync();
        var product = await CreateProductAsync(client, "Tek kalem");
        product.Variants.Should().HaveCount(1);

        var resp = await client.PutAsJsonAsync($"/api/panel/products/{product.Id}", new
        {
            name = product.Name, code = product.Code, categoryId = (Guid?)null,
            defaultPrice = product.DefaultPrice, cost = (decimal?)null,
            axis1Name = "Renk", axis1Role = 2,
            axis2Name = (string?)null, axis2Role = (int?)null,
        });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = (await resp.Content.ReadFromJsonAsync<ProductDto>())!;
        dto.Axis1Name.Should().Be("Renk");
        dto.Variants.Should().BeEmpty();
    }

    [Fact]
    public async Task Update_409_when_an_axis_is_switched_off_while_valued_variants_exist()
    {
        var (client, _) = await SeedAsync();
        var product = await CreateProductAsync(client, "Renkli",
            axis1Name: "Renk", axis1Role: 2);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var entity = db.Products.Single(p => p.Id == product.Id);
            db.ProductVariants.Add(new ProductVariant
            {
                Id = Guid.NewGuid(), LicenseId = entity.LicenseId, ProductId = entity.Id,
                Axis1Value = "Siyah", Axis1Code = "SIYA",
                VariantCode = entity.Code + "-SIYA", IsActive = true,
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var resp = await client.PutAsJsonAsync($"/api/panel/products/{product.Id}", new
        {
            name = product.Name, code = product.Code, categoryId = (Guid?)null,
            defaultPrice = product.DefaultPrice, cost = (decimal?)null,
            axis1Name = (string?)null, axis1Role = (int?)null,
            axis2Name = (string?)null, axis2Role = (int?)null,
        });

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await TitleAsync(resp)).Should().Be("axis-in-use");
    }

    [Fact]
    public async Task Update_409_when_an_axis_is_renamed_while_valued_variants_exist()
    {
        var (client, _) = await SeedAsync();
        var product = await CreateProductAsync(client, "Renkli",
            axis1Name: "Renk", axis1Role: 2);
        await PostVariantAsync(client, product.Id, axis1Value: "Siyah");

        var resp = await PutProductAsync(client, product,
            axis1Name: "Beden", axis1Role: 2);

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await TitleAsync(resp)).Should().Be("axis-in-use");

        var after = await client.GetFromJsonAsync<ProductDto>(
            $"/api/panel/products/{product.Id}");
        after!.Axis1Name.Should().Be("Renk");
        after.Variants.Single().Axis1Value.Should().Be("Siyah");
    }

    [Fact]
    public async Task Update_409_when_the_two_axes_are_swapped_while_valued_variants_exist()
    {
        var (client, _) = await SeedAsync();
        var product = await CreateProductAsync(client, "Çift eksenli",
            axis1Name: "Renk", axis1Role: 2, axis2Name: "Beden", axis2Role: 1);
        await PostVariantAsync(client, product.Id, axis1Value: "Siyah", axis2Value: "38");

        var resp = await PutProductAsync(client, product,
            axis1Name: "Beden", axis1Role: 1, axis2Name: "Renk", axis2Role: 2);

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await TitleAsync(resp)).Should().Be("axis-in-use");

        var after = await client.GetFromJsonAsync<ProductDto>(
            $"/api/panel/products/{product.Id}");
        after!.Axis1Name.Should().Be("Renk");
        after.Axis2Name.Should().Be("Beden");
    }

    [Fact]
    public async Task Update_409_when_an_axis_role_changes_while_valued_variants_exist()
    {
        var (client, _) = await SeedAsync();
        var product = await CreateProductAsync(client, "Rol değişimi",
            axis1Name: "Renk", axis1Role: 2);
        await PostVariantAsync(client, product.Id, axis1Value: "Siyah");

        var resp = await PutProductAsync(client, product,
            axis1Name: "Renk", axis1Role: 1);

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await TitleAsync(resp)).Should().Be("axis-in-use");

        var after = await client.GetFromJsonAsync<ProductDto>(
            $"/api/panel/products/{product.Id}");
        after!.Axis1Role.Should().Be(2);
    }

    [Fact]
    public async Task Update_renames_the_axis_when_no_variant_carries_a_value()
    {
        var (client, _) = await SeedAsync();
        var product = await CreateProductAsync(client, "Yeni kart",
            axis1Name: "Renkk", axis1Role: 2);

        var resp = await PutProductAsync(client, product,
            axis1Name: "Renk", axis1Role: 2);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = (await resp.Content.ReadFromJsonAsync<ProductDto>())!;
        dto.Axis1Name.Should().Be("Renk");
        dto.Variants.Should().BeEmpty();
    }

    [Fact]
    public async Task Update_swaps_the_axes_when_no_variant_carries_a_value()
    {
        var (client, _) = await SeedAsync();
        var product = await CreateProductAsync(client, "Ters kurulmuş",
            axis1Name: "Renk", axis1Role: 2, axis2Name: "Beden", axis2Role: 1);

        var resp = await PutProductAsync(client, product,
            axis1Name: "Beden", axis1Role: 1, axis2Name: "Renk", axis2Role: 2);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = (await resp.Content.ReadFromJsonAsync<ProductDto>())!;
        dto.Axis1Name.Should().Be("Beden");
        dto.Axis1Role.Should().Be(1);
        dto.Axis2Name.Should().Be("Renk");
        dto.Axis2Role.Should().Be(2);
        dto.Variants.Should().BeEmpty();
    }

    [Fact]
    public async Task Update_changes_the_axis_role_when_no_variant_carries_a_value()
    {
        var (client, _) = await SeedAsync();
        var product = await CreateProductAsync(client, "Rol düzeltme",
            axis1Name: "Renk", axis1Role: 2);

        var resp = await PutProductAsync(client, product,
            axis1Name: "Renk", axis1Role: 1);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = (await resp.Content.ReadFromJsonAsync<ProductDto>())!;
        dto.Axis1Role.Should().Be(1);
    }

    [Fact]
    public async Task Update_rebuilds_the_auto_variant_when_the_axis_is_switched_off_unused()
    {
        var (client, _) = await SeedAsync();
        var product = await CreateProductAsync(client, "Eksen kapanıyor",
            axis1Name: "Renk", axis1Role: 2);

        var resp = await PutProductAsync(client, product);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = (await resp.Content.ReadFromJsonAsync<ProductDto>())!;
        dto.Axis1Name.Should().BeNull();
        dto.Axis1Role.Should().BeNull();
        dto.Variants.Single().VariantCode.Should().Be(dto.Code);
        dto.Variants.Single().Axis1Value.Should().BeNull();
        AssertVariantCodesAreDerived(product.Id);
    }

    /// <summary>
    /// Katı kuralın taşıdığı asıl risk: aynı eksen adlarını geri gönderen sıradan
    /// bir kaydetme (ad/fiyat düzeltme) yanlışlıkla 409'a düşerse gerçek
    /// kullanıcılar anında çarpar. Boşluk kırpma sonrası da no-op sayılmalı.
    /// </summary>
    [Fact]
    public async Task Update_with_an_unchanged_axis_payload_keeps_the_valued_variants()
    {
        var (client, _) = await SeedAsync();
        var product = await CreateProductAsync(client, "Değişmeyen eksen",
            axis1Name: "Renk", axis1Role: 2, axis2Name: "Beden", axis2Role: 1);
        var variant = await PostVariantAsync(client, product.Id, "Siyah", "38");

        var resp = await PutProductAsync(client, product,
            axis1Name: "  Renk ", axis1Role: 2, axis2Name: "Beden ", axis2Role: 1);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = (await resp.Content.ReadFromJsonAsync<ProductDto>())!;
        dto.Axis1Name.Should().Be("Renk");
        dto.Axis2Name.Should().Be("Beden");
        dto.Variants.Single().Id.Should().Be(variant.Id);
        dto.Variants.Single().Axis1Value.Should().Be("Siyah");
        dto.Variants.Single().Axis2Value.Should().Be("38");
        AssertVariantCodesAreDerived(product.Id);
    }

    [Fact]
    public async Task Delete_removes_the_product_and_its_variants()
    {
        var (client, _) = await SeedAsync();
        var product = await CreateProductAsync(client, "Silinecek");

        var resp = await client.DeleteAsync($"/api/panel/products/{product.Id}");

        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        db.Products.Any(p => p.Id == product.Id).Should().BeFalse();
        db.ProductVariants.Any(v => v.ProductId == product.Id).Should().BeFalse();
    }

    /// <summary>
    /// Fotoğraf iliştirir: panelin R2'ye yaptığı PUT'un yerine depo seed'i geçer,
    /// sonra gerçek Attach ucu çağrılır. Anahtarı döner.
    /// </summary>
    private async Task<string> AttachPhotoAsync(HttpClient client, Guid productId)
    {
        var upload = await client.PostAsJsonAsync(
            $"/api/panel/products/{productId}/photos/upload-url",
            new { contentType = "image/jpeg", sizeBytes = 120_000 });
        upload.StatusCode.Should().Be(HttpStatusCode.OK);
        var key = (await upload.Content
            .ReadFromJsonAsync<UploadUrlDto>())!.ObjectKey;

        _factory.BroadcastMedia.Seed(key, 120_000, "image/jpeg");

        var attach = await client.PostAsJsonAsync(
            $"/api/panel/products/{productId}/photos",
            new { objectKey = key, width = 800, height = 800 });
        attach.StatusCode.Should().Be(HttpStatusCode.Created);
        return key;
    }

    private sealed record UploadUrlDto(string ObjectKey, string UploadUrl);

    [Fact]
    public async Task Delete_also_removes_the_photo_object_from_storage()
    {
        var (client, _) = await SeedAsync();
        var product = await CreateProductAsync(client, "Fotoğraflı silinecek");
        var key = await AttachPhotoAsync(client, product.Id);

        var resp = await client.DeleteAsync($"/api/panel/products/{product.Id}");

        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);
        _factory.BroadcastMedia.DeleteCalls.Should().Contain(key);
        (await _factory.BroadcastMedia.HeadAsync(key)).Should().BeNull();
    }

    [Fact]
    public async Task Delete_does_not_touch_storage_when_the_product_has_no_photo()
    {
        var (client, _) = await SeedAsync();
        var product = await CreateProductAsync(client, "Fotoğrafsız silinecek");
        var before = _factory.BroadcastMedia.DeleteCalls.Count;

        var resp = await client.DeleteAsync($"/api/panel/products/{product.Id}");

        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);
        _factory.BroadcastMedia.DeleteCalls.Count.Should().Be(before,
            "fotoğrafsız üründe depoya hiç silme çağrısı gitmemeli");
    }

    [Fact]
    public async Task Next_code_endpoint_returns_the_next_free_code()
    {
        var (client, _) = await SeedAsync();
        await CreateProductAsync(client, "Birinci");

        var dto = await client.GetFromJsonAsync<NextCodeDto>("/api/panel/products/next-code");

        dto!.Code.Should().Be("A2");
    }

    [Fact]
    public async Task Shelf_location_is_saved_trimmed_and_blank_becomes_null()
    {
        var (client, _) = await SeedAsync();

        var created = await client.PostAsJsonAsync("/api/panel/products", new
        {
            name = "Raflı Ürün", defaultPrice = 10m, shelfLocation = "  A-3 / 2  ",
        });
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var dto = (await created.Content.ReadFromJsonAsync<ProductDto>())!;

        dto.ShelfLocation.Should().Be("A-3 / 2");

        var cleared = await client.PutAsJsonAsync($"/api/panel/products/{dto.Id}", new
        {
            name = "Raflı Ürün", defaultPrice = 10m, shelfLocation = "   ",
        });
        cleared.StatusCode.Should().Be(HttpStatusCode.OK);
        (await cleared.Content.ReadFromJsonAsync<ProductDto>())!
            .ShelfLocation.Should().BeNull();
    }

    [Fact]
    public async Task Shelf_location_longer_than_the_column_is_rejected()
    {
        var (client, _) = await SeedAsync();

        var resp = await client.PostAsJsonAsync("/api/panel/products", new
        {
            name = "Uzun Raf", defaultPrice = 10m,
            shelfLocation = new string('R', CatalogLimits.ShelfLocation + 1),
        });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await HasValidationErrorAsync(resp, "ShelfLocation")).Should().BeTrue();
    }

    private sealed record AxisValues(string Name, List<string> Values);

    [Fact]
    public async Task Axis_values_returns_distinct_values_used_under_the_same_axis_name()
    {
        var (client, _) = await SeedAsync();

        var tisort = await CreateProductAsync(client, "Tişört", "AV1",
            axis1Name: "Renk", axis1Role: 1, axis2Name: "Beden", axis2Role: 2);
        foreach (var (renk, beden) in new[] { ("Siyah", "M"), ("Beyaz", "L") })
            (await client.PostAsJsonAsync($"/api/panel/products/{tisort.Id}/variants",
                new { axis1Value = renk, axis2Value = beden, isActive = true }))
                .StatusCode.Should().Be(HttpStatusCode.Created);

        var gomlek = await CreateProductAsync(client, "Gömlek", "AV2",
            axis1Name: "Beden", axis1Role: 2);
        (await client.PostAsJsonAsync($"/api/panel/products/{gomlek.Id}/variants",
            new { axis1Value = "XL", isActive = true }))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        var resp = await client.GetFromJsonAsync<AxisValues>(
            "/api/panel/products/axis-values?name=Beden");

        resp!.Values.Should().BeEquivalentTo(new[] { "M", "L", "XL" },
            "eksen adı hangi slotta olursa olsun değerler tek listede toplanmalı");
    }

    [Fact]
    public async Task Axis_values_needs_a_name()
    {
        var (client, _) = await SeedAsync();

        var resp = await client.GetAsync("/api/panel/products/axis-values?name=  ");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await TitleAsync(resp)).Should().Be("missing-axis-name");
    }

    /// <summary>
    /// Öneriler kiracı sınırını aşmamalı. Eksen adları jenerik ("Beden"), yani
    /// iki lisansın aynı adı kullanması kural değil istisna değil — sızıntı
    /// olsaydı bir mağaza diğerinin beden/renk listesini görürdü.
    /// </summary>
    [Fact]
    public async Task Axis_values_never_leaks_another_tenants_values()
    {
        var (clientA, _) = await SeedAsync();
        var mine = await CreateProductAsync(clientA, "Tişört", "AV3",
            axis1Name: "Beden", axis1Role: 2);
        (await clientA.PostAsJsonAsync($"/api/panel/products/{mine.Id}/variants",
            new { axis1Value = "M", isActive = true }))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        var (clientB, _) = await SeedAsync();
        var theirs = await CreateProductAsync(clientB, "Gömlek", "AV4",
            axis1Name: "Beden", axis1Role: 2);
        (await clientB.PostAsJsonAsync($"/api/panel/products/{theirs.Id}/variants",
            new { axis1Value = "XXL", isActive = true }))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        var resp = await clientA.GetFromJsonAsync<AxisValues>(
            "/api/panel/products/axis-values?name=Beden");

        resp!.Values.Should().BeEquivalentTo(new[] { "M" });
    }
}
