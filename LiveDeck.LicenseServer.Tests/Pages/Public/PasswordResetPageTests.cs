using System.Net;
using FluentAssertions;
using LiveDeck.LicenseServer.Data;
using LiveDeck.LicenseServer.Domain;
using LiveDeck.LicenseServer.Services.Auth;
using LiveDeck.LicenseServer.Tests.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LiveDeck.LicenseServer.Tests.Pages.Public;

public sealed class PasswordResetPageTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public PasswordResetPageTests(ApiFactory factory) => _factory = factory;

    private async Task<Guid> SeedTokenAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<PasswordHasher>();
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            Email = $"prp-{Guid.NewGuid():N}@x",
            Name = "PRP",
            PasswordHash = hasher.Hash("old-password-12345"),
            CreatedAt = DateTimeOffset.UtcNow,
            EmailConfirmedAt = DateTimeOffset.UtcNow
        };
        var token = new PasswordResetToken
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.Customers.Add(customer);
        db.PasswordResetTokens.Add(token);
        await db.SaveChangesAsync();
        return token.Id;
    }

    [Fact]
    public async Task Get_with_token_returns_200_with_form()
    {
        var tokenId = await SeedTokenAsync();
        var client = _factory.CreateClient();

        var resp = await client.GetAsync($"/password-reset?token={tokenId}");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await resp.Content.ReadAsStringAsync();
        html.Should().Contain(tokenId.ToString());
        html.Should().Contain("Yeni şifre");
    }

    [Fact]
    public async Task Post_valid_token_completes_reset()
    {
        var tokenId = await SeedTokenAsync();
        var client = _factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            HandleCookies = true
        });

        var getResp = await client.GetAsync($"/password-reset?token={tokenId}");
        var antiForgery = AdminLoginHelper.ExtractAntiForgeryToken(await getResp.Content.ReadAsStringAsync());

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = antiForgery,
            ["Input.Token"] = tokenId.ToString(),
            ["Input.NewPassword"] = "new-password-12345",
            ["Input.ConfirmPassword"] = "new-password-12345"
        });
        var postResp = await client.PostAsync("/password-reset", form);

        postResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await postResp.Content.ReadAsStringAsync();
        body.Should().Contain("başarıyla güncellendi");

        // DB doğrula
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var token = await db.PasswordResetTokens.FirstAsync(t => t.Id == tokenId);
        token.UsedAt.Should().NotBeNull();
    }
}
