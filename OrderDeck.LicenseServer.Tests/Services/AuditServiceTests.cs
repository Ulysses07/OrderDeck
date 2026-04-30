using System.Security.Claims;
using FluentAssertions;
using LiveDeck.LicenseServer.Data;
using LiveDeck.LicenseServer.Services.Audit;
using LiveDeck.LicenseServer.Tests.TestHelpers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LiveDeck.LicenseServer.Tests.Services;

public sealed class AuditServiceTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public AuditServiceTests(ApiFactory factory) => _factory = factory;

    private (LicenseDbContext db, AuditService svc, DefaultHttpContext httpContext) Build()
    {
        var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var ctx = new DefaultHttpContext();
        var accessor = new HttpContextAccessor { HttpContext = ctx };
        var svc = new AuditService(db, accessor);
        return (db, svc, ctx);
    }

    [Fact]
    public async Task LogAsync_creates_entry_with_user_claims()
    {
        var (db, svc, ctx) = Build();
        var adminId = Guid.NewGuid();
        ctx.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("sub", adminId.ToString()),
            new Claim("username", "alice")
        }, "AdminCookie"));
        ctx.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("10.0.0.5");

        await svc.LogAsync(AuditEvents.LicenseRevoke, AuditTargets.License, "LDK-XYZ",
            new { reason = "test" });

        var entry = db.AuditLogs.OrderByDescending(a => a.OccurredAt).First();
        entry.AdminId.Should().Be(adminId);
        entry.AdminUsername.Should().Be("alice");
        entry.EventType.Should().Be("license.revoke");
        entry.TargetType.Should().Be("license");
        entry.TargetId.Should().Be("LDK-XYZ");
        entry.Details.Should().Contain("test");
        entry.IpAddress.Should().Be("10.0.0.5");
    }

    [Fact]
    public async Task LogAsync_serializes_details_as_json()
    {
        var (db, svc, ctx) = Build();
        ctx.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("sub", Guid.NewGuid().ToString()),
            new Claim("username", "bob")
        }, "AdminCookie"));

        await svc.LogAsync(AuditEvents.LicenseExtend, AuditTargets.License, "LDK-EXT",
            new { additionalDays = 30 });

        var entry = db.AuditLogs.OrderByDescending(a => a.OccurredAt).First();
        entry.Details.Should().Contain("\"additionalDays\":30");
    }

    [Fact]
    public async Task LogAsync_with_null_details_writes_null_field()
    {
        var (db, svc, ctx) = Build();
        ctx.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("sub", Guid.NewGuid().ToString()),
            new Claim("username", "carol")
        }, "AdminCookie"));

        await svc.LogAsync(AuditEvents.CustomerConfirmEmail, AuditTargets.Customer, "cust-1");

        var entry = db.AuditLogs.OrderByDescending(a => a.OccurredAt).First();
        entry.Details.Should().BeNull();
    }

    [Fact]
    public async Task LogLoginAsync_writes_entry_without_user_claims()
    {
        var (db, svc, _) = Build();
        var adminId = Guid.NewGuid();

        await svc.LogLoginAsync(adminId, "dave", "192.168.1.1");

        var entry = db.AuditLogs.OrderByDescending(a => a.OccurredAt).First();
        entry.AdminId.Should().Be(adminId);
        entry.AdminUsername.Should().Be("dave");
        entry.EventType.Should().Be("admin.login");
        entry.TargetType.Should().Be("admin");
        entry.TargetId.Should().Be(adminId.ToString());
        entry.IpAddress.Should().Be("192.168.1.1");
    }
}
