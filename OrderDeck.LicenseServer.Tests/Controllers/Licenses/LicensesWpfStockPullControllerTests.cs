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

public class LicensesWpfStockPullControllerTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public LicensesWpfStockPullControllerTests(ApiFactory factory) => _factory = factory;

    private static readonly DateTimeOffset Stamp =
        new(2026, 8, 13, 10, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Bir ürün + <paramref name="variantCount"/> varyant açar ve her varyanta
    /// <paramref name="movementsPerVariant"/> adet −1 hareketi yazar. Hareketlerin
    /// HEPSİ aynı <c>CreatedAt</c> damgasını taşır — tek senkron paketinin
    /// gerçek davranışı budur ve eşitlik tuzağını ancak böyle sınayabiliriz.
    /// </summary>
    private async Task<(HttpClient Client, Guid LicenseId, Guid ProductId, List<Guid> VariantIds)>
        SeedAsync(int variantCount, int movementsPerVariant)
    {
        var (client, customerId, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();

        var license = new License
        {
            Id = Guid.NewGuid(), CustomerId = customerId,
            LicenseKey = "LDK-STKP-" + Guid.NewGuid().ToString("N"),
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

        var variantIds = new List<Guid>();
        for (var v = 0; v < variantCount; v++)
        {
            var variant = new ProductVariant
            {
                Id = Guid.NewGuid(), LicenseId = license.Id, ProductId = product.Id,
                Axis1Value = $"B{v}", Axis1Code = $"B{v}",
                VariantCode = $"A1-B{v}", IsActive = true,
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
            };
            db.ProductVariants.Add(variant);
            variantIds.Add(variant.Id);

            for (var i = 0; i < movementsPerVariant; i++)
            {
                db.StockMovements.Add(new StockMovement
                {
                    Id = Guid.NewGuid(),
                    LicenseId = license.Id,
                    ProductId = product.Id,
                    ProductVariantId = variant.Id,
                    Quantity = -1,
                    Reason = StockMovementReason.Sale,
                    OccurredAt = Stamp.AddHours(-6),
                    CreatedAt = Stamp,
                });
            }
        }

        await db.SaveChangesAsync();
        return (client, license.Id, product.Id, variantIds);
    }

    private static string Url(Guid licenseId, DateTimeOffset since, Guid sinceId, int take)
        => $"/api/v1/licenses/{licenseId}/stock/balances/since"
           + $"?since={Uri.EscapeDataString(since.ToString("O"))}&sinceId={sinceId}&take={take}";

    private async Task<JsonElement> GetPageAsync(
        HttpClient client, Guid licenseId, DateTimeOffset since, Guid sinceId, int take)
        => await client.GetFromJsonAsync<JsonElement>(Url(licenseId, since, sinceId, take));

    [Fact]
    public async Task Returns_recomputed_balances_for_keys_touched_after_the_cursor()
    {
        var (client, licenseId, productId, variantIds) = await SeedAsync(1, 1);

        var page = await GetPageAsync(client, licenseId, Stamp.AddMinutes(-1), Guid.Empty, 100);

        var balances = page.GetProperty("balances").EnumerateArray().ToList();
        balances.Should().HaveCount(1);
        balances[0].GetProperty("productId").GetGuid().Should().Be(productId);
        balances[0].GetProperty("productVariantId").GetGuid().Should().Be(variantIds[0]);
        balances[0].GetProperty("quantity").GetInt32().Should().Be(-1);
        page.GetProperty("hasMore").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Quantity_is_the_absolute_balance_not_the_paged_delta()
    {
        // Tek anahtarda iki hareket, sayfa boyu 1: sayfa anahtarın hareketlerini
        // ortasından kesiyor. Dönen sayı yine de TAM bakiye (−2) olmalı.
        var (client, licenseId, _, _) = await SeedAsync(1, 2);

        var page = await GetPageAsync(client, licenseId, Stamp.AddMinutes(-1), Guid.Empty, 1);

        var balances = page.GetProperty("balances").EnumerateArray().ToList();
        balances.Should().HaveCount(1);
        balances[0].GetProperty("quantity").GetInt32().Should().Be(-2);
        page.GetProperty("hasMore").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Paging_never_loses_keys_that_share_a_created_at()
    {
        var (client, licenseId, _, variantIds) = await SeedAsync(5, 1);

        var seen = new HashSet<Guid>();
        var since = Stamp.AddMinutes(-1);
        var sinceId = Guid.Empty;

        while (true)
        {
            var page = await GetPageAsync(client, licenseId, since, sinceId, 2);
            var balances = page.GetProperty("balances").EnumerateArray().ToList();
            if (balances.Count == 0) break;

            foreach (var b in balances)
                seen.Add(b.GetProperty("productVariantId").GetGuid());

            since = page.GetProperty("cursorCreatedAt").GetDateTimeOffset();
            sinceId = page.GetProperty("cursorId").GetGuid();
        }

        seen.Should().BeEquivalentTo(variantIds);
    }

    [Fact]
    public async Task Cursor_at_the_end_returns_nothing_and_preserves_the_cursor()
    {
        var (client, licenseId, _, _) = await SeedAsync(2, 1);

        var first = await GetPageAsync(client, licenseId, Stamp.AddMinutes(-1), Guid.Empty, 100);
        var cursorCreatedAt = first.GetProperty("cursorCreatedAt").GetDateTimeOffset();
        var cursorId = first.GetProperty("cursorId").GetGuid();

        var next = await GetPageAsync(client, licenseId, cursorCreatedAt, cursorId, 100);

        next.GetProperty("balances").GetArrayLength().Should().Be(0);
        next.GetProperty("hasMore").GetBoolean().Should().BeFalse();
        // İmleç geri sarmamalı: boş sayfa istemcinin imlecini olduğu gibi döndürür.
        next.GetProperty("cursorCreatedAt").GetDateTimeOffset().Should().Be(cursorCreatedAt);
        next.GetProperty("cursorId").GetGuid().Should().Be(cursorId);
    }

    [Fact]
    public async Task Movements_newer_than_the_stability_horizon_are_not_returned()
    {
        // Ufkun altındaki varyant: −1, Stamp damgalı.
        var (client, licenseId, productId, variantIds) = await SeedAsync(1, 1);

        // Aynı ürüne ufkun ÜSTÜNDE (şu an damgalı) bir hareket ekle. Uçuşta olan
        // bir işlemin bu damganın gerisine yazma ihtimali sürdüğü için uç bu
        // satırı henüz okumamalı.
        Guid freshVariantId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var fresh = new ProductVariant
            {
                Id = Guid.NewGuid(), LicenseId = licenseId, ProductId = productId,
                Axis1Value = "B9", Axis1Code = "B9",
                VariantCode = "A1-B9", IsActive = true,
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
            };
            db.ProductVariants.Add(fresh);
            freshVariantId = fresh.Id;

            db.StockMovements.Add(new StockMovement
            {
                Id = Guid.NewGuid(),
                LicenseId = licenseId,
                ProductId = productId,
                ProductVariantId = fresh.Id,
                Quantity = -5,
                Reason = StockMovementReason.Sale,
                OccurredAt = DateTimeOffset.UtcNow,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var page = await GetPageAsync(client, licenseId, Stamp.AddMinutes(-1), Guid.Empty, 100);

        var balances = page.GetProperty("balances").EnumerateArray().ToList();
        balances.Should().HaveCount(1);
        balances[0].GetProperty("productVariantId").GetGuid().Should().Be(variantIds[0]);
        balances[0].GetProperty("quantity").GetInt32().Should().Be(-1);
        balances.Should().NotContain(
            b => b.GetProperty("productVariantId").GetGuid() == freshVariantId);
        // İmleç de taze satıra atlamamalı; ufkun altındaki son satırda kalır.
        page.GetProperty("cursorCreatedAt").GetDateTimeOffset().Should().Be(Stamp);
    }

    [Fact]
    public async Task Another_customers_license_is_not_readable()
    {
        var (client, _, _, _) = await SeedAsync(1, 1);
        var (_, otherLicenseId, _, _) = await SeedAsync(1, 1);

        var resp = await client.GetAsync(Url(otherLicenseId, Stamp.AddMinutes(-1), Guid.Empty, 100));

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
