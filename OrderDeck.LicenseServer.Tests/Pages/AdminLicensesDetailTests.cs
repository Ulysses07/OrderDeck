using System.Net;
using FluentAssertions;
using LiveDeck.LicenseServer.Data;
using LiveDeck.LicenseServer.Domain;
using LiveDeck.LicenseServer.Tests.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LiveDeck.LicenseServer.Tests.Pages;

public sealed class AdminLicensesDetailTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public AdminLicensesDetailTests(ApiFactory factory) => _factory = factory;

    private async Task<License> SeedLicenseAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var custId = Guid.NewGuid();
        db.Customers.Add(new Customer { Id = custId, Email = $"l-{Guid.NewGuid():N}@x", Name = "L", PasswordHash = "x", CreatedAt = DateTimeOffset.UtcNow });
        var lic = new License { Id = Guid.NewGuid(), LicenseKey = "LDK-DET-" + Guid.NewGuid().ToString("N"), CustomerId = custId, SkuCode = "STD", ActivationSlots = 1, IssuedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddDays(30) };
        db.Licenses.Add(lic);
        await db.SaveChangesAsync();
        return lic;
    }

    [Fact]
    public async Task Get_detail_returns_license_info()
    {
        var lic = await SeedLicenseAsync();
        var client = await _factory.CreateLoggedInAdminClientAsync($"admin-{Guid.NewGuid():N}");
        var resp = await client.GetAsync($"/admin/licenses/{lic.LicenseKey}");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var html = await resp.Content.ReadAsStringAsync();
        html.Should().Contain(lic.LicenseKey);
        html.Should().Contain("STD");
    }

    [Fact]
    public async Task Post_revoke_marks_license_revoked_and_writes_audit()
    {
        var lic = await SeedLicenseAsync();
        var client = await _factory.CreateLoggedInAdminClientAsync($"admin-{Guid.NewGuid():N}");

        var getResp = await client.GetAsync($"/admin/licenses/{lic.LicenseKey}");
        var token = AdminLoginHelper.ExtractAntiForgeryToken(await getResp.Content.ReadAsStringAsync());

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["RevokeForm.Reason"] = "Test iptal"
        });
        var postResp = await client.PostAsync($"/admin/licenses/{lic.LicenseKey}?handler=Revoke", form);
        postResp.StatusCode.Should().Be(HttpStatusCode.Redirect);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var updated = await db.Licenses.FirstAsync(l => l.LicenseKey == lic.LicenseKey);
        updated.RevokedAt.Should().NotBeNull();
        updated.RevokeReason.Should().Be("Test iptal");

        var audit = await db.AuditLogs
            .Where(a => a.EventType == "license.revoke" && a.TargetId == lic.LicenseKey)
            .FirstOrDefaultAsync();
        audit.Should().NotBeNull();
    }

    [Fact]
    public async Task Post_extend_updates_expiry_and_writes_audit()
    {
        var lic = await SeedLicenseAsync();
        var originalExpiry = lic.ExpiresAt;
        var client = await _factory.CreateLoggedInAdminClientAsync($"admin-{Guid.NewGuid():N}");

        var getResp = await client.GetAsync($"/admin/licenses/{lic.LicenseKey}");
        var token = AdminLoginHelper.ExtractAntiForgeryToken(await getResp.Content.ReadAsStringAsync());

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["ExtendForm.AdditionalDays"] = "60"
        });
        var postResp = await client.PostAsync($"/admin/licenses/{lic.LicenseKey}?handler=Extend", form);
        postResp.StatusCode.Should().Be(HttpStatusCode.Redirect);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var updated = await db.Licenses.FirstAsync(l => l.LicenseKey == lic.LicenseKey);
        updated.ExpiresAt.Should().BeCloseTo(originalExpiry.AddDays(60), TimeSpan.FromSeconds(2));

        var audit = await db.AuditLogs
            .Where(a => a.EventType == "license.extend" && a.TargetId == lic.LicenseKey)
            .FirstOrDefaultAsync();
        audit.Should().NotBeNull();
        audit!.Details.Should().Contain("60");
    }

    [Fact]
    public async Task Get_unknown_key_returns_404()
    {
        var client = await _factory.CreateLoggedInAdminClientAsync($"admin-{Guid.NewGuid():N}");
        var resp = await client.GetAsync("/admin/licenses/LDK-DOES-NOT-EXIST");
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
