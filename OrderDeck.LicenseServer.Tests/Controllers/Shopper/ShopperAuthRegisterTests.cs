using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Auth;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Shopper;

public class ShopperAuthRegisterTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public ShopperAuthRegisterTests(ApiFactory factory) => _factory = factory;

    // ── DTOs matching the controller ────────────────────────────────────────
    private sealed record RegisterRequest(
        string BroadcasterCode,
        string FullName,
        string Phone,
        string Password,
        string Address,
        string Platform,
        string Username,
        string? Email = null,
        string? Tc = null);

    private sealed record BroadcasterSummary(Guid LicenseId, string DisplayName, string Platform, string Username);

    private sealed record AuthResponse(
        string AccessToken,
        DateTimeOffset AccessTokenExpiresAt,
        string RefreshToken,
        DateTimeOffset RefreshTokenExpiresAt,
        Guid ShopperId,
        BroadcasterSummary[] Broadcasters);

    // ── Seeding helpers ──────────────────────────────────────────────────────

    private static string UniquePhone() =>
        "+9055" + Random.Shared.Next(10_000_000, 99_999_999).ToString();

    private async Task<(Guid licenseId, string code, string customerName)> SeedLicenseAsync(
        string? code = null)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var sku = await db.Skus.FirstOrDefaultAsync() ??
                  new Sku { Code = "TST", DisplayName = "Test", DefaultDurationDays = 365, DefaultActivationSlots = 1 };
        if (!await db.Skus.AnyAsync()) db.Skus.Add(sku);

        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            Email = $"cust-{Guid.NewGuid():N}@x.test",
            Name = "YayinciName-" + Guid.NewGuid().ToString("N")[..6],
            PasswordHash = "ph",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Customers.Add(customer);

        var shopperCode = (code ?? Guid.NewGuid().ToString("N")[..10]).ToLowerInvariant();
        var licenseId = Guid.NewGuid();
        db.Licenses.Add(new License
        {
            Id = licenseId,
            CustomerId = customer.Id,
            SkuCode = "STD",
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddYears(1),
            LicenseKey = "key-" + Guid.NewGuid().ToString("N"),
            ShopperCode = shopperCode,
            ShopperCodeUpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        return (licenseId, shopperCode, customer.Name);
    }

    // ── T7.1: Happy path → 201, JWT in body, Shopper row, Link row ──────────

    [Fact]
    public async Task Register_happy_path_returns_201_with_tokens_and_creates_rows()
    {
        var (licenseId, code, _) = await SeedLicenseAsync();
        var phone = UniquePhone();
        var req = new RegisterRequest(code, "Test Kullanıcı", phone, "Password1!", "İstanbul", "youtube", "testuser");

        var resp = await _factory.CreateClient()
            .PostAsJsonAsync("/api/v1/shopper/auth/register", req);

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await resp.Content.ReadFromJsonAsync<AuthResponse>();
        body.Should().NotBeNull();
        body!.AccessToken.Should().NotBeNullOrEmpty();
        body.RefreshToken.Should().NotBeNullOrEmpty();
        body.ShopperId.Should().NotBeEmpty();
        body.Broadcasters.Should().HaveCount(1);
        body.Broadcasters[0].LicenseId.Should().Be(licenseId);
        body.Broadcasters[0].Platform.Should().Be("youtube");
        body.Broadcasters[0].Username.Should().Be("testuser");

        // Verify rows in DB
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var shopper = await db.Shoppers.FirstOrDefaultAsync(s => s.Phone == phone);
        shopper.Should().NotBeNull();
        var link = await db.ShopperBroadcasterLinks
            .FirstOrDefaultAsync(l => l.ShopperId == shopper!.Id && l.LicenseId == licenseId);
        link.Should().NotBeNull();
        link!.Platform.Should().Be("youtube");
        link.Username.Should().Be("testuser");
    }

    // ── T7.2: Existing phone + correct password → reuses Shopper (no dup) ───

    [Fact]
    public async Task Register_existing_phone_correct_password_reuses_shopper()
    {
        var (_, code1, _) = await SeedLicenseAsync();
        var (_, code2, _) = await SeedLicenseAsync();
        var phone = UniquePhone();

        // First register
        var req1 = new RegisterRequest(code1, "Ali Veli", phone, "Password1!", "Ankara", "instagram", "aliveli");
        var resp1 = await _factory.CreateClient()
            .PostAsJsonAsync("/api/v1/shopper/auth/register", req1);
        resp1.StatusCode.Should().Be(HttpStatusCode.Created);
        var body1 = await resp1.Content.ReadFromJsonAsync<AuthResponse>();

        // Second register with same phone+password but different broadcaster
        var req2 = new RegisterRequest(code2, "Ali Veli", phone, "Password1!", "Ankara", "tiktok", "aliveli2");
        var resp2 = await _factory.CreateClient()
            .PostAsJsonAsync("/api/v1/shopper/auth/register", req2);
        resp2.StatusCode.Should().Be(HttpStatusCode.Created);
        var body2 = await resp2.Content.ReadFromJsonAsync<AuthResponse>();

        // Same ShopperId
        body2!.ShopperId.Should().Be(body1!.ShopperId);

        // No duplicate Shopper row
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var count = await db.Shoppers.CountAsync(s => s.Phone == phone);
        count.Should().Be(1);
    }

    // ── T7.3: Existing phone + wrong password → 401 phone-already-used ──────

    [Fact]
    public async Task Register_existing_phone_wrong_password_returns_401()
    {
        var (_, code1, _) = await SeedLicenseAsync();
        var (_, code2, _) = await SeedLicenseAsync();
        var phone = UniquePhone();

        var req1 = new RegisterRequest(code1, "Fatma", phone, "CorrectPass1!", "İzmir", "youtube", "fatma");
        await _factory.CreateClient().PostAsJsonAsync("/api/v1/shopper/auth/register", req1);

        var req2 = new RegisterRequest(code2, "Fatma", phone, "WrongPass99!", "İzmir", "youtube", "fatma2");
        var resp = await _factory.CreateClient()
            .PostAsJsonAsync("/api/v1/shopper/auth/register", req2);

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── T7.4: Unknown broadcaster code → 404 invalid-code ───────────────────

    [Fact]
    public async Task Register_unknown_code_returns_404()
    {
        var phone = UniquePhone();
        var req = new RegisterRequest("nosuchcode9999", "User", phone, "Password1!", "Bursa", "youtube", "u");
        var resp = await _factory.CreateClient()
            .PostAsJsonAsync("/api/v1/shopper/auth/register", req);
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── T7.5: Invalid phone → 400 invalid-phone ──────────────────────────────

    [Fact]
    public async Task Register_invalid_phone_returns_400()
    {
        var (_, code, _) = await SeedLicenseAsync();
        var req = new RegisterRequest(code, "User", "notaphone", "Password1!", "Konya", "youtube", "u");
        var resp = await _factory.CreateClient()
            .PostAsJsonAsync("/api/v1/shopper/auth/register", req);
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── T7.6: Weak password (<8) → 400 weak-password ────────────────────────

    [Fact]
    public async Task Register_weak_password_returns_400()
    {
        var (_, code, _) = await SeedLicenseAsync();
        var phone = UniquePhone();
        var req = new RegisterRequest(code, "User", phone, "short", "Adana", "youtube", "u");
        var resp = await _factory.CreateClient()
            .PostAsJsonAsync("/api/v1/shopper/auth/register", req);
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── T7.7: WpfCustomerProjection match → link.WpfCustomerId populated ────

    [Fact]
    public async Task Register_with_matching_wpf_projection_populates_WpfCustomerId()
    {
        var (licenseId, code, _) = await SeedLicenseAsync();

        // Seed a WpfCustomerProjection that matches (licenseId, "youtube", "wpfmatch")
        var wpfId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            db.WpfCustomerProjections.Add(new WpfCustomerProjection
            {
                Id = wpfId,
                LicenseId = licenseId,
                Platform = "youtube",
                Username = "wpfmatch",
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var phone = UniquePhone();
        var req = new RegisterRequest(code, "WpfUser", phone, "Password1!", "Samsun", "youtube", "wpfmatch");
        var resp = await _factory.CreateClient()
            .PostAsJsonAsync("/api/v1/shopper/auth/register", req);
        resp.StatusCode.Should().Be(HttpStatusCode.Created);

        using var scope2 = _factory.Services.CreateScope();
        var db2 = scope2.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var shopper = await db2.Shoppers.FirstAsync(s => s.Phone == phone);
        var link = await db2.ShopperBroadcasterLinks
            .FirstAsync(l => l.ShopperId == shopper.Id && l.LicenseId == licenseId);
        link.WpfCustomerId.Should().Be(wpfId);
    }

    // ── T7.8: Same shopper registers again to same broadcaster → 409 ────────

    [Fact]
    public async Task Register_same_shopper_same_broadcaster_returns_409()
    {
        var (_, code, _) = await SeedLicenseAsync();
        var phone = UniquePhone();
        var req = new RegisterRequest(code, "Dup User", phone, "Password1!", "Erzurum", "youtube", "dupuser");

        var resp1 = await _factory.CreateClient()
            .PostAsJsonAsync("/api/v1/shopper/auth/register", req);
        resp1.StatusCode.Should().Be(HttpStatusCode.Created);

        var resp2 = await _factory.CreateClient()
            .PostAsJsonAsync("/api/v1/shopper/auth/register", req);
        resp2.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    // ── T7.9: No existing WpfProjection → auto-creates one + sets WpfCustomerId ─

    [Fact]
    public async Task Register_without_existing_projection_auto_creates_projection_and_sets_WpfCustomerId()
    {
        var (licenseId, code, _) = await SeedLicenseAsync();
        var phone = UniquePhone();
        var req = new RegisterRequest(code, "AutoProj User", phone, "Password1!", "Kayseri", "tiktok", "autoprojuser");

        var resp = await _factory.CreateClient()
            .PostAsJsonAsync("/api/v1/shopper/auth/register", req);
        resp.StatusCode.Should().Be(HttpStatusCode.Created);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();

        // A WpfCustomerProjection must have been created
        var projection = await db.WpfCustomerProjections
            .FirstOrDefaultAsync(p => p.LicenseId == licenseId && p.Platform == "tiktok" && p.Username == "autoprojuser");
        projection.Should().NotBeNull("auto-projection must be created on register when no prior match");
        projection!.FullName.Should().Be("AutoProj User");
        projection.Phone.Should().NotBeNull();

        // And the link's WpfCustomerId must point to the new projection
        var shopper = await db.Shoppers.FirstAsync(s => s.Phone == phone);
        var link = await db.ShopperBroadcasterLinks
            .FirstAsync(l => l.ShopperId == shopper.Id && l.LicenseId == licenseId);
        link.WpfCustomerId.Should().Be(projection.Id);
    }

    // ── T7.10: Existing WpfProjection → no duplicate created, existing id used ─

    [Fact]
    public async Task Register_with_existing_projection_does_not_create_duplicate()
    {
        var (licenseId, code, _) = await SeedLicenseAsync();
        var wpfId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            db.WpfCustomerProjections.Add(new WpfCustomerProjection
            {
                Id = wpfId,
                LicenseId = licenseId,
                Platform = "youtube",
                Username = "existingprojuser",
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var phone = UniquePhone();
        var req = new RegisterRequest(code, "ExistProj User", phone, "Password1!", "Trabzon", "youtube", "existingprojuser");
        var resp = await _factory.CreateClient()
            .PostAsJsonAsync("/api/v1/shopper/auth/register", req);
        resp.StatusCode.Should().Be(HttpStatusCode.Created);

        using var scope2 = _factory.Services.CreateScope();
        var db2 = scope2.ServiceProvider.GetRequiredService<LicenseDbContext>();

        // Should NOT have created a second projection
        var projections = await db2.WpfCustomerProjections
            .Where(p => p.LicenseId == licenseId && p.Platform == "youtube" && p.Username == "existingprojuser")
            .ToListAsync();
        projections.Should().HaveCount(1, "no duplicate projection should be created when one already exists");
        projections[0].Id.Should().Be(wpfId, "existing projection id must be reused");

        // Link must point to the pre-existing projection
        var shopper = await db2.Shoppers.FirstAsync(s => s.Phone == phone);
        var link = await db2.ShopperBroadcasterLinks
            .FirstAsync(l => l.ShopperId == shopper.Id && l.LicenseId == licenseId);
        link.WpfCustomerId.Should().Be(wpfId);
    }
}
