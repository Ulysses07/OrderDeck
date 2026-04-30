using System.Net;
using FluentAssertions;
using LiveDeck.LicenseServer.Data;
using LiveDeck.LicenseServer.Domain;
using LiveDeck.LicenseServer.Services.Email;
using LiveDeck.LicenseServer.Tests.TestHelpers;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LiveDeck.LicenseServer.Tests.Pages.Public;

public sealed class UnsubscribePageTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public UnsubscribePageTests(ApiFactory factory) => _factory = factory;

    private async Task<(Customer customer, string token)> SeedAsync(bool initiallyUnsubscribed = false)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var signer = scope.ServiceProvider.GetRequiredService<UnsubscribeTokenSigner>();
        var c = new Customer
        {
            Id = Guid.NewGuid(),
            Email = $"unsub-{Guid.NewGuid():N}@x",
            Name = "Unsub",
            PasswordHash = "x",
            CreatedAt = DateTimeOffset.UtcNow,
            EmailConfirmedAt = DateTimeOffset.UtcNow,
            Unsubscribed = initiallyUnsubscribed
        };
        db.Customers.Add(c);
        await db.SaveChangesAsync();
        var token = signer.Sign(c.Id, DateTimeOffset.UtcNow);
        return (c, token);
    }

    [Fact]
    public async Task Get_with_valid_token_renders_email_and_form()
    {
        var (customer, token) = await SeedAsync();
        var client = _factory.CreateClient();

        var resp = await client.GetAsync($"/unsubscribe?token={Uri.EscapeDataString(token)}");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await resp.Content.ReadAsStringAsync();
        html.Should().Contain(customer.Email);
        html.Should().Contain("Aboneliği durdur");
    }

    [Fact]
    public async Task Post_with_valid_token_sets_Unsubscribed_flag()
    {
        var (customer, token) = await SeedAsync();
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });

        var getResp = await client.GetAsync($"/unsubscribe?token={Uri.EscapeDataString(token)}");
        var antiForgery = AdminLoginHelper.ExtractAntiForgeryToken(await getResp.Content.ReadAsStringAsync());

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = antiForgery,
            ["Token"] = token
        });
        var postResp = await client.PostAsync("/unsubscribe", form);

        postResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await postResp.Content.ReadAsStringAsync();
        html.Should().Contain("Aboneliğiniz durduruldu");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var updated = await db.Customers.FirstAsync(c => c.Id == customer.Id);
        updated.Unsubscribed.Should().BeTrue();
    }

    [Fact]
    public async Task Get_with_already_unsubscribed_customer_shows_info_message()
    {
        var (customer, token) = await SeedAsync(initiallyUnsubscribed: true);
        var client = _factory.CreateClient();

        var resp = await client.GetAsync($"/unsubscribe?token={Uri.EscapeDataString(token)}");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await resp.Content.ReadAsStringAsync();
        html.Should().Contain("aboneliğiniz zaten kapalı");
    }

    [Fact]
    public async Task Get_with_invalid_token_shows_error_message()
    {
        var client = _factory.CreateClient();

        var resp = await client.GetAsync("/unsubscribe?token=invalid.tampered.token");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await resp.Content.ReadAsStringAsync();
        html.Should().Contain("Bu bağlantı geçersiz");
    }
}
