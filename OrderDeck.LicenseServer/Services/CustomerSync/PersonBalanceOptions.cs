using OrderDeck.LicenseServer.Services.Sync;

namespace OrderDeck.LicenseServer.Services.CustomerSync;

/// <summary>
/// Ortak bakiyenin (kişi havuzu) sunucu ayarları — <c>PersonBalance</c> bölümü (spec 2026-10-11 §7.2, M1). Prod'da
/// compose ortamından yalnız <c>PersonBalance__MinClientVersion</c> gelir; <see cref="ActivePcWindowDays"/> kod
/// varsayılanında kalır.
/// </summary>
public sealed class PersonBalanceOptions
{
    public const string Section = "PersonBalance";

    /// <summary><see cref="ActivePcWindowDays"/>'in varsayılanı (§7.2-1; satış senkronu kapısıyla aynı ölçüt).</summary>
    public const int DefaultActivePcWindowDays = 14;

    /// <summary>
    /// Wh'nin (W-havuz'u taşıyan ilk WPF sürümü, spec §2) numarası; Wh yayımlanınca Burak yazar (§7.1 adım 6). Wh'nin
    /// numarası S2 yazılırken bilinmediği için koda gömülmez; ileride yükseltilebilir. Boş = tanımsız:
    /// <c>person-balance enable --apply</c> reddeder; havuz kipindeki lisansta preview ve yeni yazım yapacak apply 409
    /// <c>client-update-required</c> döner (kapalı-hata, §7.2). Ayrıştırılamayan değer de tanımsız sayılır; bkz.
    /// <see cref="ParseMinClientVersion"/>. Açılışta tanımsızken havuz kipinde lisans varsa tek uyarı
    /// (<see cref="PersonBalanceStartup"/>).
    /// </summary>
    public string? MinClientVersion { get; set; }

    /// <summary>
    /// <c>person-balance enable</c>'ın 1. koşulunun penceresi (§7.2-1): son bu kadar gün içinde görülmüş ve devre dışı
    /// bırakılmamış her etkinleştirmenin sürümü ≥ <see cref="MinClientVersion"/> olmalı. Yalnız orada okunur;
    /// <c>verify</c>'ın 9. maddesi S1'in sabit penceresinde kalır. 0'dan büyük olmalı — sunucu aksi hâlde açılmaz
    /// (0 ya da eksi hiçbir PC'yi etkin saymaz ve 1. koşulu boşuna geçirirdi).
    /// </summary>
    public int ActivePcWindowDays { get; set; } = DefaultActivePcWindowDays;

    /// <summary><see cref="ActivePcWindowDays"/> süre olarak.</summary>
    public TimeSpan ActivePcWindow => TimeSpan.FromDays(ActivePcWindowDays);

    /// <summary>Alt sınırın ayrıştırılmış hâli (satış senkronunun ortak yardımcısı, §15-3); boş ya da
    /// ayrıştırılamıyorsa null (kapı kapalı).</summary>
    public ClientVersion? ParseMinClientVersion() => ClientVersion.Parse(MinClientVersion);
}
