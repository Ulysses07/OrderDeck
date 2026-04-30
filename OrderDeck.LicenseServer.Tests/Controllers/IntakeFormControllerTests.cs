using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using LiveDeck.LicenseServer.Data;
using LiveDeck.LicenseServer.Domain;
using LiveDeck.LicenseServer.Tests.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LiveDeck.LicenseServer.Tests.Controllers;

public sealed class IntakeFormControllerTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public IntakeFormControllerTests(ApiFactory factory) => _factory = factory;

    private async Task<(HttpClient client, Guid customerId)> CreateAuthedClientAsync()
    {
        var client = _factory.CreateClient();
        var email = $"ifc-{Guid.NewGuid():N}@x";
        await client.PostAsJsonAsync("/api/v1/auth/register",
            new { email, name = "IFC", password = "secret-password" });

        Guid tokenId, customerId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var customer = await db.Customers.FirstAsync(c => c.Email == email);
            customerId = customer.Id;
            var token = await db.EmailConfirmationTokens
                .Where(t => t.CustomerId == customerId).FirstAsync();
            tokenId = token.Token;

            // Aktif lisans seed
            db.Licenses.Add(new License
            {
                Id = Guid.NewGuid(),
                LicenseKey = "LDK-IFC-" + Guid.NewGuid().ToString("N"),
                CustomerId = customerId,
                SkuCode = "STD",
                ActivationSlots = 1,
                IssuedAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(30)
            });
            await db.SaveChangesAsync();
        }
        await client.GetAsync($"/api/v1/auth/confirm-email/{tokenId}");

        var loginResp = await client.PostAsJsonAsync("/api/v1/auth/login",
            new { email, password = "secret-password" });
        var login = await loginResp.Content.ReadFromJsonAsync<LoginBody>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login!.Token);
        return (client, customerId);
    }

    [Fact]
    public async Task Get_intake_form_returns_404_when_not_configured()
    {
        var (client, _) = await CreateAuthedClientAsync();
        var resp = await client.GetAsync("/api/v1/me/intake-form");
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Put_intake_form_creates_config_and_returns_200()
    {
        var (client, _) = await CreateAuthedClientAsync();
        var slug = $"s-{Guid.NewGuid():N}"[..10];

        var resp = await client.PutAsJsonAsync("/api/v1/me/intake-form", new
        {
            slug,
            whatsAppPhone = "+905551234567",
            customTitle = "Test Form",
            isActive = true
        });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<IntakeFormBody>();
        body!.slug.Should().Be(slug);
        body.formUrl.Should().EndWith($"/r/{slug}");
    }

    [Fact]
    public async Task Get_intake_form_returns_200_after_put()
    {
        var (client, _) = await CreateAuthedClientAsync();
        var slug = $"s-{Guid.NewGuid():N}"[..10];

        await client.PutAsJsonAsync("/api/v1/me/intake-form", new
        {
            slug, whatsAppPhone = "+905551234567", isActive = true
        });

        var resp = await client.GetAsync("/api/v1/me/intake-form");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<IntakeFormBody>();
        body!.slug.Should().Be(slug);
    }

    [Fact]
    public async Task Put_intake_form_returns_400_for_invalid_slug()
    {
        var (client, _) = await CreateAuthedClientAsync();
        var resp = await client.PutAsJsonAsync("/api/v1/me/intake-form", new
        {
            slug = "ADMIN", whatsAppPhone = "+905551234567"
        });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Put_intake_form_returns_400_for_invalid_phone()
    {
        var (client, _) = await CreateAuthedClientAsync();
        var resp = await client.PutAsJsonAsync("/api/v1/me/intake-form", new
        {
            slug = $"s-{Guid.NewGuid():N}"[..10], whatsAppPhone = "abc"
        });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Put_intake_form_returns_409_for_slug_taken_by_another_customer()
    {
        var (client1, _) = await CreateAuthedClientAsync();
        var (client2, _) = await CreateAuthedClientAsync();
        var slug = $"s-{Guid.NewGuid():N}"[..10];

        var first = await client1.PutAsJsonAsync("/api/v1/me/intake-form", new
        {
            slug, whatsAppPhone = "+905551111111"
        });
        first.StatusCode.Should().Be(HttpStatusCode.OK);

        var second = await client2.PutAsJsonAsync("/api/v1/me/intake-form", new
        {
            slug, whatsAppPhone = "+905552222222"
        });
        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Get_form_submissions_returns_empty_array_initially_then_includes_new_after_seed()
    {
        var (client, customerId) = await CreateAuthedClientAsync();
        var slug = $"s-{Guid.NewGuid():N}"[..10];
        await client.PutAsJsonAsync("/api/v1/me/intake-form",
            new { slug, whatsAppPhone = "+905551234567" });

        // Initial: empty
        var resp1 = await client.GetAsync("/api/v1/me/form-submissions");
        resp1.StatusCode.Should().Be(HttpStatusCode.OK);
        var rows1 = await resp1.Content.ReadFromJsonAsync<List<SubmissionBody>>();
        rows1!.Count.Should().Be(0);

        // Seed submission
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var cfg = await db.IntakeFormConfigs.FirstAsync(c => c.CustomerId == customerId);
            db.IntakeFormSubmissions.Add(new IntakeFormSubmission
            {
                Id = Guid.NewGuid(),
                IntakeFormConfigId = cfg.Id,
                Username = "uname",
                FullName = "Full Name",
                Address = "Test Address",
                SubmittedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var resp2 = await client.GetAsync("/api/v1/me/form-submissions");
        var rows2 = await resp2.Content.ReadFromJsonAsync<List<SubmissionBody>>();
        rows2!.Count.Should().Be(1);
        rows2[0].username.Should().Be("uname");
    }

    private sealed record LoginBody(string Token, DateTimeOffset ExpiresAt);
    private sealed record IntakeFormBody(string slug, string whatsAppPhone, string? customTitle, bool isActive, string formUrl);
    private sealed record SubmissionBody(Guid id, string username, string fullName, string address, DateTimeOffset submittedAt);
}
