namespace OrderDeck.LicenseServer.Domain;

/// <summary>
/// Bir WPF yerel veritabanının (kopyanın) bu lisanstaki izi — yayın ve etiket
/// senkronu spec'i 2026-10-13 §3.1. Kopya kimliği (<see cref="ReplicaId"/>)
/// yerel veritabanı dosyasına bağlıdır (<c>_meta.ReplicaId</c>), ayar dosyasına
/// değil; yedekten geri yüklemede yenilenir (§8.7).
///
/// Okuyucular: sahip sessizliği (kalp atışı, §4.6), mod kapısı (§4.10),
/// kopya veritabanı tespiti (§8.7), açılış penceresi ve kip geçişi kanıtı
/// (§4.0, §8.4, §8.6).
///
/// Anahtar <b>lisans başına</b> (<see cref="LicenseId"/>, <see cref="ReplicaId"/>):
/// lisans değiştiren bilgisayar yeni lisansta yeni satır açar, eski lisanstaki
/// sessizlik denetimi bozulmaz.
/// </summary>
public sealed class WpfReplica
{
    public Guid LicenseId { get; set; }
    public License License { get; set; } = null!;

    public Guid ReplicaId { get; set; }

    public string? MachineName { get; set; }

    /// <summary>İstemcinin bildirdiği donanım parmak izi. Aynı ReplicaId'nin
    /// farklı parmak iziyle görünmesi disk klonudur (§8.7).</summary>
    public string? HardwareFingerprint { get; set; }

    /// <summary>User-Agent'tan okunan istemci sürümü (§4.0).</summary>
    public string? ClientVersion { get; set; }

    /// <summary>İsteğin protokol sürümü (v2 = 2).</summary>
    public byte Protocol { get; set; }

    /// <summary><c>hello</c>'daki yetenek düzeyi; mod kapısı 3 ister (§4.10-2).</summary>
    public byte FeatureLevel { get; set; }

    /// <summary>Kopyanın son v2 isteğinde bildirdiği, o anda uyguladığı kip (§2
    /// "Görülen kip"). NULL = henüz bildirmedi.</summary>
    public SalesSyncMode? SeenMode { get; set; }
    public DateTimeOffset? SeenModeAt { get; set; }

    /// <summary>Kopyanın <see cref="SalesSyncMode.On"/> bildirdiği ilk an (§4.0
    /// açılış penceresi).</summary>
    public DateTimeOffset? SeenModeOnAt { get; set; }

    /// <summary>Geri yüklenen kopyanın önceki kimliği. Doluysa kopya açılış
    /// penceresi sayılmaz (§4.0, §8.7).</summary>
    public Guid? PreviousReplicaId { get; set; }

    public DateTimeOffset FirstSeenAt { get; set; }

    /// <summary>Kalp atışı: her v2 isteğinde ilerler (§4.0).</summary>
    public DateTimeOffset LastSeenAt { get; set; }

    public DateTimeOffset? BackfillCompletedAt { get; set; }
    public DateTimeOffset? PaymentHistoryCompletedAt { get; set; }

    /// <summary>Geri doldurmadaki birim ayrışması sayıları (§4.3): yerelin
    /// kazandığı ve sunucunun kazandığı satırlar.</summary>
    public int BackfillDivergedLocal { get; set; }
    public int BackfillDivergedServer { get; set; }

    /// <summary>Kopyanın bildirdiği yerel kargo ayarı; lisans geneli ayar
    /// yazılmadan önce yan yana gösterilir (§6.2).</summary>
    public decimal? ReportedThreshold { get; set; }
    public decimal? ReportedFee { get; set; }
}
