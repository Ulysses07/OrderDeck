using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Auth;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Auth;

/// <summary>
/// Parola değiştiğinde eski oturumların gerçekten kapandığını doğrular.
///
/// Testler mekanizmayı değil <b>sonucu</b> ölçüyor: elde tutulan eski refresh
/// token'la yenileme denenir ve 401 beklenir. Böylece iptalin nasıl yapıldığı
/// (döngü, toplu UPDATE, ayrı servis) değişse de testler ayakta kalır.
///
/// Kapatılan açık: parola değiştirmenin tek sebebi çoğu zaman "hesabıma biri
/// girdi"dir. Eski refresh token'lar ayakta kalırsa parolayı değiştirmek
/// hesabı geri almaz — saldırgan token ömrü boyunca (müşteri tarafında 90 gün)
/// sessizce oturumda kalmaya devam eder.
/// </summary>
public sealed class PasswordChangeRevokesSessionsTests : IClassFixture<ApiFactory>
{
    private const string OldPassword = "old-password-12345";
    private const string NewPassword = "new-password-67890";

    private readonly ApiFactory _factory;
    public PasswordChangeRevokesSessionsTests(ApiFactory factory) => _factory = factory;

    // ── Yayıncı (Customer) tarafı ───────────────────────────────────────────

    private async Task<Customer> SeedConfirmedCustomerAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<PasswordHasher>();
        var c = new Customer
        {
            Id = Guid.NewGuid(),
            Email = $"revoke-{Guid.NewGuid():N}@x",
            Name = "Revoke Test",
            PasswordHash = hasher.Hash(OldPassword),
            CreatedAt = DateTimeOffset.UtcNow,
            EmailConfirmedAt = DateTimeOffset.UtcNow,
        };
        db.Customers.Add(c);
        await db.SaveChangesAsync();
        return c;
    }

    /// <summary>Giriş yapıp (accessToken, refreshToken) döner.</summary>
    private static async Task<(string Access, string Refresh)> LoginCustomerAsync(
        HttpClient client, string email)
    {
        var resp = await client.PostAsJsonAsync("/api/v1/auth/login",
            new { email, password = OldPassword });
        resp.StatusCode.Should().Be(HttpStatusCode.OK, "giriş ön koşulu tutmalı");
        var body = await resp.Content.ReadFromJsonAsync<LoginBody>();
        return (body!.Token, body.RefreshToken);
    }

    private sealed record LoginBody(string Token, string RefreshToken);

    [Fact]
    public async Task Yayinci_parolasini_degistirince_eski_oturum_yenileyemez()
    {
        var customer = await SeedConfirmedCustomerAsync();
        var client = _factory.CreateClient();
        var (access, oldRefresh) = await LoginCustomerAsync(client, customer.Email);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", access);
        var change = await client.PostAsJsonAsync("/api/v1/me/password",
            new { currentPassword = OldPassword, newPassword = NewPassword });
        change.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Elde kalan eski refresh token artık işe yaramamalı.
        var refresh = await client.PostAsJsonAsync("/api/v1/auth/refresh",
            new { refreshToken = oldRefresh });
        refresh.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "parola değiştikten sonra eski oturum yenilenebiliyorsa parola değişikliği hesabı geri almamış olur");
    }

    [Fact]
    public async Task Yayinci_parola_sifirlayinca_eski_oturum_yenileyemez()
    {
        var customer = await SeedConfirmedCustomerAsync();
        var client = _factory.CreateClient();
        var (_, oldRefresh) = await LoginCustomerAsync(client, customer.Email);

        Guid resetToken;
        using (var scope = _factory.Services.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<PasswordResetService>();
            await svc.RequestResetAsync(customer.Email, default);
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            resetToken = await db.PasswordResetTokens
                .Where(t => t.CustomerId == customer.Id && t.UsedAt == null)
                .Select(t => t.Id)
                .FirstAsync();
        }

        var complete = await client.PostAsJsonAsync("/api/v1/auth/password-reset",
            new { token = resetToken, newPassword = NewPassword });
        complete.IsSuccessStatusCode.Should().BeTrue("sıfırlama ön koşulu tutmalı");

        var refresh = await client.PostAsJsonAsync("/api/v1/auth/refresh",
            new { refreshToken = oldRefresh });
        refresh.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "sıfırlama akışının tamamı 'hesabıma erişemiyorum' içindir; eski oturum ayakta kalırsa amacını karşılamaz");
    }

    // ── Müşteri (Shopper) tarafı ────────────────────────────────────────────

    [Fact]
    public async Task Musteri_parolasini_degistirince_eski_oturum_yenileyemez()
    {
        var client = _factory.CreateClient();
        var code = await SeedBroadcasterCodeAsync();
        var phone = "+90500" + Random.Shared.Next(1_000_000, 9_999_999);

        var register = await client.PostAsJsonAsync("/api/v1/shopper/auth/register", new
        {
            broadcasterCode = code,
            fullName = "Revoke Shopper",
            phone,
            password = OldPassword,
            address = "Ankara",
            platform = "youtube",
            username = "revokeshopper",
        });
        register.StatusCode.Should().Be(HttpStatusCode.Created, "kayıt ön koşulu tutmalı");
        var reg = await register.Content.ReadFromJsonAsync<ShopperAuthBody>();

        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", reg!.AccessToken);
        var change = await client.PostAsJsonAsync("/api/v1/shopper/auth/change-password",
            new { currentPassword = OldPassword, newPassword = NewPassword });
        change.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var refresh = await client.PostAsJsonAsync("/api/v1/shopper/auth/refresh",
            new { refreshToken = reg.RefreshToken });
        refresh.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "parola değiştikten sonra eski cihaz yenilemeye devam edebiliyorsa oturum gerçekten kapanmamıştır");
    }

    private sealed record ShopperAuthBody(string AccessToken, string RefreshToken);

    private async Task<string> SeedBroadcasterCodeAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            Email = $"bc-{Guid.NewGuid():N}@x",
            Name = "Yayıncı",
            PasswordHash = "hash",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Customers.Add(customer);

        var code = "rvk" + Guid.NewGuid().ToString("N")[..8];
        db.Licenses.Add(new License
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            SkuCode = "STD",
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddYears(1),
            LicenseKey = "key-" + Guid.NewGuid().ToString("N"),
            ShopperCode = code,
            ShopperCodeUpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        return code;
    }
}
