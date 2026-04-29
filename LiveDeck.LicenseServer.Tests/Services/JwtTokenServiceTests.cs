using System.IdentityModel.Tokens.Jwt;
using FluentAssertions;
using LiveDeck.LicenseServer.Services.Auth;
using Microsoft.Extensions.Options;
using Xunit;

namespace LiveDeck.LicenseServer.Tests.Services;

public class JwtTokenServiceTests
{
    private readonly JwtTokenService _service;

    public JwtTokenServiceTests()
    {
        var options = Options.Create(new JwtOptions
        {
            SecretKey = "test-secret-key-must-be-at-least-32-bytes-long-for-hs256",
            Issuer = "livedeck-license-server"
        });
        _service = new JwtTokenService(options);
    }

    [Fact]
    public void IssueCustomerToken_includes_sub_email_and_audience()
    {
        var customerId = Guid.NewGuid();
        var (token, expiresAt) = _service.IssueCustomerToken(customerId, "user@example.com");

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
        jwt.Audiences.Should().ContainSingle().Which.Should().Be("livedeck-customer");
        jwt.Issuer.Should().Be("livedeck-license-server");
        jwt.Claims.Should().ContainSingle(c => c.Type == "sub" && c.Value == customerId.ToString());
        jwt.Claims.Should().ContainSingle(c => c.Type == "email" && c.Value == "user@example.com");
        expiresAt.Should().BeCloseTo(DateTimeOffset.UtcNow.AddDays(7), TimeSpan.FromMinutes(1));
    }

    [Fact]
    public void IssueAdminToken_uses_admin_audience_and_one_hour_expiry()
    {
        var adminId = Guid.NewGuid();
        var (token, expiresAt) = _service.IssueAdminToken(adminId, "admin");

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
        jwt.Audiences.Should().ContainSingle().Which.Should().Be("livedeck-admin");
        jwt.Claims.Should().ContainSingle(c => c.Type == "sub" && c.Value == adminId.ToString());
        jwt.Claims.Should().ContainSingle(c => c.Type == "username" && c.Value == "admin");
        expiresAt.Should().BeCloseTo(DateTimeOffset.UtcNow.AddHours(1), TimeSpan.FromMinutes(1));
    }
}
