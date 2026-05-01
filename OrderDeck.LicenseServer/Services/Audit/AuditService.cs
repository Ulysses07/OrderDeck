using System.Security.Claims;
using System.Text.Json;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using Microsoft.AspNetCore.Http;

namespace OrderDeck.LicenseServer.Services.Audit;

public sealed class AuditService : IAuditService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private readonly LicenseDbContext _db;
    private readonly IHttpContextAccessor _httpContext;

    public AuditService(LicenseDbContext db, IHttpContextAccessor httpContext)
    {
        _db = db;
        _httpContext = httpContext;
    }

    public Task LogAsync(string eventType, string targetType, string? targetId, object? details = null, CancellationToken ct = default)
    {
        var ctx = _httpContext.HttpContext
            ?? throw new InvalidOperationException("LogAsync requires HttpContext (use LogLoginAsync/LogLogoutAsync outside request scope).");

        var sub = ctx.User.FindFirst("sub")?.Value
            ?? ctx.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? throw new InvalidOperationException("Audit LogAsync requires authenticated User with 'sub' claim.");
        var username = ctx.User.FindFirst("username")?.Value
            ?? ctx.User.FindFirst(ClaimTypes.Name)?.Value
            ?? "(unknown)";
        var ip = ctx.Connection.RemoteIpAddress?.ToString();

        return WriteAsync(Guid.Parse(sub), username, eventType, targetType, targetId, details, ip, ct);
    }

    public Task LogLoginAsync(Guid adminId, string username, string? ipAddress, CancellationToken ct = default) =>
        WriteAsync(adminId, username, AuditEvents.AdminLogin, AuditTargets.Admin, adminId.ToString(), null, ipAddress, ct);

    public Task LogLogoutAsync(Guid adminId, string username, string? ipAddress, CancellationToken ct = default) =>
        WriteAsync(adminId, username, AuditEvents.AdminLogout, AuditTargets.Admin, adminId.ToString(), null, ipAddress, ct);

    public Task LogCustomerEventAsync(
        Guid customerId, string customerEmail,
        string eventType, string targetType, string? targetId,
        object? details, string? ipAddress, CancellationToken ct = default) =>
        WriteAsync(customerId, customerEmail, eventType, targetType, targetId, details, ipAddress, ct);

    private async Task WriteAsync(
        Guid adminId, string username,
        string eventType, string targetType, string? targetId,
        object? details, string? ip, CancellationToken ct)
    {
        var entry = new AuditLogEntry
        {
            Id = Guid.NewGuid(),
            OccurredAt = DateTimeOffset.UtcNow,
            AdminId = adminId,
            AdminUsername = username,
            EventType = eventType,
            TargetType = targetType,
            TargetId = targetId,
            Details = details is null ? null : JsonSerializer.Serialize(details, JsonOpts),
            IpAddress = ip
        };
        _db.AuditLogs.Add(entry);
        await _db.SaveChangesAsync(ct);
    }
}
