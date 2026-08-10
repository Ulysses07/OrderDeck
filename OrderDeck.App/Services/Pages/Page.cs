namespace OrderDeck.App.Services.Pages;

/// <summary>
/// Tek bir açık sayfanın canlı örneği: kimlik + başlık + içerik + kapanışı
/// bekleyen görev.
///
/// <see cref="Drawers.Drawer"/>'ın kardeşi, üç farkla:
///
/// 1. <b>Sonuç sözleşmesi yok.</b> Çekmece <c>Task&lt;bool&gt;</c> taşıyor
///    çünkü <c>ShowDialog() == true</c>'nun karşılığı olması gerekiyordu.
///    Sayfada öyle bir sözleşme yok — dönüştürülen on pencerenin çağrı yeri
///    tek tek okundu, <b>hiçbiri</b> sonucu kullanmıyor. Olmayan bir
///    sözleşmeyi taklit etmek her sayfaya anlamsız bir <c>Close(true)</c>
///    ekletirdi. Yine de <see cref="Completion"/> await edilebilir: kapanış
///    SONRASI iş var (örn. <c>RefreshHighlights()</c>).
/// 2. <see cref="Key"/> var: sol nav'da hangi satırın vurgulanacağını
///    söyler. Çekmecenin nav'da karşılığı yok.
/// 3. <c>IsTop</c> yok: sayfa yığınında alttakiler hiç çizilmiyor (üstteki
///    zaten tam örtüyor), yani solma/tıklama-yeme durumu doğmuyor.
/// </summary>
/// <remarks>
/// <see cref="INotifyPropertyChanged"/> uygulamıyor, gerekmiyor: tüm
/// özellikleri yığına eklenmeden önce sabitleniyor. Host'un bağlandığı
/// değişken şey <c>PageStack.Top</c>, o da yığında bildiriliyor.
/// </remarks>
public sealed class Page
{
    // RunContinuationsAsynchronously: Close() UI thread'inden çağrılıyor.
    // Bayrak olmasa await eden gövde Close()'un İÇİNDEN, yığın daha kendini
    // toparlamadan devam ederdi. (Drawer'da da aynı gerekçe.)
    private readonly TaskCompletionSource _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private bool _closed;

    internal Page(string key, string title)
    {
        Key = key;
        Title = title;
    }

    /// <summary>Nav vurgusunun bağlandığı kimlik ("history", "settings"…).
    /// Başlık metni değil ayrı bir anahtar: başlık çevrilebilir/değişebilir
    /// bir şey, vurgu ona bağlanırsa metin düzeltmesi navigasyonu bozar.</summary>
    public string Key { get; }

    /// <summary>Sayfa başlığında görünen metin.</summary>
    public string Title { get; }

    /// <summary>Gövdeye yerleşen görsel içerik. Yığın, sayfayı listeye
    /// eklemeden hemen önce doldurur (fabrika Page'in kendisini almalı ki
    /// içeriğin ViewModel'i <see cref="Close"/>'u tutabilsin).</summary>
    public object? Content { get; internal set; }

    /// <summary>Kapanınca tamamlanır.</summary>
    public Task Completion => _completion.Task;

    /// <summary>
    /// Kapanış BURADAN başlar; yığın <see cref="Closed"/>'ı dinleyip kendini
    /// günceller. İkinci çağrı sessizce yok sayılır — ESC ile geri düğmesi
    /// aynı karede gelebilir, ya da yığın üstteki sayfayı kapatırken o sayfa
    /// zaten kapanıyor olabilir.
    /// </summary>
    public void Close()
    {
        if (_closed) return;
        _closed = true;
        // Önce yığından düş, sonra sonucu ver: await eden kod uyandığında
        // ekranda kapanmış bir sayfa görmesin.
        Closed?.Invoke(this);
        _completion.TrySetResult();
    }

    internal event Action<Page>? Closed;
}
