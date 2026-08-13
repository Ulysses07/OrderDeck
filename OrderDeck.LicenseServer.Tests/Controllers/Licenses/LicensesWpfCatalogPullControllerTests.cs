using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
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
        int productCount, bool archiveFirst = false)
    {
        var (client, customerId, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);
        using var scope = _factory.Services.CreateScope();
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
                Axis1Value = "M", Axis1Code = "M", VariantCode = product.Code + "-M",
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
            });
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
}
