using System.Net;
using AngleSharp;
using FluentAssertions;
using LiveDeck.LicenseServer.Data;
using LiveDeck.LicenseServer.Domain;
using LiveDeck.LicenseServer.Tests.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LiveDeck.LicenseServer.Tests.Pages;

public sealed class AdminAuditPageTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public AdminAuditPageTests(ApiFactory factory) => _factory = factory;

    private async Task SeedAuditAsync(string adminUsername, string eventType, DateTimeOffset when)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        db.AuditLogs.Add(new AuditLogEntry
        {
            Id = Guid.NewGuid(),
            OccurredAt = when,
            AdminId = Guid.NewGuid(),
            AdminUsername = adminUsername,
            EventType = eventType,
            TargetType = "license",
            TargetId = "LDK-TEST",
            Details = null,
            IpAddress = "127.0.0.1"
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Index_renders_with_default_filter()
    {
        await SeedAuditAsync($"u-{Guid.NewGuid():N}", "license.issue", DateTimeOffset.UtcNow.AddHours(-1));

        var client = await _factory.CreateLoggedInAdminClientAsync($"admin-{Guid.NewGuid():N}");
        var resp = await client.GetAsync("/admin/audit");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var html = await resp.Content.ReadAsStringAsync();
        html.Should().Contain("license.issue");
    }

    [Fact]
    public async Task Index_eventType_filter_narrows_results()
    {
        var u1 = $"u-{Guid.NewGuid():N}";
        var u2 = $"u-{Guid.NewGuid():N}";
        await SeedAuditAsync(u1, "license.revoke", DateTimeOffset.UtcNow.AddHours(-1));
        await SeedAuditAsync(u2, "license.extend", DateTimeOffset.UtcNow.AddHours(-1));

        var client = await _factory.CreateLoggedInAdminClientAsync($"admin-{Guid.NewGuid():N}");
        var resp = await client.GetAsync("/admin/audit?eventType=license.revoke");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var html = await resp.Content.ReadAsStringAsync();
        var doc = await BrowsingContext.New(Configuration.Default).OpenAsync(req => req.Content(html));
        var rows = doc.QuerySelectorAll("table[data-table='audit'] tbody tr");
        // All rows should have only license.revoke entries
        foreach (var row in rows)
        {
            row.TextContent.Should().Contain("license.revoke");
            row.TextContent.Should().NotContain("license.extend");
        }
    }

    [Fact]
    public async Task Index_date_range_filter_excludes_old_entries()
    {
        var oldUser = $"old-{Guid.NewGuid():N}";
        var newUser = $"new-{Guid.NewGuid():N}";
        await SeedAuditAsync(oldUser, "license.issue", DateTimeOffset.UtcNow.AddDays(-30));
        await SeedAuditAsync(newUser, "license.issue", DateTimeOffset.UtcNow.AddHours(-1));

        var client = await _factory.CreateLoggedInAdminClientAsync($"admin-{Guid.NewGuid():N}");
        var resp = await client.GetAsync("/admin/audit");   // default: last 7 days
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var html = await resp.Content.ReadAsStringAsync();
        html.Should().Contain(newUser);
        html.Should().NotContain(oldUser);
    }
}
