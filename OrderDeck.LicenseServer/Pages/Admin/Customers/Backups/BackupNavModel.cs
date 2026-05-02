namespace OrderDeck.LicenseServer.Pages.Admin.Customers.Backups;

/// <summary>Lightweight model for the _BackupNav.cshtml partial. Each viewer
/// sub-page passes a fresh instance with its own active-tab marker.</summary>
public sealed record BackupNavModel(Guid CustomerId, Guid BackupId, string Active);
