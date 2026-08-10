using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace OrderDeck.App.Services.Pages;

/// <summary>
/// Açık sayfaların yığını. Tek örnek (singleton): hem <see cref="IPageService"/>
/// olarak ViewModel'lere verilir hem de PageHost'un DataContext'i olur.
///
/// NEDEN YIĞIN, tek yuva değil: <c>StreamHistoryDialog</c> bugün
/// <c>StreamReportDialog</c>'u İÇ İÇE modal olarak açıyor
/// (<c>StreamHistoryDialog.xaml.cs:20-23</c>). Tek yuva olsa rapordan
/// listeye dönüş yolu kalmazdı.
///
/// Çekmece yığınından farkı: burada <b>yalnız en üstteki çizilir</b>. Sayfa
/// altındakini tamamen örttüğü için ikisini birden ölçmek boşuna yerleşim
/// maliyeti; host bu yüzden ItemsControl değil ContentControl.
///
/// Thread: WPF UI thread'i. Kilit yok; ObservableCollection zaten tek
/// thread'e bağlı.
/// </summary>
public sealed class PageStack : IPageService, INotifyPropertyChanged
{
    private readonly ObservableCollection<Page> _items = new();

    public PageStack() => Items = new ReadOnlyObservableCollection<Page>(_items);

    /// <summary>Alttan üste sıralı açık sayfalar. Host yalnız
    /// <see cref="Top"/>'a bağlanır; bu koleksiyon derinlik sorgusu ve
    /// testler için.</summary>
    public ReadOnlyObservableCollection<Page> Items { get; }

    public Page? Top => _items.Count == 0 ? null : _items[^1];

    public bool IsOpen => _items.Count > 0;

    /// <summary>Sol nav'ın vurgu için baktığı anahtar. Açık sayfa yoksa null
    /// — hiçbir nav satırı vurgulanmaz, kabuktayız demektir.</summary>
    public string? CurrentKey => Top?.Key;

    public Task ShowAsync(string key, string title, Func<Page, object> buildContent)
    {
        var page = new Page(key, title);
        page.Content = buildContent(page);
        page.Closed += OnPageClosed;
        _items.Add(page);
        Refresh();
        return page.Completion;
    }

    public bool Back()
    {
        var top = Top;
        if (top is null) return false;
        top.Close();
        return true;
    }

    private void OnPageClosed(Page page)
    {
        var index = _items.IndexOf(page);
        if (index < 0) return;

        // Bir sayfa kapanırken ONUN AÇTIKLARI ekranda kalamaz: üsttekiler
        // de kapatılır. Her biri buraya yeniden girip kendini listeden
        // düşürdüğü için döngü bittiğinde index hâlâ geçerli.
        for (var i = _items.Count - 1; i > index; i--)
            _items[i].Close();

        page.Closed -= OnPageClosed;
        _items.RemoveAt(index);
        Refresh();
    }

    private void Refresh()
    {
        OnPropertyChanged(nameof(Top));
        OnPropertyChanged(nameof(IsOpen));
        OnPropertyChanged(nameof(CurrentKey));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
