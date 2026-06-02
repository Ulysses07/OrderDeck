using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Services.Auth;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Shopper;

public class ShopperAuthResetPasswordTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public ShopperAuthResetPasswordTests(ApiFactory factory) => _factory = factory;

    private sealed record ForgotPasswordRequest(string Phone);
    private sealed record ResetPasswordRequest(string Phone, string Code, string NewPassword);
    private sealed record LoginRequest(string Phone, string Password);
    private sealed record RefreshRequest(string RefreshToken);
    private sealed record AuthBody(string AccessToken, string RefreshToken);

    private static string UniquePhone() =>
        "+9055" + Random.Shared.Next(10_000_000, 99_999_999).ToString();

    private async Task<string> SeedShopperAsync(string phone, string password)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<PasswordHasher>();
        var now = DateTimeOffset.UtcNow;
        db.Shoppers.Add(new OrderDeck.LicenseServer.Domain.Shopper
        {
            Id = Guid.NewGuid(),
            FullName = "Reset Tester",
            Phone = phone,
            PasswordHash = hasher.Hash(password),
            Address = "Addr",
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync();
        return phone;
    }

    private async Task<string> RequestCodeAsync(System.Net.Http.HttpClient client, string phone)
    {
        var resp = await client.PostAsJsonAsync("/api/v1/shopper/auth/forgot-password",
            new ForgotPasswordRequest(phone));
        resp.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var msg = _factory.Sms.Sent.Last(m => m.Phone == phone).Text;
        return Regex.Match(msg, @"\d{6}").Value;
    }

    [Fact]
    public async Task ResetPassword_happy_path_changes_password_and_revokes_tokens()
    {
        var phone = UniquePhone();
        await SeedShopperAsync(phone, "OldPass1!");
        var client = _factory.CreateClient();

        // Eski oturum (refresh token) al.
        var loginResp = await client.PostAsJsonAsync("/api/v1/shopper/auth/login",
            new LoginRequest(phone, "OldPass1!"));
        loginResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var oldAuth = await loginResp.Content.ReadFromJsonAsync<AuthBody>();

        var code = await RequestCodeAsync(client, phone);

        var resetResp = await client.PostAsJsonAsync("/api/v1/shopper/auth/reset-password",
            new ResetPasswordRequest(phone, code, "NewPass1!"));
        resetResp.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Eski refresh token iptal edildi → 401.
        var refreshResp = await client.PostAsJsonAsync("/api/v1/shopper/auth/refresh",
            new RefreshRequest(oldAuth!.RefreshToken));
        refreshResp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // Yeni parolayla giriş çalışır.
        var newLogin = await client.PostAsJsonAsync("/api/v1/shopper/auth/login",
            new LoginRequest(phone, "NewPass1!"));
        newLogin.StatusCode.Should().Be(HttpStatusCode.OK);

        // Eski parola artık çalışmaz.
        var oldLogin = await client.PostAsJsonAsync("/api/v1/shopper/auth/login",
            new LoginRequest(phone, "OldPass1!"));
        oldLogin.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ResetPassword_wrong_code_returns_400()
    {
        var phone = UniquePhone();
        await SeedShopperAsync(phone, "OldPass1!");
        var client = _factory.CreateClient();
        await RequestCodeAsync(client, phone);

        var resp = await client.PostAsJsonAsync("/api/v1/shopper/auth/reset-password",
            new ResetPasswordRequest(phone, "000000", "NewPass1!"));
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ResetPassword_weak_password_returns_400()
    {
        var phone = UniquePhone();
        await SeedShopperAsync(phone, "OldPass1!");
        var client = _factory.CreateClient();
        var code = await RequestCodeAsync(client, phone);

        var resp = await client.PostAsJsonAsync("/api/v1/shopper/auth/reset-password",
            new ResetPasswordRequest(phone, code, "short"));
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // Kod tüketilmedi (parola önce kontrol edilir) → doğru parolayla çalışır.
        var ok = await client.PostAsJsonAsync("/api/v1/shopper/auth/reset-password",
            new ResetPasswordRequest(phone, code, "NewPass1!"));
        ok.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task ResetPassword_unknown_phone_returns_400()
    {
        var client = _factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/api/v1/shopper/auth/reset-password",
            new ResetPasswordRequest(UniquePhone(), "123456", "NewPass1!"));
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
