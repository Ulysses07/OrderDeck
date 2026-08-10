namespace OrderDeck.App.Services.Pages;

/// <summary>
/// Sayfa açmanın tek yolu. Spec §6.1: yayın DIŞINDA bakılan görünümler modal
/// pencere değil, kabuğun içerik alanını (üst bar hariç) kaplayan sayfalar.
///
/// Çekmecenin (<see cref="Drawers.IDrawerService"/>) kardeşi. İkisi ayrı
/// duruyor, ortak bir tabana soyutlanmadı: davranışları üç yerde ayrışıyor
/// (sonuç sözleşmesi, alttakilerin çizilmesi, kapladıkları alan) ve ortak
/// taban her seferinde <c>if (isDrawer)</c> üretirdi.
///
/// ViewModel'ler bu arayüze bağlanır; testlerde sahtesi verilir. Üretimdeki
/// uygulaması <see cref="PageStack"/> (aynı nesne hem servis hem de
/// PageHost'un bağlandığı yığın — arada üçüncü bir sınıf yok).
/// </summary>
public interface IPageService
{
    /// <summary>
    /// Sayfayı açar ve kapanmasını bekler. Sonuç DÖNMEZ — sayfada
    /// "onayla/iptal" ayrımı yok. Yine de await edilir, çünkü kapanış
    /// sonrası iş var (örn. <c>RefreshHighlights()</c>).
    ///
    /// <paramref name="key"/> nav vurgusunun kimliği; <paramref name="title"/>
    /// başlık şeridinde görünür. <paramref name="buildContent"/> içeriği
    /// üretirken <see cref="Page"/>'i alır: içeriğin ViewModel'i
    /// <c>page.Close</c>'u saklayıp kendini kapatabilsin. Fabrika kalıbı,
    /// içeriksiz bir sayfanın ekrana düşmesini imkânsız kılıyor.
    /// </summary>
    Task ShowAsync(string key, string title, Func<Page, object> buildContent);

    /// <summary>
    /// En üstteki sayfanın <see cref="Page.Key"/>'i, açık sayfa yoksa null.
    /// Sol nav bunu vurgu için bağlıyor (<c>{Binding Pages.CurrentKey}</c>);
    /// bildirimi <see cref="PageStack"/> yayınlıyor.
    /// </summary>
    string? CurrentKey { get; }

    /// <summary>
    /// En üstteki sayfayı kapatır — yığında başka sayfa varsa ona döner,
    /// yoksa kabuğa. Açık sayfa yoksa false döner; ESC işleyicisi tuşu
    /// tüketmesin, altındaki davranışa yol versin.
    /// </summary>
    bool Back();
}
