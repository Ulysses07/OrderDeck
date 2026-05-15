using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers;

/// <summary>
/// WhatsApp template sync (2026-05-15): WPF PUT + Mobile GET endpoint'leri.
/// Upsert pattern, tenant izolasyonu, validation kontrolleri.
/// </summary>
public class WhatsAppTemplatesTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public WhatsAppTemplatesTests(ApiFactory factory) => _factory = factory;

    private sealed record TemplatesDto(
        string PaymentTemplate, string ShippingWonTemplate, DateTimeOffset UpdatedAt);

    private async Task<(HttpClient client, Guid licenseId)> SeedAsync()
    {
        var (client, customerId, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);
        Guid licenseId;
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var license = new License
        {
            Id = Guid.NewGuid(),
            LicenseKey = "LDK-WA-" + Guid.NewGuid().ToString("N"),
            CustomerId = customerId,
            SkuCode = "STD",
            ActivationSlots = 1,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30)
        };
        db.Licenses.Add(license);
        licenseId = license.Id;
        await db.SaveChangesAsync();
        return (client, licenseId);
    }

    [Fact]
    public async Task Put_creates_new_template_row()
    {
        var (client, licenseId) = await SeedAsync();

        var resp = await client.PutAsJsonAsync(
            $"/api/v1/licenses/{licenseId}/whatsapp-templates",
            new { paymentTemplate = "Test ödeme {ad}", shippingWonTemplate = "Test kargo {ad}" });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await resp.Content.ReadFromJsonAsync<TemplatesDto>();
        body!.PaymentTemplate.Should().Be("Test ödeme {ad}");
        body.ShippingWonTemplate.Should().Be("Test kargo {ad}");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var count = await db.WhatsAppTemplateSettings.CountAsync(s => s.LicenseId == licenseId);
        count.Should().Be(1);
    }

    [Fact]
    public async Task Put_upserts_existing_template_row()
    {
        var (client, licenseId) = await SeedAsync();

        await client.PutAsJsonAsync(
            $"/api/v1/licenses/{licenseId}/whatsapp-templates",
            new { paymentTemplate = "v1", shippingWonTemplate = "v1-kargo" });
        await client.PutAsJsonAsync(
            $"/api/v1/licenses/{licenseId}/whatsapp-templates",
            new { paymentTemplate = "v2", shippingWonTemplate = "v2-kargo" });

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var rows = await db.WhatsAppTemplateSettings.Where(s => s.LicenseId == licenseId).ToListAsync();
        rows.Should().HaveCount(1, "PUT iki kez çağrılırsa upsert, ikinci satır olmaz");
        rows[0].PaymentTemplate.Should().Be("v2");
        rows[0].ShippingWonTemplate.Should().Be("v2-kargo");
    }

    [Fact]
    public async Task Put_400_empty_payment_template()
    {
        var (client, licenseId) = await SeedAsync();
        var resp = await client.PutAsJsonAsync(
            $"/api/v1/licenses/{licenseId}/whatsapp-templates",
            new { paymentTemplate = "", shippingWonTemplate = "x" });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Put_400_template_over_2000_chars()
    {
        var (client, licenseId) = await SeedAsync();
        var resp = await client.PutAsJsonAsync(
            $"/api/v1/licenses/{licenseId}/whatsapp-templates",
            new { paymentTemplate = new string('x', 2001), shippingWonTemplate = "ok" });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Put_404_cross_tenant_license()
    {
        var (client1, _) = await SeedAsync();
        var (_, otherLicenseId) = await SeedAsync();

        var resp = await client1.PutAsJsonAsync(
            $"/api/v1/licenses/{otherLicenseId}/whatsapp-templates",
            new { paymentTemplate = "x", shippingWonTemplate = "y" });
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Get_license_scoped_returns_template()
    {
        var (client, licenseId) = await SeedAsync();
        await client.PutAsJsonAsync(
            $"/api/v1/licenses/{licenseId}/whatsapp-templates",
            new { paymentTemplate = "fetched", shippingWonTemplate = "fetched-k" });

        var resp = await client.GetAsync($"/api/v1/licenses/{licenseId}/whatsapp-templates");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<TemplatesDto>();
        body!.PaymentTemplate.Should().Be("fetched");
    }

    [Fact]
    public async Task Get_license_scoped_204_when_no_row_yet()
    {
        var (client, licenseId) = await SeedAsync();
        var resp = await client.GetAsync($"/api/v1/licenses/{licenseId}/whatsapp-templates");
        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Get_panel_returns_template_for_first_active_license()
    {
        var (client, licenseId) = await SeedAsync();
        await client.PutAsJsonAsync(
            $"/api/v1/licenses/{licenseId}/whatsapp-templates",
            new { paymentTemplate = "panel-fetch", shippingWonTemplate = "panel-k" });

        var resp = await client.GetAsync("/api/panel/whatsapp-templates");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<TemplatesDto>();
        body!.PaymentTemplate.Should().Be("panel-fetch");
        body.ShippingWonTemplate.Should().Be("panel-k");
    }

    [Fact]
    public async Task Get_panel_204_when_no_template_yet()
    {
        var (client, _) = await SeedAsync();
        var resp = await client.GetAsync("/api/panel/whatsapp-templates");
        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Get_panel_isolates_tenants()
    {
        var (client1, license1) = await SeedAsync();
        await client1.PutAsJsonAsync(
            $"/api/v1/licenses/{license1}/whatsapp-templates",
            new { paymentTemplate = "tenant1", shippingWonTemplate = "tenant1-k" });

        // Diğer tenant kendi panel'inde tenant1'in template'ini görmemeli.
        var (client2, _) = await SeedAsync();
        var resp = await client2.GetAsync("/api/panel/whatsapp-templates");
        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }
}
