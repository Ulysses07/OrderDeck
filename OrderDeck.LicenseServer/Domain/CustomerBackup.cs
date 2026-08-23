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

    /// <summary>
    /// Zarf BİÇİMİ — anahtar sürümünden ayrı bir eksen.
    /// 0 = eski tek-atış AES-GCM zarfı (<see cref="KeyVersion"/> baştaki sürüm
    /// baytının olup olmadığını söyler), 2 = parçalı akış zarfı.
    ///
    /// <para><b>Neden ayrı bir sütun:</b> eski biçimde ilk bayt "anahtar sürümü"
    /// anlamına geliyordu, yani biçimi baytlardan okumak <c>KeyVersion == 2</c>
    /// olan bir kurulumda iki biçimi birbirinden ayıramazdı. Belirsizliğin bedeli
    /// tam olarak müşterinin tek felaket kurtarma kopyasını çözememek olurdu; bir
    /// sütun bundan ucuz. Diskteki sihirli imza (<c>ODB2</c>) yalnızca veritabanı
    /// satırı olmayan araçlar (restore tatbikatı, <c>RestoreVerify</c>) için var.</para>
    /// </summary>
    public int EnvelopeFormat { get; set; }
}
