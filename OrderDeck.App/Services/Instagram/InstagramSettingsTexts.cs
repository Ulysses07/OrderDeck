namespace OrderDeck.App.Services.Instagram;

/// <summary>
/// Ayarlar'daki Instagram kartının operatöre görünen metinleri (çoklu hesap tasarımı §8.6.1). Tek yer:
/// <see cref="OrderDeck.App.ViewModels.SettingsViewModel"/> durum satırına, hesap satırlarına ve sonuç satırına bunları
/// yazar; kartın sabit metinleri de buradan okunur (<c>x:Static</c>). Metinlerde yalnız hesap ve Sayfa adı ile sayı
/// geçer.
/// </summary>
public static class InstagramSettingsTexts
{
    // ---- Durum satırı (kart başlığının altı) --------------------------------

    /// <summary>Facebook bağlı değil: gösterilecek hesap yok.</summary>
    public const string NotConnected = "Önce Facebook'a bağlan.";

    /// <summary>Tek Sayfa, Sayfaya bağlı Instagram hesabı yok. Çoklu hesaptan önceki metin, aynen.</summary>
    public const string SinglePageNotLinked = "Bu Sayfa'ya bağlı Instagram profesyonel hesabı yok.";

    /// <summary>Tek Sayfa, hesabı çözen çağrı düştü: hesap var mı yok mu bilinmiyor.</summary>
    public const string SinglePageUnreadable = "Durum okunamadı.";

    /// <summary>Tek Sayfa, hesap bulundu. Çoklu hesaptan önceki metin, aynen.</summary>
    /// <param name="accountLabel">Hesap etiketi (<see cref="AccountLabel"/>).</param>
    public static string SinglePageConnected(string accountLabel) => $"Bağlı Instagram hesabı: {accountLabel}";

    /// <summary>İki ve üzeri Sayfa.</summary>
    /// <param name="resolvedAccounts">Hesabı çözülen Sayfa sayısı; bağlı Sayfa sayısı DEĞİL (hesapsız Sayfa
    /// sayılmaz).</param>
    public static string ConnectedAccountCount(int resolvedAccounts) => $"{resolvedAccounts} Instagram hesabı bağlı";

    // ---- Hesap satırı -------------------------------------------------------

    /// <summary>Hesap etiketi: "@" + kullanıcı adı; Graph kullanıcı adını döndürmediyse "@" + Instagram kimliği.</summary>
    public static string AccountLabel(string igUserId, string? username) => "@" + (username ?? igUserId);

    /// <summary>Satır durumu: Sayfaya bağlı Instagram hesabı yok.</summary>
    public const string RowNotLinked = "Bu Sayfaya bağlı Instagram profesyonel hesabı yok";

    /// <summary>Satır durumu: hesabı çözen çağrı düştü.</summary>
    public const string RowUnreadable = "Durum okunamadı";

    /// <summary>Satırdaki Sayfa adının biçimi (<c>StringFormat</c>).</summary>
    public const string PageNameFormat = "Sayfa: {0}";

    /// <summary>Birincil satırda Sayfa adının ardına yazılan ek; baştaki boşluk metnin parçasıdır.</summary>
    public const string PrimaryRowSuffix = " · birincil (Facebook yorumları ve moderasyon bu Sayfadan)";

    /// <summary>Birincil satırın ipucu: o satırda neden "Kaldır" olmadığını söyler.</summary>
    public const string PrimaryRowTooltip =
        "Birincil Sayfayı değiştirmek için \"Bağlantıyı Kaldır\" + \"Facebook'a Bağlan\"";

    /// <summary>Ek satırdaki düğme.</summary>
    public const string RemoveButton = "Kaldır";

    // ---- Sonuç satırı -------------------------------------------------------

    /// <summary>"Kaldır" tamamlandı.</summary>
    /// <param name="label">Kaldırılan hesabın etiketi; hesabı çözülememiş satırda Sayfa adı.</param>
    public static string AccountRemoved(string label) => $"{label} kaldırıldı.";

    /// <summary>"Kaldır" yazılamadı: bağlantı değişmedi, satır yerinde.</summary>
    public const string RemoveFailed = "Hesap kaldırılamadı, yeniden dene.";

    /// <summary>Sonuç satırını kapatan düğme.</summary>
    public const string DismissNoticeButton = "Tamam";

    // ---- Altyazı ------------------------------------------------------------

    /// <summary>Hesap satırları görünürken (iki ve üzeri Sayfa) kartın altında yazar.</summary>
    public const string TwoAccountsCaption =
        "İki hesap bağlıyken yorumlar tek akışta birleşir; her Instagram yorumunun sağında hangi hesaptan geldiği yazar.";
}
