using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Auth;
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

    private async Task<HttpClient> SeedOwnerAsync() => (await SeedOwnerWithIdAsync()).Client;

    private async Task<(HttpClient Client, Guid CustomerId)> SeedOwnerWithIdAsync()
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
        return (client, customerId);
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

    /// <summary>
    /// Faz 1a ÖNCESİ üretilmiş bir operatör token'ını birebir taklit eder: claim
    /// listesi <c>JwtTokenService.IssueOperatorToken</c> ile aynı, yalnız
    /// <c>oprole</c> YOK. Token üretim imzalayıcısının anahtarı/issuer'ı/
    /// audience'ı ile kuruluyor; başka yolu yok — servis bugün rolü her zaman
    /// ekliyor, yani bu şekli yalnız elle kurabiliriz.
    /// </summary>
    private async Task<HttpClient> LegacyOperatorClientWithoutRoleClaimAsync(
        HttpClient ownerClient, Guid tenantCustomerId)
    {
        var email = $"op-{Guid.NewGuid():N}@example.com";
        var invite = await ownerClient.PostAsJsonAsync("/api/panel/operators", new
        {
            email, name = "Eski operatör",
            password = "pwd-" + Guid.NewGuid().ToString("N"),
            role = OperatorRoles.Staff,
        });
        invite.StatusCode.Should().Be(HttpStatusCode.Created);
        var operatorId = (await invite.Content.ReadFromJsonAsync<OperatorDto>())!.Id;

        using var scope = _factory.Services.CreateScope();
        var options = scope.ServiceProvider.GetRequiredService<IOptions<JwtOptions>>().Value;
        var signing = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.SecretKey)),
            SecurityAlgorithms.HmacSha256);

        var jwt = new JwtSecurityToken(
            issuer: options.Issuer,
            audience: JwtOptions.CustomerAudience,
            claims:
            [
                new Claim(TenantClaims.Sub, operatorId.ToString()),
                new Claim(TenantClaims.TenantCustomerId, tenantCustomerId.ToString()),
                new Claim(TenantClaims.PrincipalType, "operator"),
                new Claim(TenantClaims.OperatorId, operatorId.ToString()),
                new Claim("email", email),
            ],
            notBefore: DateTime.UtcNow,
            expires: DateTime.UtcNow.AddMinutes(15),
            signingCredentials: signing);
        var token = new JwtSecurityTokenHandler().WriteToken(jwt);

        // Güvenlik ağı: claim adı yarın değişirse test sessizce anlamsızlaşmasın.
        new JwtSecurityTokenHandler().ReadJwtToken(token).Claims
            .Should().NotContain(c => c.Type == TenantClaims.OperatorRole,
                "testin bütün anlamı rolsüz token");

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
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
        // 403 birçok sebepten dönebilir; testin DOĞRU sebebi doğrulaması lazım.
        (await TitleAsync(resp)).Should().Be("stock-staff-forbidden");
    }

    /// <summary>
    /// Geriye dönük uyumluluk: <c>oprole</c> claim'i Faz 1a ile geldi, yani
    /// sahadaki her ESKİ operatör token'ı onu taşımıyor. Doğru davranış, rol
    /// yokken eski <c>staff</c> davranışını sürdürmek — kapı yalnız "stock"
    /// değerine tepki verir. Kapı yarın "rol yoksa kapat" diye çevrilirse
    /// sahadaki bütün eski token'lar sessizce kilitlenir; bu test o değişikliği
    /// süit yeşilken geçmeye bırakmaz.
    /// </summary>
    [Fact]
    public async Task Operator_token_without_the_role_claim_keeps_full_access()
    {
        var (owner, customerId) = await SeedOwnerWithIdAsync();
        var legacy = await LegacyOperatorClientWithoutRoleClaimAsync(owner, customerId);

        // Stok kısıtına takılan uç (işaretsiz) ve açık uç — ikisi de 200 olmalı.
        var customers = await legacy.GetAsync("/api/panel/customers");
        var products = await legacy.GetAsync("/api/panel/products");

        customers.StatusCode.Should().Be(HttpStatusCode.OK);
        products.StatusCode.Should().Be(HttpStatusCode.OK);
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

    /// <summary>
    /// Hareket dökümü stok elemanına açık, ama <c>orderId</c> siparişe götüren
    /// bir tutamaç — spec'in "sipariş bilgisini göremez" kuralı tam olarak bunu
    /// kapsıyor. Maliyet maskesiyle aynı biçim: uç kapanmıyor, tek alan boşalıyor.
    /// </summary>
    [Fact]
    public async Task Stock_operator_reads_the_movement_but_not_its_order_id()
    {
        var (owner, customerId) = await SeedOwnerWithIdAsync();
        var stock = await OperatorClientAsync(owner, "stock");

        var created = await CreateProductAsync(owner, "Satılan ürün", cost: null);
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var productId = (await created.Content.ReadFromJsonAsync<ProductCostView>())!.Id;

        // Satış hareketleri yalnız WPF senkronundan doğuyor; panelden yazmanın
        // yolu yok, o yüzden satır doğrudan kuruluyor.
        var orderId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var licenseId = db.Licenses.First(l => l.CustomerId == customerId).Id;
            db.StockMovements.Add(new StockMovement
            {
                Id = Guid.NewGuid(), LicenseId = licenseId, ProductId = productId,
                Quantity = -1, Reason = StockMovementReason.Sale, OrderId = orderId,
                OccurredAt = DateTimeOffset.UtcNow, CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var stockView = await ReadMovementAsync(stock, productId);
        var ownerView = await ReadMovementAsync(owner, productId);

        // Uç açık — hareket okunuyor; yalnız sipariş bağı boş dönüyor.
        stockView.Quantity.Should().Be(-1);
        stockView.OrderId.Should().BeNull();
        ownerView.OrderId.Should().Be(orderId);
    }

    private sealed record MovementView(Guid Id, int Quantity, Guid? OrderId);

    private static async Task<MovementView> ReadMovementAsync(HttpClient client, Guid productId)
    {
        var resp = await client.GetAsync($"/api/panel/stock/movements?productId={productId}");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await resp.Content.ReadFromJsonAsync<List<MovementView>>())!.Single();
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
