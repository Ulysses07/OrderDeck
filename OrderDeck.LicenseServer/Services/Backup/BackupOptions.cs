namespace OrderDeck.LicenseServer.Services.Backup;

public sealed class BackupOptions
{
    public string MasterKeyHex { get; set; } = "";
    public string StorageRoot { get; set; } = "/app/Backups";
    public int MaxBlobSizeMb { get; set; } = 200;
}
