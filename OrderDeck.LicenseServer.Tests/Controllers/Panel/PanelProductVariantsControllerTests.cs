using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Panel;

public class PanelProductVariantsControllerTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public PanelProductVariantsControllerTests(ApiFactory f) => _factory = f;

    private sealed record VariantDto(
        Guid Id, string? Axis1Value, string? Axis2Value,
        string? Barcode, bool IsActive);

    private sealed record ProductDto(
        Guid Id, Guid? CategoryId, string Code, string Name,
        decimal DefaultPrice, decimal? Cost,
        string? Axis1Name, int? Axis1Role,
        string? Axis2Name, int? Axis2Role,
        bool IsArchived, List<VariantDto> Variants);

    private static async Task<string?> TitleAsync(HttpResponseMessage resp)
    {
        var json = await resp.Content.ReadAsStringAsync();
        if (string.IsNullOrWhiteSpace(json)) return null;
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("title", out var t) ? t.GetString() : null;
    }

    private static async Task<string?> DetailAsync(HttpResponseMessage resp)
    {
        var json = await resp.Content.ReadAsStringAsync();
        if (string.IsNullOrWhiteSpace(json)) return null;
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("detail", out var d) ? d.GetString() : null;
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

    private async Task<(HttpClient Client, Guid licenseId)> SeedAsync()
    {
        var (client, customerId, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var license = new License
        {
            Id = Guid.NewGuid(), CustomerId = customerId,
            LicenseKey = "LDK-VARI-" + Guid.NewGuid().ToString("N"),
            SkuCode = "STD", ActivationSlots = 1,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
        };
        db.Licenses.Add(license);
        await db.SaveChangesAsync();
        return (client, license.Id);
    }

    private static async Task<ProductDto> CreateProductAsync(
        HttpClient client,
        string name = "Deneme ürünü",
        string? axis1Name = "Renk", int? axis1Role = 2,
        string? axis2Name = null, int? axis2Role = null)
    {
        var resp = await client.PostAsJsonAsync("/api/panel/products", new
        {
            name, categoryId = (Guid?)null,
            defaultPrice = 100m, cost = (decimal?)null,
            axis1Name, axis1Role, axis2Name, axis2Role,
        });
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await resp.Content.ReadFromJsonAsync<ProductDto>())!;
    }

    private static Task<HttpResponseMessage> PostVariantAsync(
        HttpClient client, Guid productId,
        string? axis1Value = null, string? axis2Value = null)
        => client.PostAsJsonAsync($"/api/panel/products/{productId}/variants",
            new { axis1Value, axis2Value, isActive = true });

    /// <summary>
    /// Varyantın kodu yok: kullanıcıya görünen hâli eksen değerinin ta kendisi.
    /// Değer <b>olduğu gibi</b> saklanmalı (Türkçe harfler dahil); ASCII'ye
    /// kısaltma yapan eski kod kavramı kalktı.
    /// </summary>
    [Fact]
    public async Task Create_stores_the_display_value_as_is()
    {
        var (client, _) = await SeedAsync();
        var product = await CreateProductAsync(client);

        var resp = await PostVariantAsync(client, product.Id, axis1Value: "Yeşil");

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var dto = (await resp.Content.ReadFromJsonAsync<VariantDto>())!;
        dto.Axis1Value.Should().Be("Yeşil");
        dto.Axis2Value.Should().BeNull();
    }

    [Fact]
    public async Task Create_stores_both_values_for_a_two_axis_product()
    {
        var (client, _) = await SeedAsync();
        var product = await CreateProductAsync(client,
            axis1Name: "Renk", axis1Role: 2, axis2Name: "Beden", axis2Role: 1);

        var resp = await PostVariantAsync(client, product.Id,
            axis1Value: "Siyah", axis2Value: "38");

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var dto = (await resp.Content.ReadFromJsonAsync<VariantDto>())!;
        dto.Axis1Value.Should().Be("Siyah");
        dto.Axis2Value.Should().Be("38");
    }

    [Fact]
    public async Task Create_404_for_another_tenants_product()
    {
        var (clientA, _) = await SeedAsync();
        var product = await CreateProductAsync(clientA);
        var (clientB, _) = await SeedAsync();

        var resp = await PostVariantAsync(clientB, product.Id, axis1Value: "Siyah");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Create_400_when_the_product_has_no_axis()
    {
        var (client, _) = await SeedAsync();
        var product = await CreateProductAsync(client, axis1Name: null, axis1Role: null);

        var resp = await PostVariantAsync(client, product.Id, axis1Value: "Siyah");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await TitleAsync(resp)).Should().Be("product-has-no-axis");
    }

    [Fact]
    public async Task Create_400_when_the_first_axis_value_is_missing()
    {
        var (client, _) = await SeedAsync();
        var product = await CreateProductAsync(client);

        var resp = await PostVariantAsync(client, product.Id, axis1Value: "  ");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await TitleAsync(resp)).Should().Be("missing-axis-value");
    }

    [Fact]
    public async Task Create_400_when_the_second_axis_value_is_missing()
    {
        var (client, _) = await SeedAsync();
        var product = await CreateProductAsync(client,
            axis1Name: "Renk", axis1Role: 2, axis2Name: "Beden", axis2Role: 1);

        var resp = await PostVariantAsync(client, product.Id, axis1Value: "Siyah");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await TitleAsync(resp)).Should().Be("missing-axis-value");
    }

    [Fact]
    public async Task Create_400_when_a_second_value_is_sent_to_a_single_axis_product()
    {
        var (client, _) = await SeedAsync();
        var product = await CreateProductAsync(client);

        var resp = await PostVariantAsync(client, product.Id,
            axis1Value: "Siyah", axis2Value: "38");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await TitleAsync(resp)).Should().Be("unexpected-axis-value");
    }

    /// <summary>
    /// Görünen eksen değeri serbest metin ama kolon <c>nvarchar(60)</c>;
    /// sınır DTO'da duyurulmazsa taşan girdi prod'da 500 oluyor.
    /// </summary>
    [Theory]
    [InlineData("Axis1Value")]
    [InlineData("Axis2Value")]
    public async Task Create_400_when_an_axis_value_exceeds_the_column_limit(string field)
    {
        var (client, _) = await SeedAsync();
        var product = await CreateProductAsync(client,
            axis1Name: "Renk", axis1Role: 2, axis2Name: "Beden", axis2Role: 1);

        var resp = await PostVariantAsync(client, product.Id,
            axis1Value: field == "Axis1Value"
                ? new string('A', CatalogLimits.AxisValue + 1) : "Siyah",
            axis2Value: field == "Axis2Value"
                ? new string('B', CatalogLimits.AxisValue + 1) : "M");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await HasValidationErrorAsync(resp, field)).Should().BeTrue();
    }

    /// <summary>
    /// Farklı yazım gerçek tekrardır: baştaki/sondaki boşluk, harf büyüklüğü ve
    /// Türkçe harf katlaması normalleştiriciden geçiyor — "  kirmizi  " ile
    /// "Kırmızı" aynı varyant. Mesaj da kodu değil DEĞERİ anmalı; kullanıcı
    /// kartta kodu değil değeri görüyor.
    /// </summary>
    [Fact]
    public async Task Same_axis_value_in_different_casing_is_a_duplicate()
    {
        var (client, _) = await SeedAsync();
        var product = await CreateProductAsync(client);
        (await PostVariantAsync(client, product.Id, axis1Value: "Kırmızı"))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        var resp = await PostVariantAsync(client, product.Id, axis1Value: "  kirmizi  ");

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await TitleAsync(resp)).Should().Be("duplicate-variant");
        (await DetailAsync(resp)).Should().Contain("kirmizi");
    }

    /// <summary>
    /// Eskiden bu iki değer aynı türetilmiş koda (KIRM) düşüp yapay bir 409
    /// üretiyordu. Benzersizlik normalize DEĞERE taşındığından ikisi de
    /// meşru ve ayrı varyant.
    /// </summary>
    [Fact]
    public async Task Similar_axis_values_are_separate_variants()
    {
        var (client, _) = await SeedAsync();
        var product = await CreateProductAsync(client);
        (await PostVariantAsync(client, product.Id, axis1Value: "Kırmızı"))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        var resp = await PostVariantAsync(client, product.Id, axis1Value: "Kırmızılı");

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var after = await client.GetFromJsonAsync<ProductDto>($"/api/panel/products/{product.Id}");
        after!.Variants.Select(v => v.Axis1Value).Should().BeEquivalentTo(
            new[] { "Kırmızı", "Kırmızılı" });
    }

    /// <summary>İkinci eksende de aynı kural: benzer yazım ayrı varyanttır.</summary>
    [Fact]
    public async Task Similar_second_axis_values_are_separate_variants()
    {
        var (client, _) = await SeedAsync();
        var product = await CreateProductAsync(client,
            axis1Name: "Renk", axis1Role: 2, axis2Name: "Beden", axis2Role: 1);
        (await PostVariantAsync(client, product.Id, axis1Value: "Siyah", axis2Value: "Küçük"))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        var resp = await PostVariantAsync(client, product.Id,
            axis1Value: "Siyah", axis2Value: "Küçükçe");

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    /// <summary>İki eksenli üründe gerçek tekrar da doğru slug'ı almalı.</summary>
    [Fact]
    public async Task Create_409_duplicate_variant_on_a_two_axis_product()
    {
        var (client, _) = await SeedAsync();
        var product = await CreateProductAsync(client,
            axis1Name: "Renk", axis1Role: 2, axis2Name: "Beden", axis2Role: 1);
        (await PostVariantAsync(client, product.Id, axis1Value: "Siyah", axis2Value: "Küçük"))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        var resp = await PostVariantAsync(client, product.Id,
            axis1Value: "siyah", axis2Value: "küçük");

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await TitleAsync(resp)).Should().Be("duplicate-variant");
    }

    /// <summary>
    /// Ürün kartı güncellendikten sonra da aynı eksen değeri iki kez eklenemez:
    /// benzersizlik ürüne değil, varyantın normalize eksen değerlerine bağlı.
    /// </summary>
    [Fact]
    public async Task Create_409_when_the_same_axis_value_is_re_added_after_a_product_update()
    {
        var (client, _) = await SeedAsync();
        var product = await CreateProductAsync(client);
        (await PostVariantAsync(client, product.Id, axis1Value: "Siyah"))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        // Ürün kodu sistem tarafından üretilir ve değişmez; adı güncelleyelim.
        (await client.PutAsJsonAsync($"/api/panel/products/{product.Id}", new
        {
            name = "Yeni ad", categoryId = (Guid?)null,
            defaultPrice = product.DefaultPrice, cost = (decimal?)null,
            axis1Name = "Renk", axis1Role = 2,
            axis2Name = (string?)null, axis2Role = (int?)null,
        })).StatusCode.Should().Be(HttpStatusCode.OK);

        // Güncelleme sonrası aynı eksen değeri eklenemez — çakışma yakalanmalı.
        var resp = await PostVariantAsync(client, product.Id, axis1Value: "Siyah");

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await TitleAsync(resp)).Should().Be("duplicate-variant");

        var after = await client.GetFromJsonAsync<ProductDto>($"/api/panel/products/{product.Id}");
        after!.Variants.Should().ContainSingle();
        after.Variants[0].Axis1Value.Should().Be("Siyah");
    }

    [Fact]
    public async Task Update_changes_the_axis_value()
    {
        var (client, _) = await SeedAsync();
        var product = await CreateProductAsync(client);
        var created = (await (await PostVariantAsync(client, product.Id, axis1Value: "Siyah"))
            .Content.ReadFromJsonAsync<VariantDto>())!;

        var resp = await client.PutAsJsonAsync(
            $"/api/panel/products/{product.Id}/variants/{created.Id}",
            new
            {
                axis1Value = "Beyaz", axis2Value = (string?)null,
                isActive = false,
            });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = (await resp.Content.ReadFromJsonAsync<VariantDto>())!;
        dto.Axis1Value.Should().Be("Beyaz");
        dto.IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task Update_409_when_it_collides_with_a_sibling()
    {
        var (client, _) = await SeedAsync();
        var product = await CreateProductAsync(client);
        await PostVariantAsync(client, product.Id, axis1Value: "Siyah");
        var beyaz = (await (await PostVariantAsync(client, product.Id, axis1Value: "Beyaz"))
            .Content.ReadFromJsonAsync<VariantDto>())!;

        var resp = await client.PutAsJsonAsync(
            $"/api/panel/products/{product.Id}/variants/{beyaz.Id}",
            new
            {
                axis1Value = "Siyah", axis2Value = (string?)null,
                isActive = true,
            });

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await TitleAsync(resp)).Should().Be("duplicate-variant");
    }

    /// <summary>
    /// Güncelleme yolu da yeni gerçeğe uymalı: kardeşiyle aynı türetilmiş koda
    /// düşen ama DEĞERİ farklı olan satır artık çakışma değil.
    /// </summary>
    [Fact]
    public async Task Update_200_when_the_new_value_only_resembles_a_sibling()
    {
        var (client, _) = await SeedAsync();
        var product = await CreateProductAsync(client);
        await PostVariantAsync(client, product.Id, axis1Value: "Kırmızı");
        var mavi = (await (await PostVariantAsync(client, product.Id, axis1Value: "Mavi"))
            .Content.ReadFromJsonAsync<VariantDto>())!;

        var resp = await client.PutAsJsonAsync(
            $"/api/panel/products/{product.Id}/variants/{mavi.Id}",
            new
            {
                axis1Value = "Kırmızılı", axis2Value = (string?)null,
                isActive = true,
            });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await resp.Content.ReadFromJsonAsync<VariantDto>())!
            .Axis1Value.Should().Be("Kırmızılı");
    }

    /// <summary>
    /// Satır kendi değerleriyle kaydedilince kendi kendiyle çakışmamalı —
    /// yoksa sadece <c>IsActive</c>'i değiştiren kaydetme 409 yerdi.
    /// </summary>
    [Fact]
    public async Task Update_200_when_the_row_is_saved_with_its_own_values()
    {
        var (client, _) = await SeedAsync();
        var product = await CreateProductAsync(client);
        var created = (await (await PostVariantAsync(client, product.Id, axis1Value: "Kırmızı"))
            .Content.ReadFromJsonAsync<VariantDto>())!;

        var resp = await client.PutAsJsonAsync(
            $"/api/panel/products/{product.Id}/variants/{created.Id}",
            new
            {
                axis1Value = "Kırmızı", axis2Value = (string?)null,
                isActive = false,
            });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// Kiracı sınırı oluşturmada olduğu gibi GÜNCELLEMEDE de geçerli. Kural 404
    /// (403 değil): başka kiracının ürünü bizim için var olmamalı.
    /// </summary>
    [Fact]
    public async Task Update_404_for_another_tenants_product()
    {
        var (clientA, _) = await SeedAsync();
        var product = await CreateProductAsync(clientA);
        var variant = (await (await PostVariantAsync(clientA, product.Id, axis1Value: "Siyah"))
            .Content.ReadFromJsonAsync<VariantDto>())!;
        var (clientB, _) = await SeedAsync();

        var resp = await clientB.PutAsJsonAsync(
            $"/api/panel/products/{product.Id}/variants/{variant.Id}",
            new
            {
                axis1Value = "Beyaz", axis2Value = (string?)null,
                isActive = false,
            });

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);

        // 404 dönüp yine de yazmış olmak yalnız duruma bakan bir testten kaçar.
        var after = await clientA.GetFromJsonAsync<ProductDto>(
            $"/api/panel/products/{product.Id}");
        after!.Variants.Single().Axis1Value.Should().Be("Siyah");
        after.Variants.Single().IsActive.Should().BeTrue();
    }

    /// <summary>
    /// Silmede 404 tek başına yetmez: satır sahibinin gözünden HÂLÂ DURUYOR
    /// olmalı. 404 dönüp yine de silen bir uç, yalnız durum kodu kontrol eden
    /// bir testin gözünden kaçar.
    /// </summary>
    [Fact]
    public async Task Delete_404_for_another_tenants_product()
    {
        var (clientA, _) = await SeedAsync();
        var product = await CreateProductAsync(clientA);
        var variant = (await (await PostVariantAsync(clientA, product.Id, axis1Value: "Siyah"))
            .Content.ReadFromJsonAsync<VariantDto>())!;
        var (clientB, _) = await SeedAsync();

        var resp = await clientB.DeleteAsync(
            $"/api/panel/products/{product.Id}/variants/{variant.Id}");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var after = await clientA.GetFromJsonAsync<ProductDto>(
            $"/api/panel/products/{product.Id}");
        after!.Variants.Should().ContainSingle(v => v.Id == variant.Id);
    }

    [Fact]
    public async Task Delete_removes_the_variant()
    {
        var (client, _) = await SeedAsync();
        var product = await CreateProductAsync(client);
        var created = (await (await PostVariantAsync(client, product.Id, axis1Value: "Siyah"))
            .Content.ReadFromJsonAsync<VariantDto>())!;

        var resp = await client.DeleteAsync(
            $"/api/panel/products/{product.Id}/variants/{created.Id}");

        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var after = await client.GetFromJsonAsync<ProductDto>($"/api/panel/products/{product.Id}");
        after!.Variants.Should().BeEmpty();
    }

    [Fact]
    public async Task Bulk_writes_every_row_in_one_go()
    {
        var (client, _) = await SeedAsync();
        var product = await CreateProductAsync(client, "Tişört",
            axis1Name: "Renk", axis1Role: 1, axis2Name: "Beden", axis2Role: 2);

        var resp = await client.PostAsJsonAsync(
            $"/api/panel/products/{product.Id}/variants/bulk",
            new
            {
                items = new[]
                {
                    new { axis1Value = "Siyah", axis2Value = "M", isActive = true },
                    new { axis1Value = "Siyah", axis2Value = "L", isActive = true },
                    new { axis1Value = "Beyaz", axis2Value = "M", isActive = true },
                },
            });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<BulkResult>();
        body!.Variants.Should().HaveCount(3);
        body.Variants.Select(v => $"{v.Axis1Value}/{v.Axis2Value}").Should()
            .BeEquivalentTo(new[] { "Siyah/M", "Siyah/L", "Beyaz/M" });
    }

    /// <summary>
    /// Kritik güvence: parti bölünmez. Ortadaki tek geçersiz satır bile yazımı
    /// tamamen iptal etmeli — yoksa kullanıcı yarım kurulmuş ürünle kalır ve
    /// tekrar denediğinde ilk yazılanlar çakışır.
    /// </summary>
    [Fact]
    public async Task Bulk_writes_zero_rows_when_any_item_is_invalid()
    {
        var (client, _) = await SeedAsync();
        var product = await CreateProductAsync(client, "Tişört",
            axis1Name: "Renk", axis1Role: 1, axis2Name: "Beden", axis2Role: 2);

        var resp = await client.PostAsJsonAsync(
            $"/api/panel/products/{product.Id}/variants/bulk",
            new
            {
                items = new[]
                {
                    new { axis1Value = "Siyah", axis2Value = "M", isActive = true },
                    new { axis1Value = "Beyaz", axis2Value = (string?)null, isActive = true },
                    new { axis1Value = "Mavi", axis2Value = "L", isActive = true },
                },
            });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await TitleAsync(resp)).Should().Be("missing-axis-value");

        var after = await client.GetFromJsonAsync<ProductDto>(
            $"/api/panel/products/{product.Id}");
        after!.Variants.Should().BeEmpty("geçersiz parti hiç satır bırakmamalı");
    }

    [Fact]
    public async Task Bulk_rejects_a_duplicate_inside_the_batch()
    {
        var (client, _) = await SeedAsync();
        var product = await CreateProductAsync(client, "Tişört",
            axis1Name: "Renk", axis1Role: 1);

        var resp = await client.PostAsJsonAsync(
            $"/api/panel/products/{product.Id}/variants/bulk",
            new
            {
                items = new[]
                {
                    new { axis1Value = "Siyah", isActive = true },
                    new { axis1Value = "siyah", isActive = true },
                },
            });

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await TitleAsync(resp)).Should().Be("duplicate-in-batch");
    }

    [Fact]
    public async Task Bulk_rejects_a_row_that_already_exists()
    {
        var (client, _) = await SeedAsync();
        var product = await CreateProductAsync(client, "Tişört",
            axis1Name: "Renk", axis1Role: 1);

        (await client.PostAsJsonAsync($"/api/panel/products/{product.Id}/variants",
            new { axis1Value = "Siyah", isActive = true }))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        var resp = await client.PostAsJsonAsync(
            $"/api/panel/products/{product.Id}/variants/bulk",
            new { items = new[] { new { axis1Value = "Siyah", isActive = true } } });

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await TitleAsync(resp)).Should().Be("duplicate-variant");
    }

    private sealed record BulkResult(List<VariantDto> Variants);
}
