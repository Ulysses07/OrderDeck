using System.Net;
using System.Net.Http.Json;
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

    /// <param name="withMovement">Varyanta bağlı bir stok hareketi yazılsın mı.</param>
    /// <param name="axisless">
    /// <c>true</c> ise kart eksensiz kurulur ve varyant, <c>BuildAutoVariant</c>
    /// ile aynı şekli alır: eksen değerleri boş, varyant kodu ürün kodunun aynısı.
    /// Eksen ekleme senaryosunun tam olarak bu şekle ihtiyacı var — değerli
    /// varyantta <c>axis-in-use</c> zaten devreye girer, stok kapısı hiç sınanmaz.
    /// </param>
    private async Task<Seed> SeedAsync(bool withMovement, bool axisless = false)
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
            Axis1Value = axisless ? null : "M",
            Axis1Code = axisless ? null : "M",
            VariantCode = axisless ? "A1" : "A1-M",
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

    /// <summary>
    /// Eksensiz karta eksen eklemek, otomatik varyantı SİLİP yerine değerli
    /// varyantlar açmak demek. O varyantın defteri varsa silme Restrict FK'sına
    /// çarpar (InMemory'de zorlanmaz, SQL Server'da 500) — kapı 409'la önce
    /// kapanmalı.
    /// </summary>
    private static Task<HttpResponseMessage> PutAddAxisAsync(Seed s)
        => s.Client.PutAsJsonAsync($"/api/panel/products/{s.ProductId}", new
        {
            name = "Tişört",
            code = "A1",
            categoryId = (Guid?)null,
            defaultPrice = 100m,
            cost = (decimal?)null,
            shelfLocation = (string?)null,
            axis1Name = "Beden",
            axis1Role = 2,
            axis2Name = (string?)null,
            axis2Role = (int?)null,
        });

    [Fact]
    public async Task Axisless_product_with_movements_cannot_gain_an_axis()
    {
        var s = await SeedAsync(withMovement: true, axisless: true);

        var resp = await PutAddAxisAsync(s);

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await TitleAsync(resp)).Should().Be("axis-in-use-stock");
    }

    [Fact]
    public async Task Axisless_product_without_movements_can_still_gain_an_axis()
    {
        var s = await SeedAsync(withMovement: false, axisless: true);

        var resp = await PutAddAxisAsync(s);

        resp.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NoContent);
    }
}
