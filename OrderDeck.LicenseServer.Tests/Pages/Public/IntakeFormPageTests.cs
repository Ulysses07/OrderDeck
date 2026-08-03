using System.Net;
using FluentAssertions;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Pages.Public;

public sealed class IntakeFormPageTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public IntakeFormPageTests(ApiFactory factory) => _factory = factory;

    private async Task<(string slug, Guid customerId)> SeedConfigAsync(
        bool licenseActive = true, bool formActive = true)
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
        if (licenseActive)
        {
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
        }
        var slug = $"s-{Guid.NewGuid():N}"[..10];
        db.IntakeFormConfigs.Add(new IntakeFormConfig
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            Slug = slug,
            WhatsAppPhone = "+905551234567",
            IsActive = formActive,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        return (slug, customer.Id);
    }

    [Fact]
    public async Task Get_form_page_returns_200_with_form_when_active()
    {
        var (slug, _) = await SeedConfigAsync();
        var client = _factory.CreateClient();

        var resp = await client.GetAsync($"/r/{slug}");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await resp.Content.ReadAsStringAsync();
        html.Should().Contain("Instagram");
        html.Should().Contain("E-posta");
        html.Should().Contain("Tamamla");
    }

    [Fact]
    public async Task Get_form_page_returns_410_when_form_inactive()
    {
        var (slug, _) = await SeedConfigAsync(formActive: false);
        var client = _factory.CreateClient();

        var resp = await client.GetAsync($"/r/{slug}");

        resp.StatusCode.Should().Be(HttpStatusCode.Gone);
    }

    [Fact]
    public async Task Get_form_page_returns_410_when_license_expired()
    {
        var (slug, _) = await SeedConfigAsync(licenseActive: false);
        var client = _factory.CreateClient();

        var resp = await client.GetAsync($"/r/{slug}");

        resp.StatusCode.Should().Be(HttpStatusCode.Gone);
    }

    [Fact]
    public async Task Post_submit_with_valid_input_redirects_to_wa_me()
    {
        var (slug, customerId) = await SeedConfigAsync();
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });

        // Anti-forgery token
        var getResp = await client.GetAsync($"/r/{slug}");
        var antiForgery = AdminLoginHelper.ExtractAntiForgeryToken(await getResp.Content.ReadAsStringAsync());

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = antiForgery,
            ["Slug"] = slug,
            ["Input.InstagramUsername"] = "bilalcanli",
            ["Input.FullName"] = "Bilal Canlı",
            ["Input.Email"] = "bilal@example.com",
            ["Input.Address"] = "Atatürk Cad. No:12",
            ["Input.City"] = "İstanbul",
            ["Input.District"] = "Kadıköy",
            ["Input.Phone"] = "5551234567"
        });
        var postResp = await client.PostAsync($"/r/{slug}?handler=Submit", form);

        postResp.StatusCode.Should().Be(HttpStatusCode.Redirect);
        postResp.Headers.Location!.ToString().Should().StartWith("https://wa.me/905551234567?text=");

        // Submission persisted?
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var sub = await db.IntakeFormSubmissions
            .Where(s => s.Config.CustomerId == customerId && s.Username == "bilalcanli")
            .FirstOrDefaultAsync();
        sub.Should().NotBeNull();
        sub!.City.Should().Be("İstanbul");
        sub.District.Should().Be("Kadıköy");
        sub.Address.Should().Be("Atatürk Cad. No:12");
    }

    [Fact]
    public async Task Post_submit_honeypot_filled_silently_returns_200_and_does_not_persist()
    {
        var (slug, customerId) = await SeedConfigAsync();
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });

        var getResp = await client.GetAsync($"/r/{slug}");
        var antiForgery = AdminLoginHelper.ExtractAntiForgeryToken(await getResp.Content.ReadAsStringAsync());

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = antiForgery,
            ["Slug"] = slug,
            ["Input.Username"] = "bot",
            ["Input.FullName"] = "Bot Bot",
            ["Input.Address"] = "spam",
            ["website"] = "http://bot-spam.example"
        });
        var postResp = await client.PostAsync($"/r/{slug}?handler=Submit", form);

        // Silent: 200, NOT redirect
        postResp.StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var sub = await db.IntakeFormSubmissions
            .Where(s => s.Config.CustomerId == customerId && s.Username == "bot")
            .FirstOrDefaultAsync();
        sub.Should().BeNull();
    }

    [Fact]
    public async Task Post_submit_with_no_platform_username_returns_page_with_error()
    {
        var (slug, _) = await SeedConfigAsync();
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = true
        });

        var getResp = await client.GetAsync($"/r/{slug}");
        var antiForgery = AdminLoginHelper.ExtractAntiForgeryToken(await getResp.Content.ReadAsStringAsync());

        // All platform usernames empty → "at least one" validation error.
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = antiForgery,
            ["Slug"] = slug,
            ["Input.FullName"] = "Bilal",
            ["Input.Email"] = "bilal@example.com",
            ["Input.Address"] = "Adres",
            ["Input.Phone"] = "5551234567"
        });
        var postResp = await client.PostAsync($"/r/{slug}?handler=Submit", form);

        // ModelState invalid → return Page() (200 with errors), Razor convention
        postResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = System.Net.WebUtility.HtmlDecode(await postResp.Content.ReadAsStringAsync());
        html.Should().Contain("En az bir platform");
    }
}
