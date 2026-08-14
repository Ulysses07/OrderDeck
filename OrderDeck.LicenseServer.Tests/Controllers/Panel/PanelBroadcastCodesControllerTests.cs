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

public class PanelBroadcastCodesControllerTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public PanelBroadcastCodesControllerTests(ApiFactory f) => _factory = f;

    [Fact]
    public async Task Code_is_saved_and_returned_for_the_seller_axis_value()
    {
        var client = await NewPanelClientAsync();
        var productId = await NewProductWithSellerAxisAsync(client);

        var put = await client.PutAsJsonAsync(
            $"/api/panel/products/{productId}/broadcast-codes",
            new { sellerAxisValue = "Siyah", code = " ateş " });

        put.StatusCode.Should().Be(HttpStatusCode.OK);

        var get = await client.GetFromJsonAsync<JsonElement>(
            $"/api/panel/products/{productId}/broadcast-codes");

        get.GetArrayLength().Should().Be(1);
        get[0].GetProperty("code").GetString().Should().Be("ateş");
        get[0].GetProperty("sellerAxisValue").GetString().Should().Be("Siyah");
    }

    /// <summary>
    /// Kod bir daha ASLA devredilmez: başka bir ürüne verilmiş kod reddedilir.
    /// Devredilseydi, eski yayın videosundaki kodu bugün yazan izleyicinin
    /// siparişi yanlış ürüne düşerdi.
    /// </summary>
    [Fact]
    public async Task Code_used_by_another_product_is_rejected()
    {
        var client = await NewPanelClientAsync();
        var first = await NewProductWithSellerAxisAsync(client);
        var second = await NewProductWithSellerAxisAsync(client);

        await client.PutAsJsonAsync(
            $"/api/panel/products/{first}/broadcast-codes",
            new { sellerAxisValue = "Siyah", code = "ATEŞ" });

        var clash = await client.PutAsJsonAsync(
            $"/api/panel/products/{second}/broadcast-codes",
            new { sellerAxisValue = "Siyah", code = "ates" });

        clash.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await clash.Content.ReadAsStringAsync())
            .Should().Contain("Bu yayın kodu daha önce kullanılmış.");
    }

    /// <summary>
    /// Aynı hedefe aynı kodu yeniden yazmak çakışma değil; satır tazelenir.
    /// Panel kaydete iki kez basınca 409 görmemeli.
    /// </summary>
    [Fact]
    public async Task Rewriting_the_same_code_to_the_same_target_succeeds()
    {
        var client = await NewPanelClientAsync();
        var productId = await NewProductWithSellerAxisAsync(client);

        await client.PutAsJsonAsync(
            $"/api/panel/products/{productId}/broadcast-codes",
            new { sellerAxisValue = "Siyah", code = "ATEŞ" });

        var again = await client.PutAsJsonAsync(
            $"/api/panel/products/{productId}/broadcast-codes",
            new { sellerAxisValue = "Siyah", code = "ATEŞ" });

        again.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// Kod değişince eski satır SİLİNMEZ — kodu rezerve tutmaya devam eder.
    /// "Güncel" olan yalnız en yeni satır, GET onu döndürür.
    /// </summary>
    [Fact]
    public async Task Changing_the_code_keeps_the_old_one_reserved()
    {
        var client = await NewPanelClientAsync();
        var productId = await NewProductWithSellerAxisAsync(client);
        var other = await NewProductWithSellerAxisAsync(client);

        await client.PutAsJsonAsync(
            $"/api/panel/products/{productId}/broadcast-codes",
            new { sellerAxisValue = "Siyah", code = "ESKI" });
        await client.PutAsJsonAsync(
            $"/api/panel/products/{productId}/broadcast-codes",
            new { sellerAxisValue = "Siyah", code = "YENI" });

        var current = await client.GetFromJsonAsync<JsonElement>(
            $"/api/panel/products/{productId}/broadcast-codes");
        current.GetArrayLength().Should().Be(1);
        current[0].GetProperty("code").GetString().Should().Be("YENI");

        var stealOld = await client.PutAsJsonAsync(
            $"/api/panel/products/{other}/broadcast-codes",
            new { sellerAxisValue = "Siyah", code = "ESKI" });

        stealOld.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Unknown_seller_axis_value_is_rejected()
    {
        var client = await NewPanelClientAsync();
        var productId = await NewProductWithSellerAxisAsync(client);

        var res = await client.PutAsJsonAsync(
            $"/api/panel/products/{productId}/broadcast-codes",
            new { sellerAxisValue = "Turuncu", code = "ATEŞ" });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Empty_code_is_rejected()
    {
        var client = await NewPanelClientAsync();
        var productId = await NewProductWithSellerAxisAsync(client);

        var res = await client.PutAsJsonAsync(
            $"/api/panel/products/{productId}/broadcast-codes",
            new { sellerAxisValue = "Siyah", code = "   " });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    /// <summary>
    /// Kodun kalıcı rezervasyonu ancak satır durursa mümkün; ürün fiziksel
    /// silinirse cascade satırı götürür ve kod yeniden dağıtılabilir hâle
    /// gelirdi. O yüzden kodu olan ürün silinmez, arşivlenir.
    /// </summary>
    [Fact]
    public async Task Product_with_broadcast_code_cannot_be_deleted()
    {
        var client = await NewPanelClientAsync();
        var productId = await NewProductWithSellerAxisAsync(client);

        await client.PutAsJsonAsync(
            $"/api/panel/products/{productId}/broadcast-codes",
            new { sellerAxisValue = "Siyah", code = "ATEŞ" });

        var del = await client.DeleteAsync($"/api/panel/products/{productId}");

        del.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    /// <summary>
    /// Kimliği doğrulanmış panel istemcisi + altında aktif bir lisans.
    /// <c>PanelProductsControllerTests.SeedAsync</c> ile aynı kalıp.
    /// </summary>
    private async Task<HttpClient> NewPanelClientAsync()
    {
        var (client, customerId, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        db.Licenses.Add(new License
        {
            Id = Guid.NewGuid(), CustomerId = customerId,
            LicenseKey = "LDK-PROD-" + Guid.NewGuid().ToString("N"),
            SkuCode = "STD", ActivationSlots = 1,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
        });
        await db.SaveChangesAsync();
        return client;
    }

    /// <summary>
    /// Ürün "Renk" ekseni satıcı rolünde, altında Siyah varyantı olacak şekilde
    /// kurulur ve Id'si döner.
    /// </summary>
    private static async Task<Guid> NewProductWithSellerAxisAsync(HttpClient client)
    {
        var created = await client.PostAsJsonAsync("/api/panel/products", new
        {
            name = "Yayın Kodlu " + Guid.NewGuid().ToString("N")[..6],
            defaultPrice = 100m,
            axis1Name = "Renk",
            axis1Role = 1,
        });
        created.StatusCode.Should().Be(HttpStatusCode.Created);

        var dto = await created.Content.ReadFromJsonAsync<JsonElement>();
        var id = dto.GetProperty("id").GetGuid();

        var variant = await client.PostAsJsonAsync(
            $"/api/panel/products/{id}/variants",
            new { axis1Value = "Siyah", isActive = true });
        variant.StatusCode.Should().Be(HttpStatusCode.Created);

        return id;
    }
}
