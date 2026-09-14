using System.IdentityModel.Tokens.Jwt;
using FluentAssertions;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Services.Auth;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Auth;

public class JwtTokenServiceShopperTests
{
    private static JwtTokenService NewService() =>
        new(Options.Create(new JwtOptions
        {
            SecretKey = new string('k', 64),
            Issuer = "orderdeck-test",
            AccessTokenLifetimeMinutes = 30,
        }));

    [Fact]
    public void IssueShopperToken_emits_signed_jwt_with_shopper_audience()
    {
        var svc = NewService();
        var shopperId = Guid.NewGuid();
        var phone = "+9055" + Random.Shared.Next(10_000_000, 99_999_999);
        const int authVersion = 7;
        var (token, expiresAt) = svc.IssueShopperToken(
            shopperId, phone, authVersion);

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
        jwt.Audiences.Should().Contain(JwtOptions.ShopperAudience);
        jwt.Claims.Should().Contain(c => c.Type == "sub" && c.Value == shopperId.ToString());
        jwt.Claims.Should().Contain(c => c.Type == "principal" && c.Value == "shopper");
        jwt.Claims.Should().Contain(c => c.Type == "phone" && c.Value == phone);
        jwt.Claims.Should().Contain(c =>
            c.Type == TenantClaims.ShopperAuthVersion && c.Value == authVersion.ToString());
        expiresAt.Should().BeAfter(DateTimeOffset.UtcNow.AddMinutes(25));
        expiresAt.Should().BeBefore(DateTimeOffset.UtcNow.AddMinutes(35));
    }
}
