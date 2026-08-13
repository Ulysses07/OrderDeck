using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Panel;

public class StockDeleteGuardTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public StockDeleteGuardTests(ApiFactory factory) => _factory = factory;

    private sealed record Seed(HttpClient Client, Guid ProductId, Guid VariantId);

    private async Task<Seed> SeedAsync(bool withMovement)
    {
        var (client, customerId, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();

        var license = new License
        {
            Id = Guid.NewGuid(), CustomerId = customerId,
            LicenseKey = "LDK-GARD-" + Guid.NewGuid().ToString("N"),
            SkuCode = "STD", ActivationSlots = 1,
            IssuedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddDays(30)
        };
        db.Licenses.Add(license);

        var product = new Product
        {
            Id = Guid.NewGuid(), LicenseId = license.Id,
            Code = "A1", Name = "Tişört", DefaultPrice = 100m,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        };
        db.Products.Add(product);

        var variant = new ProductVariant
        {
            Id = Guid.NewGuid(), LicenseId = license.Id, ProductId = product.Id,
            Axis1Value = "M", Axis1Code = "M", VariantCode = "A1-M",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        };
        db.ProductVariants.Add(variant);

        if (withMovement)
        {
            db.StockMovements.Add(new StockMovement
            {
                Id = Guid.NewGuid(), LicenseId = license.Id,
                ProductId = product.Id, ProductVariantId = variant.Id,
                Quantity = 5, Reason = StockMovementReason.Entry,
                OccurredAt = DateTimeOffset.UtcNow, CreatedAt = DateTimeOffset.UtcNow
            });
        }

        await db.SaveChangesAsync();
        return new Seed(client, product.Id, variant.Id);
    }

    private static async Task<string?> TitleAsync(HttpResponseMessage resp)
    {
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.TryGetProperty("title", out var t) ? t.GetString() : null;
    }

    [Fact]
    public async Task Product_with_movements_cannot_be_deleted()
    {
        var s = await SeedAsync(withMovement: true);

        var resp = await s.Client.DeleteAsync($"/api/panel/products/{s.ProductId}");

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await TitleAsync(resp)).Should().Be("product-has-stock-movements");
    }

    [Fact]
    public async Task Variant_with_movements_cannot_be_deleted()
    {
        var s = await SeedAsync(withMovement: true);

        var resp = await s.Client.DeleteAsync(
            $"/api/panel/products/{s.ProductId}/variants/{s.VariantId}");

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await TitleAsync(resp)).Should().Be("variant-has-stock-movements");
    }

    [Fact]
    public async Task Product_without_movements_is_still_deletable()
    {
        var s = await SeedAsync(withMovement: false);

        var resp = await s.Client.DeleteAsync($"/api/panel/products/{s.ProductId}");

        resp.StatusCode.Should().BeOneOf(HttpStatusCode.NoContent, HttpStatusCode.OK);
    }
}
