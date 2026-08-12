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
        Guid Id, string? Axis1Value, string? Axis1Code,
        string? Axis2Value, string? Axis2Code,
        string VariantCode, string? Barcode, bool IsActive);

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

    private async Task<HttpClient> SeedAsync()
    {
        var (client, customerId, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        db.Licenses.Add(new License
        {
            Id = Guid.NewGuid(), CustomerId = customerId,
            LicenseKey = "LDK-VARI-" + Guid.NewGuid().ToString("N"),
            SkuCode = "STD", ActivationSlots = 1,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
        });
        await db.SaveChangesAsync();
        return client;
    }

    private static async Task<ProductDto> CreateProductAsync(
        HttpClient client,
        string? axis1Name = "Renk", int? axis1Role = 2,
        string? axis2Name = null, int? axis2Role = null)
    {
        var resp = await client.PostAsJsonAsync("/api/panel/products", new
        {
            name = "Deneme ürünü", code = (string?)null, categoryId = (Guid?)null,
            defaultPrice = 100m, cost = (decimal?)null,
            axis1Name, axis1Role, axis2Name, axis2Role,
        });
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await resp.Content.ReadFromJsonAsync<ProductDto>())!;
    }

    private static Task<HttpResponseMessage> PostVariantAsync(
        HttpClient client, Guid productId,
        string? axis1Value = null, string? axis1Code = null,
        string? axis2Value = null, string? axis2Code = null)
        => client.PostAsJsonAsync($"/api/panel/products/{productId}/variants",
            new { axis1Value, axis1Code, axis2Value, axis2Code, isActive = true });

    [Fact]
    public async Task Create_derives_the_code_from_the_display_value()
    {
        var client = await SeedAsync();
        var product = await CreateProductAsync(client);

        var resp = await PostVariantAsync(client, product.Id, axis1Value: "Yeşil");

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var dto = (await resp.Content.ReadFromJsonAsync<VariantDto>())!;
        dto.Axis1Value.Should().Be("Yeşil");
        dto.Axis1Code.Should().Be("YESI");
        dto.VariantCode.Should().Be($"{product.Code}-YESI");
    }

    [Fact]
    public async Task Create_prefers_the_manually_supplied_code()
    {
        var client = await SeedAsync();
        var product = await CreateProductAsync(client);

        var resp = await PostVariantAsync(client, product.Id,
            axis1Value: "Yeşil", axis1Code: "yes");

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var dto = (await resp.Content.ReadFromJsonAsync<VariantDto>())!;
        dto.Axis1Code.Should().Be("YES");
        dto.VariantCode.Should().Be($"{product.Code}-YES");
    }

    [Fact]
    public async Task Create_builds_a_two_segment_code_for_a_two_axis_product()
    {
        var client = await SeedAsync();
        var product = await CreateProductAsync(client,
            axis1Name: "Renk", axis1Role: 2, axis2Name: "Beden", axis2Role: 1);

        var resp = await PostVariantAsync(client, product.Id,
            axis1Value: "Siyah", axis2Value: "38");

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var dto = (await resp.Content.ReadFromJsonAsync<VariantDto>())!;
        dto.Axis2Code.Should().Be("38");
        dto.VariantCode.Should().Be($"{product.Code}-SIYA-38");
    }

    [Fact]
    public async Task Create_404_for_another_tenants_product()
    {
        var clientA = await SeedAsync();
        var product = await CreateProductAsync(clientA);
        var clientB = await SeedAsync();

        var resp = await PostVariantAsync(clientB, product.Id, axis1Value: "Siyah");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Create_400_when_the_product_has_no_axis()
    {
        var client = await SeedAsync();
        var product = await CreateProductAsync(client, axis1Name: null, axis1Role: null);

        var resp = await PostVariantAsync(client, product.Id, axis1Value: "Siyah");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await TitleAsync(resp)).Should().Be("product-has-no-axis");
    }

    [Fact]
    public async Task Create_400_when_the_first_axis_value_is_missing()
    {
        var client = await SeedAsync();
        var product = await CreateProductAsync(client);

        var resp = await PostVariantAsync(client, product.Id, axis1Value: "  ");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await TitleAsync(resp)).Should().Be("missing-axis-value");
    }

    [Fact]
    public async Task Create_400_when_the_second_axis_value_is_missing()
    {
        var client = await SeedAsync();
        var product = await CreateProductAsync(client,
            axis1Name: "Renk", axis1Role: 2, axis2Name: "Beden", axis2Role: 1);

        var resp = await PostVariantAsync(client, product.Id, axis1Value: "Siyah");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await TitleAsync(resp)).Should().Be("missing-axis-value");
    }

    [Fact]
    public async Task Create_400_when_a_second_value_is_sent_to_a_single_axis_product()
    {
        var client = await SeedAsync();
        var product = await CreateProductAsync(client);

        var resp = await PostVariantAsync(client, product.Id,
            axis1Value: "Siyah", axis2Value: "38");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await TitleAsync(resp)).Should().Be("unexpected-axis-value");
    }

    [Fact]
    public async Task Create_400_when_no_ascii_code_can_be_derived()
    {
        var client = await SeedAsync();
        var product = await CreateProductAsync(client);

        var resp = await PostVariantAsync(client, product.Id, axis1Value: "•••");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await TitleAsync(resp)).Should().Be("invalid-axis-code");
    }

    [Fact]
    public async Task Create_409_on_a_duplicate_variant_code()
    {
        var client = await SeedAsync();
        var product = await CreateProductAsync(client);
        (await PostVariantAsync(client, product.Id, axis1Value: "Siyah"))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        var resp = await PostVariantAsync(client, product.Id, axis1Value: "Siyahımsı");

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await TitleAsync(resp)).Should().Be("duplicate-variant");
    }

    /// <summary>
    /// Ürün kodu değişince eski varyant kodu bayatlarsa, aynı eksen değeri
    /// ikinci kez eklendiğinde çakışma yakalanamaz ve tek üründe iki özdeş
    /// Axis1Value oluşurdu. Kod türetildiği için çakışma yakalanmalı.
    /// </summary>
    [Fact]
    public async Task Create_409_when_the_same_value_is_re_added_after_a_product_code_change()
    {
        var client = await SeedAsync();
        var product = await CreateProductAsync(client);
        (await PostVariantAsync(client, product.Id, axis1Value: "Siyah"))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        (await client.PutAsJsonAsync($"/api/panel/products/{product.Id}", new
        {
            name = product.Name, code = "D3", categoryId = (Guid?)null,
            defaultPrice = product.DefaultPrice, cost = (decimal?)null,
            axis1Name = "Renk", axis1Role = 2,
            axis2Name = (string?)null, axis2Role = (int?)null,
        })).StatusCode.Should().Be(HttpStatusCode.OK);

        var resp = await PostVariantAsync(client, product.Id, axis1Value: "Siyah");

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await TitleAsync(resp)).Should().Be("duplicate-variant");

        var after = await client.GetFromJsonAsync<ProductDto>($"/api/panel/products/{product.Id}");
        after!.Variants.Should().ContainSingle();
        after.Variants[0].VariantCode.Should().Be("D3-SIYA");
    }

    [Fact]
    public async Task Update_recomputes_the_variant_code()
    {
        var client = await SeedAsync();
        var product = await CreateProductAsync(client);
        var created = (await (await PostVariantAsync(client, product.Id, axis1Value: "Siyah"))
            .Content.ReadFromJsonAsync<VariantDto>())!;

        var resp = await client.PutAsJsonAsync(
            $"/api/panel/products/{product.Id}/variants/{created.Id}",
            new
            {
                axis1Value = "Beyaz", axis1Code = (string?)null,
                axis2Value = (string?)null, axis2Code = (string?)null,
                isActive = false,
            });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = (await resp.Content.ReadFromJsonAsync<VariantDto>())!;
        dto.Axis1Code.Should().Be("BEYA");
        dto.VariantCode.Should().Be($"{product.Code}-BEYA");
        dto.IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task Update_409_when_it_collides_with_a_sibling()
    {
        var client = await SeedAsync();
        var product = await CreateProductAsync(client);
        await PostVariantAsync(client, product.Id, axis1Value: "Siyah");
        var beyaz = (await (await PostVariantAsync(client, product.Id, axis1Value: "Beyaz"))
            .Content.ReadFromJsonAsync<VariantDto>())!;

        var resp = await client.PutAsJsonAsync(
            $"/api/panel/products/{product.Id}/variants/{beyaz.Id}",
            new
            {
                axis1Value = "Siyah", axis1Code = (string?)null,
                axis2Value = (string?)null, axis2Code = (string?)null,
                isActive = true,
            });

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await TitleAsync(resp)).Should().Be("duplicate-variant");
    }

    [Fact]
    public async Task Delete_removes_the_variant()
    {
        var client = await SeedAsync();
        var product = await CreateProductAsync(client);
        var created = (await (await PostVariantAsync(client, product.Id, axis1Value: "Siyah"))
            .Content.ReadFromJsonAsync<VariantDto>())!;

        var resp = await client.DeleteAsync(
            $"/api/panel/products/{product.Id}/variants/{created.Id}");

        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var after = await client.GetFromJsonAsync<ProductDto>($"/api/panel/products/{product.Id}");
        after!.Variants.Should().BeEmpty();
    }
}
