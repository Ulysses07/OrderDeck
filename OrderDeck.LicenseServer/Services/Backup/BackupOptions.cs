namespace OrderDeck.LicenseServer.Services.Backup;

public sealed class BackupOptions
{
    public string MasterKeyHex { get; set; } = "";
    public string StorageRoot { get; set; } = "/app/Backups";
    public int MaxBlobSizeMb { get; set; } = 200;

    /// <summary>S3-compatible off-host replication. Disabled when
    /// <see cref="S3BackupOptions.Enabled"/> is false (the default) so existing
    /// deployments keep working without provisioning a bucket. Tested against
    /// AWS S3 / Backblaze B2 / Wasabi / MinIO via the standard PutObject API.</summary>
    public S3BackupOptions S3 { get; set; } = new();
}

public sealed class S3BackupOptions
{
    public bool Enabled { get; set; } = false;
    /// <summary>Required when Enabled. e.g. "https://s3.us-west-001.backblazeb2.com"
    /// for B2, "https://s3.amazonaws.com" for AWS, "http://minio:9000" for local MinIO.</summary>
    public string ServiceUrl { get; set; } = "";
    public string AccessKey { get; set; } = "";
    public string SecretKey { get; set; } = "";
    public string Bucket { get; set; } = "";
    /// <summary>Optional path prefix inside the bucket (no leading slash).
    /// Useful when one bucket hosts multiple environments.</summary>
    public string Prefix { get; set; } = "orderdeck-backups/";
    /// <summary>If true, treat upload errors as warnings (log + continue).
    /// If false, propagate so the original POST /backups call also fails —
    /// gives stronger guarantees but ties HTTP latency to S3 availability.</summary>
    public bool BestEffort { get; set; } = true;
}
