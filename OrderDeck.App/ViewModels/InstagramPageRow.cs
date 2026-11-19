namespace OrderDeck.App.ViewModels;

/// <summary>
/// Ayarlar'daki Instagram kartının bir satırı: bağlı bir Facebook Sayfası ve ona bağlı Instagram hesabının durumu,
/// gösterime hazır. <see cref="SettingsViewModel.RefreshInstagramStatusAsync"/> üretir. Kayıt değişmezdir ve değer
/// eşitliği taşır.
/// </summary>
/// <param name="PageId">Bağlı Facebook Sayfasının kimliği; "Kaldır" o Sayfayı bununla bulur.</param>
/// <param name="PageName">Sayfanın görünen adı.</param>
/// <param name="InstagramText">Hesabın etiketi ("@" ile başlar); hesap gösterilemiyorsa nedeni (Sayfada hesap yok
/// ya da durum okunamadı).</param>
/// <param name="IsPrimary">Birincil Sayfa: Facebook yorumları ve moderasyon bu Sayfadan çalışır.</param>
/// <param name="CanRemove">Satır tek başına kaldırılabilir. Yalnız ek Sayfalarda true: birincil ancak bağlantının
/// tamamıyla kaldırılır.</param>
/// <param name="IsWarning">Satır metni uyarı rengiyle yazılır.</param>
public sealed record InstagramPageRow(
    string PageId,
    string PageName,
    string InstagramText,
    bool IsPrimary,
    bool CanRemove,
    bool IsWarning);
