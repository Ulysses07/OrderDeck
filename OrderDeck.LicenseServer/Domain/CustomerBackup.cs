namespace OrderDeck.LicenseServer.Domain;

/// <summary>
/// Phase 5a: client-uploaded SQLite DB backup, AES-256-GCM encrypted at rest on server filesystem.
/// Retention: last 5 non-milestone + first-of-month milestones (preserved indefinitely).
/// </summary>
public sealed class CustomerBackup
{
    public Guid Id { get; set; }
    public Guid CustomerId { get; set; }
    public Customer Customer { get; set; } = null!;

    /// <summary>Absolute path of encrypted blob on server filesystem.</summary>
    public string BlobPath { get; set; } = "";

    /// <summary>Encrypted blob size on disk (includes 12B nonce + 16B auth tag overhead).</summary>
    public long SizeBytes { get; set; }

    /// <summary>SHA256 of plaintext zip (pre-encrypt) — for client integrity check on download.</summary>
    public string ChecksumSha256 { get; set; } = "";

    /// <summary>True if first backup of its calendar month — preserved across retention runs.</summary>
    public bool IsMonthlyMilestone { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public string? UserAgent { get; set; }
    public string? MachineName { get; set; }

    /// <summary>Phase 5b: which master key version this blob was encrypted under.
    /// 0 = pre-Phase-5b unversioned envelope (no version byte on disk).
    /// >=1 = Phase 5b versioned envelope (first byte is the key version).
    /// Stored in DB so the right key is selectable on decrypt; without this
    /// column the server couldn't tell two key generations apart.</summary>
    public int KeyVersion { get; set; }
}
