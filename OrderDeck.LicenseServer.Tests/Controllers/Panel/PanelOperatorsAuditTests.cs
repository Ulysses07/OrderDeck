using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Audit;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Panel;

/// <summary>
/// PR audit-log (2026-05-15): PanelOperatorsController.Invite/Delete'in
/// AuditLogEntry tablosuna OperatorInvited / OperatorDeleted event'leri
/// yazdığını doğrular. Owner attribution + targetId + details payload
/// kontrol edilir.
/// </summary>
public class PanelOperatorsAuditTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public PanelOperatorsAuditTests(ApiFactory factory) => _factory = factory;

    private static string DummyPassword() => "pwd-" + Guid.NewGuid().ToString("N");

    private sealed record OperatorDto(
        Guid Id, Guid LicenseId, string Email, string Name, string Role,
        DateTimeOffset CreatedAt, DateTimeOffset? LastLoginAt, DateTimeOffset? RevokedAt);

    private async Task<(HttpClient client, Guid customerId, Guid licenseId)> SeedAsync()
    {
        var (client, customerId, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);
        Guid licenseId;
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var license = new License
        {
            Id = Guid.NewGuid(),
            LicenseKey = "LDK-OPAUDIT-" + Guid.NewGuid().ToString("N"),
            CustomerId = customerId,
            SkuCode = "STD",
            ActivationSlots = 1,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30)
        };
        db.Licenses.Add(license);
        licenseId = license.Id;
        await db.SaveChangesAsync();
        return (client, customerId, licenseId);
    }

    [Fact]
    public async Task Invite_writes_OperatorInvited_audit_entry()
    {
        var (client, customerId, _) = await SeedAsync();

        var resp = await client.PostAsJsonAsync("/api/panel/operators", new
        {
            email = "ali.audit@example.com",
            name = "Ali Audit",
            password = DummyPassword()
        });
        resp.EnsureSuccessStatusCode();
        var created = await resp.Content.ReadFromJsonAsync<OperatorDto>();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var entry = await db.AuditLogs
            .OrderByDescending(a => a.OccurredAt)
            .FirstOrDefaultAsync(a => a.EventType == AuditEvents.OperatorInvited
                && a.TargetId == created!.Id.ToString());

        entry.Should().NotBeNull("Invite endpoint must write an audit row");
        entry!.AdminId.Should().Be(customerId);   // owner attribution
        entry.TargetType.Should().Be(AuditTargets.Operator);
        entry.Details.Should().Contain("ali.audit@example.com");
        entry.Details.Should().Contain("Ali Audit");
        entry.Details.Should().Contain("staff");
    }

    [Fact]
    public async Task Delete_writes_OperatorDeleted_audit_entry()
    {
        var (client, customerId, _) = await SeedAsync();

        // Önce bir operator yarat.
        var createResp = await client.PostAsJsonAsync("/api/panel/operators", new
        {
            email = "del.audit@example.com",
            name = "Del Audit",
            password = DummyPassword()
        });
        var created = await createResp.Content.ReadFromJsonAsync<OperatorDto>();

        // Sonra sil.
        var delResp = await client.DeleteAsync($"/api/panel/operators/{created!.Id}");
        delResp.EnsureSuccessStatusCode();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var entry = await db.AuditLogs
            .OrderByDescending(a => a.OccurredAt)
            .FirstOrDefaultAsync(a => a.EventType == AuditEvents.OperatorDeleted
                && a.TargetId == created.Id.ToString());

        entry.Should().NotBeNull("Delete endpoint must write an audit row");
        entry!.AdminId.Should().Be(customerId);
        entry.TargetType.Should().Be(AuditTargets.Operator);
        entry.Details.Should().Contain("del.audit@example.com");
    }

    [Fact]
    public async Task Invite_409_duplicate_does_not_double_log()
    {
        var (client, _, _) = await SeedAsync();

        await client.PostAsJsonAsync("/api/panel/operators", new
        {
            email = "dup.audit@example.com",
            name = "Dup",
            password = DummyPassword()
        });

        // İlk başarılı, ikinci 409 — sadece bir audit kaydı olmalı.
        await client.PostAsJsonAsync("/api/panel/operators", new
        {
            email = "dup.audit@example.com",
            name = "Dup2",
            password = DummyPassword()
        });

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var count = await db.AuditLogs
            .CountAsync(a => a.EventType == AuditEvents.OperatorInvited
                && a.Details!.Contains("dup.audit@example.com"));

        count.Should().Be(1, "duplicate email should reject without audit row");
    }

    [Fact]
    public async Task Delete_404_cross_tenant_does_not_log()
    {
        var (client1, _, _) = await SeedAsync();
        var (client2, _, _) = await SeedAsync();

        // Client1 bir operator yaratsın.
        var createResp = await client1.PostAsJsonAsync("/api/panel/operators", new
        {
            email = "cross.audit@example.com",
            name = "X",
            password = DummyPassword()
        });
        var created = await createResp.Content.ReadFromJsonAsync<OperatorDto>();

        // Client2 silmeye çalışsın → 404, audit log yazılmamalı.
        await client2.DeleteAsync($"/api/panel/operators/{created!.Id}");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var deleteEntries = await db.AuditLogs
            .CountAsync(a => a.EventType == AuditEvents.OperatorDeleted
                && a.TargetId == created.Id.ToString());

        deleteEntries.Should().Be(0, "404 cross-tenant should not produce audit row");
    }
}
