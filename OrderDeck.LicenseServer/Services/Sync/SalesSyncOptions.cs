namespace OrderDeck.LicenseServer.Services.Sync;

/// <summary>
/// Yayın ve etiket senkronunun sunucu ayarları — <c>SalesSync</c> bölümü (spec 2026-10-13 §2, §9, §11). Prod'da
/// compose ortamından gelir (<c>SalesSync__MinClientVersion</c>).
/// </summary>
public sealed class SalesSyncOptions
{
    public const string Section = "SalesSync";

    /// <summary>
    /// Ws'nin (bu spec'in WPF değişikliklerini taşıyan ilk sürüm) numarası; Ws yayımlanınca Burak yazar (§8.1
    /// adım 5). Boş = tanımsız: mod açılamaz, mod On'da komut ve kira uçları 409 <c>client-update-required</c>
    /// döner (kapalı-hata, §2 "Ws"). Ayrıştırılamayan değer de tanımsız sayılır; bkz.
    /// <see cref="ParseMinClientVersion"/>.
    /// </summary>
    public string? MinClientVersion { get; set; }

    /// <summary>
    /// Silinen müşterinin sipariş metinlerinin (görünen ad, yorum) kalıcı olarak boşaltılması. Varsayılan KAPALI:
    /// geri dönüşsüz olduğu için açılması sahip kararıdır (§9, §11 açık kalanlar). Kapalıyken de akış okuma anında
    /// maskeler.
    /// </summary>
    public bool ScrubOrderTextOnPurge { get; set; }

    /// <summary><see cref="ForceEndSilenceMinutes"/>'in varsayılanı (Y7: ≥ 15 dk sessizlik).</summary>
    public const int DefaultForceEndSilenceMinutes = 15;

    /// <summary>
    /// Canlı yayının sahibi bu kadar dakika sustuktan sonra başka bilgisayar yayını devralabilir (Y7, Y8): geri
    /// yüklenen kopya başka ya da bilinmeyen makinedeyse önceki kopyanın CANLI oturumunu ancak bu sessizlikten sonra
    /// devralır (§8.7, §11). Devrediliyor oturum hiçbir sürede başka makineye geçmez. Varsayılan 15; 0'dan büyük
    /// olmalı — sunucu aksi hâlde açılmaz (0 her canlı yayını sessizlik beklemeden devredilebilir yapardı). Prod'da
    /// compose ortamından (<c>SalesSync__ForceEndSilenceMinutes</c>).
    /// </summary>
    public int ForceEndSilenceMinutes { get; set; } = DefaultForceEndSilenceMinutes;

    /// <summary><see cref="ForceEndSilenceMinutes"/> süre olarak.</summary>
    public TimeSpan ForceEndSilence => TimeSpan.FromMinutes(ForceEndSilenceMinutes);

    /// <summary>Alt sınırın ayrıştırılmış hâli; boş ya da ayrıştırılamıyorsa null (kapı kapalı).</summary>
    public ClientVersion? ParseMinClientVersion() => ClientVersion.Parse(MinClientVersion);
}
