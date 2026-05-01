namespace OrderDeck.LicenseServer.Services.Audit;

/// <summary>Phase 5a: AuditService eventType constants for backup-related actions.</summary>
public static class BackupAuditEvents
{
    public const string BackupCreated = "BackupCreated";
    public const string BackupDeleted = "BackupDeleted";
    public const string BackupAccessed = "BackupAccessed";
    public const string RestoreInitiated = "RestoreInitiated";

    public const string TargetType = "CustomerBackup";
}
