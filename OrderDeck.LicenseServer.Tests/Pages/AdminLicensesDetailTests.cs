using System.Net;
using FluentAssertions;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Pages;

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
    public async Task Post_slots_updates_count_and_writes_audit()
    {
        var lic = await SeedLicenseAsync();
        var client = await _factory.CreateLoggedInAdminClientAsync($"admin-{Guid.NewGuid():N}");

        var getResp = await client.GetAsync($"/admin/licenses/{lic.LicenseKey}");
        var token = AdminLoginHelper.ExtractAntiForgeryToken(await getResp.Content.ReadAsStringAsync());

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["SlotsForm.Slots"] = "3"
        });
        var postResp = await client.PostAsync($"/admin/licenses/{lic.LicenseKey}?handler=Slots", form);
        postResp.StatusCode.Should().Be(HttpStatusCode.Redirect);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var updated = await db.Licenses.FirstAsync(l => l.LicenseKey == lic.LicenseKey);
        updated.ActivationSlots.Should().Be(3);

        var audit = await db.AuditLogs
            .Where(a => a.EventType == "license.slots-change" && a.TargetId == lic.LicenseKey)
            .FirstOrDefaultAsync();
        audit.Should().NotBeNull();
        audit!.Details.Should().Contain("3");
    }

    [Fact]
    public async Task Post_slots_below_active_activation_count_is_rejected()
    {
        // Slot'u aktif cihaz sayısının altına indirmek lisansı taahhüt fazlası
        // bırakır: mevcut cihazlar düşmez ama slot boşalana kadar yeni makine
        // giremez ve sebebi panelde görünmez. Önce aktivasyon iptal edilmeli.
        var lic = await SeedLicenseAsync();
        using (var seedScope = _factory.Services.CreateScope())
        {
            var seedDb = seedScope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var tracked = await seedDb.Licenses.FirstAsync(l => l.LicenseKey == lic.LicenseKey);
            tracked.ActivationSlots = 2;
            seedDb.Activations.AddRange(
                NewActivation(lic.Id, "hw-a"),
                NewActivation(lic.Id, "hw-b"));
            await seedDb.SaveChangesAsync();
        }

        var client = await _factory.CreateLoggedInAdminClientAsync($"admin-{Guid.NewGuid():N}");
        var getResp = await client.GetAsync($"/admin/licenses/{lic.LicenseKey}");
        var token = AdminLoginHelper.ExtractAntiForgeryToken(await getResp.Content.ReadAsStringAsync());

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["SlotsForm.Slots"] = "1"
        });
        var postResp = await client.PostAsync($"/admin/licenses/{lic.LicenseKey}?handler=Slots", form);
        postResp.StatusCode.Should().Be(HttpStatusCode.OK); // redirect yok → form hatayla döndü

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var updated = await db.Licenses.FirstAsync(l => l.LicenseKey == lic.LicenseKey);
        updated.ActivationSlots.Should().Be(2, "slot düşürülmemeli");
    }

    private static Activation NewActivation(Guid licenseId, string hw) => new()
    {
        Id = Guid.NewGuid(),
        LicenseId = licenseId,
        HardwareFingerprint = hw,
        MachineName = hw,
        ActivatedAt = DateTimeOffset.UtcNow,
        LastSeenAt = DateTimeOffset.UtcNow
    };

    [Fact]
    public async Task Get_unknown_key_returns_404()
    {
        var client = await _factory.CreateLoggedInAdminClientAsync($"admin-{Guid.NewGuid():N}");
        var resp = await client.GetAsync("/admin/licenses/LDK-DOES-NOT-EXIST");
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
