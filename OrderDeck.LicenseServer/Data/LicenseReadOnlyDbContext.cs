namespace OrderDeck.LicenseServer.Data;

/// <summary>
/// Read-only twin of <see cref="LicenseDbContext"/>. Different connection
/// string (ConnectionStrings:LicenseDbReadOnly) so admin list / detail pages
/// can be routed to a SQL Server AlwaysOn read replica without affecting
/// any write paths.
///
/// Falls back to the primary <see cref="LicenseDbContext"/> connection when
/// the read-only connection string is not configured — code stays single-VPS-
/// safe today and HA-ready when the operator stands up a replica.
///
/// IMPORTANT: All queries through this context MUST be AsNoTracking(). EF Core
/// will happily issue tracking queries against a read-only replica and then
/// throw on SaveChanges (which is good — that's the safety net), but the
/// convention is enforced for clarity and to avoid the wasted change-tracker
/// memory per request.
/// </summary>
public sealed class LicenseReadOnlyDbContext : LicenseDbContext
{
    public LicenseReadOnlyDbContext(Microsoft.EntityFrameworkCore.DbContextOptions<LicenseDbContext> options)
        : base(options)
    {
        // ChangeTracker disabled per-context: queries will throw if anyone
        // accidentally tries to track an entity here.
        ChangeTracker.QueryTrackingBehavior = Microsoft.EntityFrameworkCore.QueryTrackingBehavior.NoTracking;
        ChangeTracker.AutoDetectChangesEnabled = false;
    }
}
