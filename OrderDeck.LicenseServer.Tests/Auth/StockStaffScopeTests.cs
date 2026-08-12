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
}
