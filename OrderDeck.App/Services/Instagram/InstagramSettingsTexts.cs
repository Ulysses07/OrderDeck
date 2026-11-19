namespace OrderDeck.App.Services.Instagram;

/// <summary>
/// Ayarlar'daki Instagram kartının çoklu hesapla gelen, operatöre görünen metinleri (çoklu hesap tasarımı §8.6.1):
/// <see cref="OrderDeck.App.ViewModels.SettingsViewModel"/>'in durum satırına, hesap satırlarına ve sonuç satırına
/// yazdıkları, "Instagram hesabı ekle" çekmecesinin gösterdikleri ve XAML'in <c>x:Static</c> ile okuduğu yeni sabit
/// metinler; izin uyarısı da yeni düğmeye yönlendiği için buradadır. Kartın çoklu hesaptan ÖNCEKİ sabit metinleri
/// (başlık, ipucu paragrafı) <c>SettingsPage.xaml</c>'de, genel hata metni
/// <see cref="OrderDeck.App.ViewModels.SettingsViewModel"/>'de kalır; durum satırının ilk değeri de "Kaldır"dan
/// sonra yazıldığı için buradadır. Metinlerde yalnız hesap ve Sayfa adı ile sayı geçer.
/// </summary>
public static class InstagramSettingsTexts
{
    // ---- Durum satırı (kart başlığının altı) --------------------------------

    /// <summary>Durum henüz okunmadı: kartın ilk değeri; "Kaldır"dan sonra da tazeleme sonucu yazana dek görünür.
    /// Çoklu hesaptan önceki metin, aynen.</summary>
    public const string Checking = "Kontrol ediliyor...";

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

    /// <summary>Bağlantı kaydedilemedi (dosya, disk ya da şifreleme hatası): hiçbir şey eklenmedi, yeniden denemek
    /// yeter. "Hesap kaldırılamadı, yeniden dene."nin eşi.</summary>
    public const string AddFailed = "Hesap eklenemedi, yeniden dene.";

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

    // ---- "Facebook izinlerini güncelle" -------------------------------------

    /// <summary>Karttaki düğme; eksik Sayfa onay çekmecesinin başlığı da budur.</summary>
    public const string ReauthorizeButton = "Facebook izinlerini güncelle";

    /// <summary>Düğmenin ipucu: yayın sırasında basmayı yasaklamaz, bedelini söyler.</summary>
    public const string ReauthorizeTooltip =
        "Yayın sırasında yalnız kopan bir hesabı düzeltmek için bas: çalışan Instagram ve Facebook yorumlarında kısa bir kesinti olabilir.";

    /// <summary>Tarayıcı onayı beklenirken düğmenin yanında görünen iptal düğmesi.</summary>
    public const string CancelReauthorizeButton = "İptal";

    /// <summary>Instagram izni eksikken kartta çıkan uyarı: kullanıcıyı izin güncelleme düğmesine yönlendirir.</summary>
    public const string PermissionsMissingWarning =
        "Instagram yorumları için Facebook izinlerini güncellemen gerekiyor: aşağıdaki \"Facebook izinlerini güncelle\"ye bas.";

    /// <summary>Tarayıcı onayı beklenirken durum satırı.</summary>
    public const string WaitingForBrowser =
        "Tarayıcıdan onay bekleniyor... Açılan Facebook ekranında TÜM Sayfalarını ve Instagram hesaplarını işaretli bırak; " +
        "yalnız yenisini işaretlersen eski hesabın bağlantısı düşer.";

    /// <summary>Eksik Sayfa onayı, kaydetmeden önce.</summary>
    /// <param name="pageNames">Yeni girişte görünmeyen bağlı Sayfaların adları, bağlı sırasıyla.</param>
    /// <param name="includesPrimary">Eksiklerden biri birincil Sayfa: Facebook yorumları da kesilebilir.</param>
    public static string MissingPagesQuestion(IReadOnlyList<string> pageNames, bool includesPrimary) =>
        $"{QuotedNames(pageNames)} bu girişte paylaşılmadı. Kaydedersen bu " +
        $"{(pageNames.Count == 1 ? "Sayfanın" : "Sayfaların")} yorumları kesilebilir" +
        (includesPrimary ? " (Facebook yorumları dahil)" : "") +
        ". Vazgeçersen OrderDeck'te hiçbir şey değişmez; \"Facebook izinlerini güncelle\"ye yeniden basıp tüm " +
        "Sayfalarını işaretle. Yine de kaydedilsin mi?";

    /// <summary>İzin güncellemesi kaydedildi.</summary>
    /// <param name="refreshedPages">Yeni token'ı yazılan Sayfa sayısı (birincil dahil).</param>
    /// <param name="missingPageNames">Yeni girişte görünmeyen (onayla kaydedilen) Sayfaların adları; yoksa boş.</param>
    public static string PermissionsUpdated(int refreshedPages, IReadOnlyList<string> missingPageNames) =>
        $"Facebook izinleri güncellendi: {refreshedPages} Sayfa yenilendi." +
        (missingPageNames.Count == 0 ? "" : $" {QuotedNames(missingPageNames)} bu girişte yoktu; yorumları kesilebilir.");

    /// <summary>"İptal"e basıldı ya da eksik Sayfa onayında vazgeçildi.</summary>
    public const string ReauthorizeCanceled = "Güncelleme iptal edildi; bağlantı değişmedi.";

    /// <summary>Tarayıcı onayı süresinde tamamlanmadı.</summary>
    public const string ReauthorizeTimedOut =
        "Facebook onayı 5 dakika içinde tamamlanmadı; bağlantı değişmedi. Yeniden dene.";

    /// <summary>Graph hatası; ham gövde yazılmaz.</summary>
    /// <param name="code">Graph hata kodu; okunamadıysa null.</param>
    /// <param name="httpStatus">HTTP durumu; kod okunamadığında yazılır.</param>
    public static string ReauthorizeGraphFailed(int? code, int? httpStatus) =>
        $"Güncelleme başarısız: Facebook hatası ({(code is { } value ? $"kod {value}" : $"HTTP {httpStatus}")}). Bağlantı değişmedi.";

    /// <summary>Diğer hatalar.</summary>
    /// <param name="reason">Kısa sebep: istisna iletisi, sondaki nokta ve boşluk kırpılmış.</param>
    public static string ReauthorizeFailed(string reason) => $"Güncelleme başarısız: {reason}. Bağlantı değişmedi.";

    /// <summary>Sayfa adları tırnak içinde, <c>", "</c> ile birleşik.</summary>
    private static string QuotedNames(IReadOnlyList<string> pageNames) =>
        string.Join(", ", pageNames.Select(name => $"\"{name}\""));

    // ---- Altyazı ------------------------------------------------------------

    /// <summary>Hesap satırları görünürken (iki ve üzeri Sayfa) kartın altında yazar.</summary>
    public const string TwoAccountsCaption =
        "İki hesap bağlıyken yorumlar tek akışta birleşir; her Instagram yorumunun sağında hangi hesaptan geldiği yazar.";
}
