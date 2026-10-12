namespace OrderDeck.App.Services.Drawers;

/// <summary>
/// Çekmece açmanın tek yolu. Spec §6: "hiçbir şey pop-up değil" — modal
/// pencerelerin yerini kabuğun sağ tarafından kayan çekmeceler alıyor.
///
/// ViewModel'ler bu arayüze bağlanır; testlerde sahtesi verilir.
/// Üretimdeki uygulaması <see cref="DrawerStack"/> (aynı nesne hem servis hem
/// de DrawerHost'un bağlandığı yığın — arada üçüncü bir sınıf yok).
/// </summary>
public interface IDrawerService
{
    /// <summary>
    /// Çekmeceyi açar ve kapanmasını bekler. true = onaylanarak kapandı.
    ///
    /// <paramref name="buildContent"/> içeriği üretirken <see cref="Drawer"/>'ı
    /// alır: içeriğin ViewModel'i <c>drawer.Close</c>'u saklayıp kendini
    /// kapatabilsin. Fabrika kalıbı, içeriksiz bir çekmecenin ekrana
    /// düşmesini imkânsız kılıyor.
    /// </summary>
    Task<bool> ShowAsync(string title, Func<Drawer, object> buildContent);

    /// <summary>
    /// En üstteki çekmeceyi iptal ederek kapatır. Açık çekmece yoksa false
    /// döner — ESC işleyicisi tuşu tüketmesin, altındaki davranışa (yedek
    /// seçim modundan çıkış) yol versin.
    /// </summary>
    bool CloseTop();
}
