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

    [Fact]
    public async Task Next_code_endpoint_returns_the_next_free_code()
    {
        var (client, _) = await SeedAsync();
        await CreateProductAsync(client, "Birinci");

        var dto = await client.GetFromJsonAsync<NextCodeDto>("/api/panel/products/next-code");

        dto!.Code.Should().Be("A2");
    }
}
