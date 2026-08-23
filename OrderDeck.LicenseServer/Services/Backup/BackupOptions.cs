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

    /// <summary>
    /// Tek bir yedek gövdesinin üst sınırı.
    ///
    /// <para><b>Bu sayı artık bellekle bağlı DEĞİL.</b> Eskiden öyleydi: yükleme
    /// yolu blob'u akıtmıyor topluyordu ve istek başına elde düz metin + zarf,
    /// yani ~<b>2 × blob</b> duruyordu (tepe ≈ <c>MaxBlobSizeMb × 2 ×
    /// <see cref="MaxConcurrentUploads"/></c> = 256 MB, konteyner ise
    /// <c>mem_limit: 1g</c>). Yükleme ve indirme artık parçalı zarfla akıyor,
    /// tepe bellek parça boyutuna bağlı (~2 MiB/istek) ve blob büyüklüğünden
    /// bağımsız. Yani bu tavan bugün <b>diski ve kotayı</b> korumak için var,
    /// belleği değil.</para>
    ///
    /// <para>Ölçüm için: gerçek bir kurulumda <c>orderdeck.db</c> 4,9 MB,
    /// zip'i 1,6 MB. Ürün fotoğrafları yedeğe GİRMİYOR (DB'de yalnız R2 nesne
    /// anahtarı var, baytlar ayrı disk önbelleğinde) ve stok replikası her
    /// senkronda baştan yazıldığı için zamanla büyümüyor. Büyüyen tek şey
    /// geçmiş: Label / Customer / GiveawayParticipant / Payment / Shipment.</para>
    /// </summary>
    public int MaxBlobSizeMb { get; set; } = 64;

    /// <summary>
    /// Aynı anda gövdesi belleğe alınan yükleme sayısı.
    ///
    /// <para>Oran sınırı bu işi yapmıyor: <c>backup-upload</c> politikası
    /// müşteri kimliğine bölünmüş 6/saat, yani tek müşterinin sıklığını
    /// sınırlar — aynı anda kaç müşterinin yüklediğini değil. Bu kapı olmadan
    /// blob başına tavan koymak sunucu belleğine tavan koymuyor.</para>
    /// </summary>
    public int MaxConcurrentUploads { get; set; } = 2;

    /// <summary>
    /// Sıra açılmasını beklemenin üst sınırı; aşılırsa 503 + Retry-After.
    /// Sınırsız beklemek bellek baskısını açık bağlantı baskısına çevirmekten
    /// ibaret olurdu.
    /// </summary>
    public int UploadQueueWaitSeconds { get; set; } = 10;

    /// <summary>Per-customer cap on total stored (encrypted) backup bytes. Counted
    /// across all rows in CustomerBackups for that customer (active retention
    /// already prunes to last-5 + monthly milestones, so this is a belt-and-suspenders
    /// limit against unbounded growth). 0 disables the check.</summary>
    public long PerCustomerQuotaMb { get; set; } = 5_000;  // 5 GB default

    // Buradaki `S3` bölümü silindi. Saha dışı kopyalama artık VPS'te gecelik
    // cron ile yapılıyor (deploy/scripts/backup-blobs-to-r2.sh). Ayarın
    // kalması, "açılabilir" görünen ama R2'de çalışmayan bir tuzak olurdu.
}
