namespace OrderDeck.LicenseServer.Services.Audit;

public interface IAuditService
{
    /// <summary>Logs an event using HttpContext.User for admin identity. Used in handlers after auth.</summary>
    Task LogAsync(string eventType, string targetType, string? targetId, object? details = null, CancellationToken ct = default);

    /// <summary>Login flow — User claims not yet set; pass admin info explicitly.</summary>
    Task LogLoginAsync(Guid adminId, string username, string? ipAddress, CancellationToken ct = default);

    /// <summary>Logout flow — log before SignOutAsync clears claims.</summary>
    Task LogLogoutAsync(Guid adminId, string username, string? ipAddress, CancellationToken ct = default);
}
