namespace OrderDeck.App.Services.Instagram;

/// <summary>
/// Ayarlar'daki Instagram kartının çoklu hesapla gelen, operatöre görünen metinleri (çoklu hesap tasarımı §8.6.1):
/// <see cref="OrderDeck.App.ViewModels.SettingsViewModel"/>'in durum satırına, hesap satırlarına ve sonuç satırına
/// yazdıkları, "Instagram hesabı ekle" çekmecesinin gösterdikleri ve XAML'in <c>x:Static</c> ile okuduğu yeni sabit
/// metinler. Kartın çoklu hesaptan ÖNCEKİ sabit metinleri
/// (başlık, izin uyarısı, ipucu paragrafı) <c>SettingsPage.xaml</c>'de, durum satırının ilk değeri ve genel hata
/// metni <see cref="OrderDeck.App.ViewModels.SettingsViewModel"/>'de kalır. Metinlerde yalnız hesap ve Sayfa adı ile
/// sayı geçer.
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

    // ---- "Instagram hesabı ekle" --------------------------------------------

    /// <summary>Karttaki düğme; çekmecenin başlığı da budur.</summary>
    public const string AddAccountButton = "Instagram hesabı ekle";

    /// <summary>Listeleme sürerken durum satırı.</summary>
    public const string ReadingPages = "Sayfalar okunuyor...";

    /// <summary>Instagram izni eksik ya da kullanıcı token'ı geçersiz (izin sondası ya da listeleme söyledi).</summary>
    public const string SessionNeedsRenewal =
        "Facebook oturumunun yenilenmesi gerekiyor. \"Facebook izinlerini güncelle\"ye bas.";

    /// <summary>Listeleme düştü ya da ekleme geçici bir hatayla (ağ, 5xx, kota) düştü.</summary>
    public const string PagesUnreadable = "Sayfalar okunamadı, internetini kontrol edip yeniden dene.";

    /// <summary>Instagram hesabı olan, henüz bağlı olmayan Sayfa yok: çekmece açılmaz.</summary>
    public const string NoAddableAccount =
        "Eklenebilecek başka hesap bulunamadı. Yeni hesabı önce bir Facebook Sayfasına bağla; Sayfa hazırsa " +
        "\"Facebook izinlerini güncelle\"ye bas ve açılan Facebook ekranında TÜM Sayfalarını ve Instagram hesaplarını işaretle.";

    /// <summary>Sınır dolu.</summary>
    /// <param name="maxPages">Bağlanabilecek en çok Sayfa sayısı (birincil dahil).</param>
    public static string PageLimitReached(int maxPages) => $"En çok {maxPages} Instagram hesabı bağlanabilir.";

    /// <summary>Seçilen Sayfanın token'ı türetilemedi.</summary>
    public static string AuthorizationFailed(string pageName) =>
        $"\"{pageName}\" için yetki alınamadı: bu Sayfada yönetici olduğundan emin ol.";

    /// <summary>Ekleme tamamlandı.</summary>
    /// <param name="accountLabel">Eklenen hesabın etiketi (<see cref="AccountLabel"/>).</param>
    public static string AccountAdded(string accountLabel) =>
        $"{accountLabel} eklendi. Yayın açıksa birkaç saniye içinde okunmaya başlar.";

    /// <summary>Çekmecenin üstündeki cümle.</summary>
    public const string AddDrawerPrompt = "Yorumlarını da okumak istediğin hesabı seç:";

    /// <summary>Çekmecenin onay düğmesi.</summary>
    public const string AddDrawerConfirm = "Ekle";

    /// <summary>Çekmecenin vazgeçme düğmesi.</summary>
    public const string AddDrawerCancel = "İptal";

    /// <summary>Çekmecenin altındaki ilk yardım satırı.</summary>
    public const string AddDrawerHelpNotListed =
        "Aradığın hesap listede yok mu? Önce Instagram hesabını bir Facebook Sayfasına bağla.";

    /// <summary>Çekmecenin altındaki ikinci yardım satırı.</summary>
    public const string AddDrawerHelpReauthorize =
        "Sayfa yine görünmüyorsa çekmeceyi kapatıp \"Facebook izinlerini güncelle\"ye bas.";

    /// <summary>Çekmecede Instagram hesabı olan Sayfanın seçeneği (ayraç U+2014).</summary>
    public static string OptionWithAccount(string accountLabel, string pageName) => $"{accountLabel} — Sayfa: {pageName}";

    /// <summary>Çekmecede Instagram hesabı olmayan (pasif) Sayfanın seçeneği (ayraç U+2014).</summary>
    public static string OptionWithoutAccount(string pageName) => $"{pageName} — Instagram hesabı bağlı değil";

    // ---- Altyazı ------------------------------------------------------------

    /// <summary>Hesap satırları görünürken (iki ve üzeri Sayfa) kartın altında yazar.</summary>
    public const string TwoAccountsCaption =
        "İki hesap bağlıyken yorumlar tek akışta birleşir; her Instagram yorumunun sağında hangi hesaptan geldiği yazar.";
}
