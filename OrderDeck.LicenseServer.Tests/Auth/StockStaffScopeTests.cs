using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Auth;

/// <summary>
/// Stok elemanı rolü (Faz 1a). Kural: <c>stock</c> rolü için her uç varsayılan
/// olarak kapalı; yalnız <c>[AllowStockStaff]</c> ile işaretli uçlar açık.
/// </summary>
public class StockStaffScopeTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public StockStaffScopeTests(ApiFactory f) => _factory = f;

    private sealed record OperatorDto(
        Guid Id, Guid LicenseId, string Email, string Name, string Role,
        DateTimeOffset CreatedAt, DateTimeOffset? LastLoginAt, DateTimeOffset? RevokedAt);

    private sealed record OperatorLoginResp(
        string Token, DateTimeOffset ExpiresAt, Guid OperatorId, Guid TenantCustomerId,
        string Email, string Name, string Role);

    /// <summary>
    /// Ürün yanıtının yalnız bu testleri ilgilendiren dilimi; gerisi okunmuyor.
    /// <c>Name</c> bilerek burada: maskenin ucu KAPATMADIĞINI, sadece tek alanı
    /// boşalttığını aynı yanıtta göstermek için.
    /// </summary>
    private sealed record ProductCostView(Guid Id, string Name, decimal? Cost);

    private static Task<HttpResponseMessage> CreateProductAsync(
        HttpClient client, string name, decimal? cost)
        => client.PostAsJsonAsync("/api/panel/products",
            new { name, defaultPrice = 250m, cost });

    private static async Task<ProductCostView> ReadProductAsync(HttpClient client, Guid id)
    {
        var resp = await client.GetAsync($"/api/panel/products/{id}");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await resp.Content.ReadFromJsonAsync<ProductCostView>())!;
    }

    private static async Task<string?> TitleAsync(HttpResponseMessage resp)
    {
        var json = await resp.Content.ReadAsStringAsync();
        if (string.IsNullOrWhiteSpace(json)) return null;
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("title", out var t) ? t.GetString() : null;
    }

    private async Task<HttpClient> SeedOwnerAsync()
    {
        var (client, customerId, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        db.Licenses.Add(new License
        {
            Id = Guid.NewGuid(), CustomerId = customerId,
            LicenseKey = "LDK-STOK-" + Guid.NewGuid().ToString("N"),
            SkuCode = "STD", ActivationSlots = 1,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
        });
        await db.SaveChangesAsync();
        return client;
    }

    private static Task<HttpResponseMessage> InviteAsync(
        HttpClient ownerClient, string? role)
        => ownerClient.PostAsJsonAsync("/api/panel/operators", new
        {
            email = $"op-{Guid.NewGuid():N}@example.com",
            name = "Depo",
            password = "pwd-" + Guid.NewGuid().ToString("N"),
            role,
        });

    /// <summary>Verilen rolde bir operatör davet edip onun adına giriş yapmış client döner.</summary>
    private async Task<HttpClient> OperatorClientAsync(HttpClient ownerClient, string role)
    {
        var email = $"op-{Guid.NewGuid():N}@example.com";
        var password = "pwd-" + Guid.NewGuid().ToString("N");

        var invite = await ownerClient.PostAsJsonAsync("/api/panel/operators",
            new { email, name = "Depo", password, role });
        invite.StatusCode.Should().Be(HttpStatusCode.Created);

        var anon = _factory.CreateClient();
        var login = await anon.PostAsJsonAsync("/api/v1/auth/operator-login", new { email, password });
        login.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = (await login.Content.ReadFromJsonAsync<OperatorLoginResp>())!;
        body.Role.Should().Be(role);

        anon.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body.Token);
        return anon;
    }

    [Fact]
    public async Task Invite_without_a_role_still_creates_a_staff_operator()
    {
        var owner = await SeedOwnerAsync();

        var resp = await InviteAsync(owner, role: null);

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var dto = (await resp.Content.ReadFromJsonAsync<OperatorDto>())!;
        dto.Role.Should().Be("staff");
    }

    [Fact]
    public async Task Invite_creates_a_stock_operator()
    {
        var owner = await SeedOwnerAsync();

        var resp = await InviteAsync(owner, role: "stock");

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var dto = (await resp.Content.ReadFromJsonAsync<OperatorDto>())!;
        dto.Role.Should().Be("stock");
    }

    [Fact]
    public async Task Invite_400_on_an_unknown_role()
    {
        var owner = await SeedOwnerAsync();

        var resp = await InviteAsync(owner, role: "admin");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await TitleAsync(resp)).Should().Be("invalid-role");
    }

    [Fact]
    public async Task Invite_400_when_someone_tries_to_mint_an_owner()
    {
        var owner = await SeedOwnerAsync();

        var resp = await InviteAsync(owner, role: "owner");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await TitleAsync(resp)).Should().Be("invalid-role");
    }

    [Fact]
    public async Task Stock_operator_can_reach_the_catalog()
    {
        var owner = await SeedOwnerAsync();
        var stock = await OperatorClientAsync(owner, "stock");

        var products = await stock.GetAsync("/api/panel/products");
        var categories = await stock.GetAsync("/api/panel/categories");

        products.StatusCode.Should().Be(HttpStatusCode.OK);
        categories.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Stock_operator_is_blocked_from_the_customer_list()
    {
        var owner = await SeedOwnerAsync();
        var stock = await OperatorClientAsync(owner, "stock");

        var resp = await stock.GetAsync("/api/panel/customers");

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await TitleAsync(resp)).Should().Be("stock-staff-forbidden");
    }

    [Fact]
    public async Task Stock_operator_is_blocked_from_orders()
    {
        var owner = await SeedOwnerAsync();
        var stock = await OperatorClientAsync(owner, "stock");

        // Sipariş listesi PanelOrdersController'da oturum altında duruyor
        // ("/api/panel/orders" diye bir uç yok). Kapı action'dan önce çalıştığı
        // için oturumun gerçekten var olması gerekmiyor.
        var resp = await stock.GetAsync($"/api/panel/sessions/{Guid.NewGuid()}/orders");

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Staff_operator_still_reaches_the_customer_list()
    {
        var owner = await SeedOwnerAsync();
        var staff = await OperatorClientAsync(owner, "staff");

        var resp = await staff.GetAsync("/api/panel/customers");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Owner_token_is_unaffected_by_the_gate()
    {
        var owner = await SeedOwnerAsync();

        var resp = await owner.GetAsync("/api/panel/customers");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Stock_operator_reads_the_product_but_not_its_cost()
    {
        var owner = await SeedOwnerAsync();
        var stock = await OperatorClientAsync(owner, "stock");

        var created = await CreateProductAsync(owner, "Maliyetli ürün", cost: 100m);
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var id = (await created.Content.ReadFromJsonAsync<ProductCostView>())!.Id;

        var stockView = await ReadProductAsync(stock, id);
        var ownerView = await ReadProductAsync(owner, id);

        // Uç açık — kart okunuyor; yalnız maliyet alanı boş dönüyor.
        stockView.Name.Should().Be("Maliyetli ürün");
        stockView.Cost.Should().BeNull();
        ownerView.Cost.Should().Be(100m);
    }

    [Fact]
    public async Task Staff_operator_still_reads_the_cost()
    {
        var owner = await SeedOwnerAsync();
        var staff = await OperatorClientAsync(owner, "staff");

        var created = await CreateProductAsync(owner, "Staff görür", cost: 100m);
        var id = (await created.Content.ReadFromJsonAsync<ProductCostView>())!.Id;

        var staffView = await ReadProductAsync(staff, id);

        staffView.Cost.Should().Be(100m);
    }

    [Fact]
    public async Task Stock_operator_cannot_write_a_cost()
    {
        var owner = await SeedOwnerAsync();
        var stock = await OperatorClientAsync(owner, "stock");

        var resp = await CreateProductAsync(stock, "Maliyet yazmaya kalkış", cost: 100m);

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        // "stock-staff-forbidden" değil: uç açık, yasak olan yalnız bu alan.
        (await TitleAsync(resp)).Should().Be("cost-forbidden");
    }

    [Fact]
    public async Task Stock_operator_round_trip_does_not_wipe_the_cost()
    {
        var owner = await SeedOwnerAsync();
        var stock = await OperatorClientAsync(owner, "stock");

        var created = await CreateProductAsync(owner, "Adı düzeltilecek", cost: 100m);
        var id = (await created.Content.ReadFromJsonAsync<ProductCostView>())!.Id;

        // Stok elemanı maliyeti göremediği için gövdeye null koyup geri gönderir;
        // bu "sil" demek DEĞİL.
        var put = await stock.PutAsJsonAsync($"/api/panel/products/{id}",
            new { name = "Adı düzeltildi", defaultPrice = 250m, cost = (decimal?)null });
        put.StatusCode.Should().Be(HttpStatusCode.OK);

        var ownerView = await ReadProductAsync(owner, id);
        ownerView.Name.Should().Be("Adı düzeltildi");
        ownerView.Cost.Should().Be(100m);
    }
}
