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

public class PanelStockControllerTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public PanelStockControllerTests(ApiFactory factory) => _factory = factory;

    private sealed record Seed(HttpClient Client, Guid LicenseId, Guid ProductId, Guid VariantId);

    private async Task<Seed> SeedAsync()
    {
        var (client, customerId, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();

        var license = new License
        {
            Id = Guid.NewGuid(), CustomerId = customerId,
            LicenseKey = "LDK-PSTK-" + Guid.NewGuid().ToString("N"),
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
            Axis1Value = "M",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        };
        db.ProductVariants.Add(variant);

        await db.SaveChangesAsync();
        return new Seed(client, license.Id, product.Id, variant.Id);
    }

    private static async Task<string?> DetailAsync(HttpResponseMessage resp)
    {
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.TryGetProperty("detail", out var d) ? d.GetString() : null;
    }

    private static async Task<int> BalanceOfAsync(HttpClient client, Guid productId, Guid? variantId)
    {
        var resp = await client.GetAsync($"/api/panel/stock/balances?productId={productId}");
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        foreach (var row in doc.RootElement.EnumerateArray())
        {
            var rowVariant = row.GetProperty("productVariantId");
            var matches = variantId is null
                ? rowVariant.ValueKind == JsonValueKind.Null
                : rowVariant.ValueKind != JsonValueKind.Null
                  && rowVariant.GetGuid() == variantId.Value;
            if (matches) return row.GetProperty("quantity").GetInt32();
        }
        return 0;
    }

    [Fact]
    public async Task Entry_increases_the_balance()
    {
        var s = await SeedAsync();

        var resp = await s.Client.PostAsJsonAsync("/api/panel/stock/entries",
            new { productId = s.ProductId, productVariantId = s.VariantId, quantity = 12, note = "irsaliye 4412" });

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        (await BalanceOfAsync(s.Client, s.ProductId, s.VariantId)).Should().Be(12);
    }

    [Fact]
    public async Task Entry_rejects_zero_and_negative_quantity()
    {
        var s = await SeedAsync();

        foreach (var qty in new[] { 0, -3 })
        {
            var resp = await s.Client.PostAsJsonAsync("/api/panel/stock/entries",
                new { productId = s.ProductId, productVariantId = (Guid?)null, quantity = qty });
            resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }
    }

    [Fact]
    public async Task Entry_rejects_a_product_that_does_not_exist()
    {
        var s = await SeedAsync();

        var resp = await s.Client.PostAsJsonAsync("/api/panel/stock/entries",
            new { productId = Guid.NewGuid(), productVariantId = (Guid?)null, quantity = 1 });

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// Yukarıdaki test rastgele bir GUID gönderiyor — o satır zaten hiç yok, yani
    /// kiracı süzgeci tamamen kaldırılsa bile 404 gelirdi ve test yeşil yanardı.
    /// Burada ürün GERÇEKTEN var, sadece başkasının: 404 ancak süzgeç çalışıyorsa
    /// gelir. İzolasyonu ölçen tek test bu.
    /// </summary>
    [Fact]
    public async Task Entry_rejects_a_product_that_belongs_to_another_broadcaster()
    {
        var mine = await SeedAsync();
        var theirs = await SeedAsync();

        var resp = await mine.Client.PostAsJsonAsync("/api/panel/stock/entries",
            new { productId = theirs.ProductId, productVariantId = (Guid?)null, quantity = 1 });

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);

        // Ve girişin sahibinin defterine de düşmediğini doğrula: 404 dönüp satırı
        // yine de yazmak sessiz veri bozulması olurdu.
        (await BalanceOfAsync(theirs.Client, theirs.ProductId, null)).Should().Be(0);
    }

    /// <summary>Başkasının bakiyesi hiç görünmemeli — ürün kimliğini bilmek yetmez.</summary>
    [Fact]
    public async Task Balances_never_leak_another_broadcasters_product()
    {
        var mine = await SeedAsync();
        var theirs = await SeedAsync();

        await theirs.Client.PostAsJsonAsync("/api/panel/stock/entries",
            new { productId = theirs.ProductId, productVariantId = (Guid?)null, quantity = 7 });

        var resp = await mine.Client.GetAsync(
            $"/api/panel/stock/balances?productId={theirs.ProductId}");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await resp.Content.ReadFromJsonAsync<List<JsonElement>>())!.Should().BeEmpty();
    }

    /// <summary>
    /// Aktif lisansı olmayan yayıncı. Kardeş panel controller'ları bu durumda
    /// <c>no-active-license</c> başlıklı 400 dönüyor; buradan 404 dönerse panel
    /// "lisansın yok" ile "ürün yok"u ayırt edemez ve kullanıcıya yanlış şey söyler.
    /// </summary>
    [Fact]
    public async Task Without_an_active_license_the_answer_is_a_titled_400()
    {
        var (client, _, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);

        foreach (var resp in new[]
                 {
                     await client.GetAsync("/api/panel/stock/balances"),
                     await client.GetAsync($"/api/panel/stock/movements?productId={Guid.NewGuid()}"),
                     await client.PostAsJsonAsync("/api/panel/stock/entries",
                         new { productId = Guid.NewGuid(), quantity = 1 }),
                     await client.PostAsJsonAsync("/api/panel/stock/counts",
                         new { productId = Guid.NewGuid(), countedQuantity = 1 }),
                 })
        {
            resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await resp.Content.ReadAsStringAsync()).Should().Contain("no-active-license");
        }
    }

    /// <summary>
    /// <c>productId</c> tekrar edilebilir bir sorgu parametresi, yani istemci
    /// binlercesini gönderebilir ve her biri doğrudan bir <c>IN</c> listesine
    /// giriyor. Tavan sessizce kırpılmıyor: kırpılsaydı panel eksik bakiye
    /// görürdü ve bunu anlamasının bir yolu olmazdı.
    /// </summary>
    [Fact]
    public async Task Balances_rejects_more_product_ids_than_the_cap()
    {
        var s = await SeedAsync();

        var query = string.Join("&", Enumerable.Range(0, 201)
            .Select(_ => $"productId={Guid.NewGuid()}"));

        var resp = await s.Client.GetAsync($"/api/panel/stock/balances?{query}");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("too-many-products");
    }

    [Fact]
    public async Task Entry_rejects_a_variant_that_belongs_to_another_product()
    {
        var s = await SeedAsync();

        var resp = await s.Client.PostAsJsonAsync("/api/panel/stock/entries",
            new { productId = s.ProductId, productVariantId = Guid.NewGuid(), quantity = 1 });

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await DetailAsync(resp)).Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Count_writes_the_difference_only()
    {
        var s = await SeedAsync();

        await s.Client.PostAsJsonAsync("/api/panel/stock/entries",
            new { productId = s.ProductId, productVariantId = s.VariantId, quantity = 10 });

        var resp = await s.Client.PostAsJsonAsync("/api/panel/stock/counts",
            new { productId = s.ProductId, productVariantId = s.VariantId, countedQuantity = 7, note = "sayım" });

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        (await BalanceOfAsync(s.Client, s.ProductId, s.VariantId)).Should().Be(7);

        var movements = await s.Client.GetFromJsonAsync<List<JsonElement>>(
            $"/api/panel/stock/movements?productId={s.ProductId}");
        movements!.Should().HaveCount(2);
        movements.Should().Contain(m => m.GetProperty("quantity").GetInt32() == -3);
    }

    [Fact]
    public async Task Count_matching_the_ledger_writes_no_movement()
    {
        var s = await SeedAsync();

        await s.Client.PostAsJsonAsync("/api/panel/stock/entries",
            new { productId = s.ProductId, productVariantId = s.VariantId, quantity = 5 });

        var resp = await s.Client.PostAsJsonAsync("/api/panel/stock/counts",
            new { productId = s.ProductId, productVariantId = s.VariantId, countedQuantity = 5 });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var movements = await s.Client.GetFromJsonAsync<List<JsonElement>>(
            $"/api/panel/stock/movements?productId={s.ProductId}");
        movements!.Should().ContainSingle();
    }

    [Fact]
    public async Task Count_rejects_negative_counted_quantity()
    {
        var s = await SeedAsync();

        var resp = await s.Client.PostAsJsonAsync("/api/panel/stock/counts",
            new { productId = s.ProductId, productVariantId = (Guid?)null, countedQuantity = -1 });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Movements_are_returned_newest_first()
    {
        var s = await SeedAsync();

        await s.Client.PostAsJsonAsync("/api/panel/stock/entries",
            new { productId = s.ProductId, productVariantId = (Guid?)null, quantity = 1, note = "ilk" });
        await s.Client.PostAsJsonAsync("/api/panel/stock/entries",
            new { productId = s.ProductId, productVariantId = (Guid?)null, quantity = 2, note = "ikinci" });

        var movements = await s.Client.GetFromJsonAsync<List<JsonElement>>(
            $"/api/panel/stock/movements?productId={s.ProductId}");

        movements!.Should().HaveCount(2);
        movements[0].GetProperty("note").GetString().Should().Be("ikinci");
    }
}
