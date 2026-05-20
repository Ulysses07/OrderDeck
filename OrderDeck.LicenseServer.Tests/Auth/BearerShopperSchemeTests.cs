using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Services.Auth;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Auth;

public class BearerShopperSchemeTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public BearerShopperSchemeTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task BearerShopper_scheme_is_registered()
    {
        using var scope = _factory.Services.CreateScope();
        var provider = scope.ServiceProvider
            .GetRequiredService<IAuthenticationSchemeProvider>();

        var scheme = await provider.GetSchemeAsync("Bearer-Shopper");
        scheme.Should().NotBeNull("Bearer-Shopper scheme Program.cs'te tescil edilmiş olmalı");
    }

    [Fact]
    public void BearerShopper_token_validation_uses_ShopperAudience()
    {
        using var scope = _factory.Services.CreateScope();
        var opts = scope.ServiceProvider
            .GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get("Bearer-Shopper");

        opts.TokenValidationParameters.ValidAudience
            .Should().Be(JwtOptions.ShopperAudience);
        opts.TokenValidationParameters.ValidateAudience.Should().BeTrue();
        opts.TokenValidationParameters.ValidateIssuer.Should().BeTrue();
        opts.TokenValidationParameters.ValidateLifetime.Should().BeTrue();
    }
}
