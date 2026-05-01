namespace OrderDeck.LicenseServer.Services.Backup;

public sealed class BackupOptions
{
    /// <summary>Pre-Phase-5b single static key. Treated as version-0 in the
    /// new ring when present. New deployments should use <see cref="MasterKeys"/>
    /// + <see cref="ActiveKeyVersion"/> instead.</summary>
    public string MasterKeyHex { get; set; } = "";

    /// <summary>Phase 5b key ring. Map of versionNumber → 64-hex AES-256 key.
    /// Version 0 is reserved for the legacy unversioned envelope (rows whose
    /// CustomerBackup.KeyVersion == 0 — the migration default). Add a new
    /// version + bump <see cref="ActiveKeyVersion"/> to rotate going forward;
    /// keep ALL keys ever used in the ring as long as any blob references them.</summary>
    public Dictionary<int, string> MasterKeys { get; set; } = new();

    /// <summary>Which key version new uploads encrypt under. Must exist in
    /// <see cref="MasterKeys"/>. Default 0 means "use the legacy MasterKeyHex
    /// path" — picked so an existing deployment with only MasterKeyHex set
    /// keeps working bytewise-identically until the operator opts into v1.</summary>
    public int ActiveKeyVersion { get; set; } = 0;

    public string StorageRoot { get; set; } = "/app/Backups";
    public int MaxBlobSizeMb { get; set; } = 200;

    /// <summary>Per-customer cap on total stored (encrypted) backup bytes. Counted
    /// across all rows in CustomerBackups for that customer (active retention
    /// already prunes to last-5 + monthly milestones, so this is a belt-and-suspenders
    /// limit against unbounded growth). 0 disables the check.</summary>
    public long PerCustomerQuotaMb { get; set; } = 5_000;  // 5 GB default

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
