using System.Net;
using FluentAssertions;
using LiveDeck.LicenseServer.Data;
using LiveDeck.LicenseServer.Domain;
using LiveDeck.LicenseServer.Tests.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LiveDeck.LicenseServer.Tests.Pages;

public sealed class AdminActivationsPageTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public AdminActivationsPageTests(ApiFactory factory) => _factory = factory;

    private async Task<(License lic, Activation act)> SeedAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var custId = Guid.NewGuid();
        db.Customers.Add(new Customer { Id = custId, Email = $"a-{Guid.NewGuid():N}@x", Name = "A", PasswordHash = "x", CreatedAt = DateTimeOffset.UtcNow });
        var lic = new License { Id = Guid.NewGuid(), LicenseKey = "LDK-ACT-" + Guid.NewGuid().ToString("N"), CustomerId = custId, SkuCode = "STD", ActivationSlots = 1, IssuedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddDays(30) };
        var act = new Activation { Id = Guid.NewGuid(), LicenseId = lic.Id, HardwareFingerprint = "fp-test", MachineName = "PC-1", ActivatedAt = DateTimeOffset.UtcNow, LastSeenAt = DateTimeOffset.UtcNow };
        db.Licenses.Add(lic);
        db.Activations.Add(act);
        await db.SaveChangesAsync();
        return (lic, act);
    }

    [Fact]
    public async Task Get_lists_activations_for_license()
    {
        var (lic, act) = await SeedAsync();
        var client = await _factory.CreateLoggedInAdminClientAsync($"admin-{Guid.NewGuid():N}");
        var resp = await client.GetAsync($"/admin/activations?licenseKey={lic.LicenseKey}");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var html = await resp.Content.ReadAsStringAsync();
        html.Should().Contain("PC-1");
        html.Should().Contain("fp-test");
    }

    [Fact]
    public async Task Post_force_deactivate_sets_DeactivatedAt_and_audit()
    {
        var (lic, act) = await SeedAsync();
        var client = await _factory.CreateLoggedInAdminClientAsync($"admin-{Guid.NewGuid():N}");

        var getResp = await client.GetAsync($"/admin/activations?licenseKey={lic.LicenseKey}");
        var token = AdminLoginHelper.ExtractAntiForgeryToken(await getResp.Content.ReadAsStringAsync());

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token
        });
        var postResp = await client.PostAsync($"/admin/activations?handler=Deactivate&licenseKey={lic.LicenseKey}&id={act.Id}", form);
        postResp.StatusCode.Should().Be(HttpStatusCode.Redirect);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var updated = await db.Activations.FirstAsync(a => a.Id == act.Id);
        updated.DeactivatedAt.Should().NotBeNull();

        var audit = await db.AuditLogs
            .Where(a => a.EventType == "activation.force-deactivate" && a.TargetId == act.Id.ToString())
            .FirstOrDefaultAsync();
        audit.Should().NotBeNull();
    }
}
