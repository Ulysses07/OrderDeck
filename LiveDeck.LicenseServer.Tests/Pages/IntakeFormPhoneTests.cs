using System.Net;
using FluentAssertions;
using LiveDeck.LicenseServer.Data;
using LiveDeck.LicenseServer.Domain;
using LiveDeck.LicenseServer.Tests.TestHelpers;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LiveDeck.LicenseServer.Tests.Pages;

/// <summary>
/// Phase 4g Task 17 — Phone field validation + persistence on the intake form.
/// </summary>
public sealed class IntakeFormPhoneTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public IntakeFormPhoneTests(ApiFactory factory) => _factory = factory;

    private async Task<(string slug, Guid customerId)> SeedConfigAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            Email = $"if-{Guid.NewGuid():N}@x",
            Name = "If",
            PasswordHash = "x",
            CreatedAt = DateTimeOffset.UtcNow,
            EmailConfirmedAt = DateTimeOffset.UtcNow
        };
        db.Customers.Add(customer);
        db.Licenses.Add(new License
        {
            Id = Guid.NewGuid(),
            LicenseKey = "LDK-IFP-" + Guid.NewGuid().ToString("N"),
            CustomerId = customer.Id,
            SkuCode = "STD",
            ActivationSlots = 1,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30)
        });
        var slug = $"s-{Guid.NewGuid():N}"[..10];
        db.IntakeFormConfigs.Add(new IntakeFormConfig
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            Slug = slug,
            WhatsAppPhone = "+905551234567",
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        return (slug, customer.Id);
    }

    private static FormUrlEncodedContent BuildForm(string token, string slug, string? phone)
    {
        var dict = new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["Slug"] = slug,
            ["Input.Username"] = "bilalcanli",
            ["Input.FullName"] = "Bilal Canlı",
            ["Input.Address"] = "Atatürk Cad. No:12 İstanbul"
        };
        if (phone is not null) dict["Input.Phone"] = phone;
        return new FormUrlEncodedContent(dict);
    }

    [Fact]
    public async Task Post_submit_without_phone_returns_validation_error()
    {
        var (slug, _) = await SeedConfigAsync();
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });

        var getResp = await client.GetAsync($"/r/{slug}");
        var antiForgery = AdminLoginHelper.ExtractAntiForgeryToken(await getResp.Content.ReadAsStringAsync());

        var form = BuildForm(antiForgery, slug, phone: "");
        var postResp = await client.PostAsync($"/r/{slug}?handler=Submit", form);

        postResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = WebUtility.HtmlDecode(await postResp.Content.ReadAsStringAsync());
        html.Should().Contain("WhatsApp numarası zorunlu");
    }

    [Fact]
    public async Task Post_submit_with_invalid_phone_returns_normalization_error()
    {
        var (slug, _) = await SeedConfigAsync();
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });

        var getResp = await client.GetAsync($"/r/{slug}");
        var antiForgery = AdminLoginHelper.ExtractAntiForgeryToken(await getResp.Content.ReadAsStringAsync());

        var form = BuildForm(antiForgery, slug, phone: "abc");
        var postResp = await client.PostAsync($"/r/{slug}?handler=Submit", form);

        postResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = WebUtility.HtmlDecode(await postResp.Content.ReadAsStringAsync());
        html.Should().Contain("Geçersiz telefon numarası");
    }

    [Fact]
    public async Task Post_submit_with_valid_phone_persists_e164_and_redirects()
    {
        var (slug, customerId) = await SeedConfigAsync();
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });

        var getResp = await client.GetAsync($"/r/{slug}");
        var antiForgery = AdminLoginHelper.ExtractAntiForgeryToken(await getResp.Content.ReadAsStringAsync());

        var form = BuildForm(antiForgery, slug, phone: "5551234567");
        var postResp = await client.PostAsync($"/r/{slug}?handler=Submit", form);

        postResp.StatusCode.Should().Be(HttpStatusCode.Redirect);
        postResp.Headers.Location!.ToString().Should().StartWith("https://wa.me/905551234567?text=");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var sub = await db.IntakeFormSubmissions
            .Where(s => s.Config.CustomerId == customerId && s.Username == "bilalcanli")
            .FirstOrDefaultAsync();
        sub.Should().NotBeNull();
        sub!.Phone.Should().Be("+905551234567");
    }

    [Fact]
    public async Task Post_submit_redirect_url_contains_telefon_line_in_message()
    {
        var (slug, _) = await SeedConfigAsync();
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });

        var getResp = await client.GetAsync($"/r/{slug}");
        var antiForgery = AdminLoginHelper.ExtractAntiForgeryToken(await getResp.Content.ReadAsStringAsync());

        var form = BuildForm(antiForgery, slug, phone: "05551234567");
        var postResp = await client.PostAsync($"/r/{slug}?handler=Submit", form);

        postResp.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var url = postResp.Headers.Location!.ToString();
        var queryStart = url.IndexOf("?text=") + 6;
        var decoded = Uri.UnescapeDataString(url[queryStart..]);
        decoded.Should().Contain("Telefon: +905551234567");
    }
}
