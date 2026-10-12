using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace OrderDeck.App.Services.Drawers;

/// <summary>
/// Açık çekmecelerin yığını. Tek örnek (singleton): hem <see cref="IDrawerService"/>
/// olarak ViewModel'lere verilir hem de DrawerHost'un DataContext'i olur.
///
/// NEDEN YIĞIN, tek yuva değil: bugünkü diyaloglar iç içe açılıyor —
/// DekontEkleDialog → ShipmentDirectiveDialog → ShipmentThresholdDialog üç
/// seviye, CustomerSearchDialog → CustomerDetailDialog iki. Tek slot bu
/// zinciri ifade edemez.
///
/// Thread: WPF UI thread'i. Kilit yok; ObservableCollection zaten tek
/// thread'e bağlı.
/// </summary>
public sealed class DrawerStack : IDrawerService, INotifyPropertyChanged
{
    private readonly ObservableCollection<Drawer> _items = new();

    public DrawerStack() => Items = new ReadOnlyObservableCollection<Drawer>(_items);

    /// <summary>Alttan üste sıralı açık çekmeceler. DrawerHost buna bağlanır.</summary>
    public ReadOnlyObservableCollection<Drawer> Items { get; }

    public Drawer? Top => _items.Count == 0 ? null : _items[^1];

    public bool IsOpen => _items.Count > 0;

    public Task<bool> ShowAsync(string title, Func<Drawer, object> buildContent)
    {
        var drawer = new Drawer(title);
        drawer.Content = buildContent(drawer);
        drawer.Closed += OnDrawerClosed;
        _items.Add(drawer);
        Refresh();
        return drawer.Completion;
    }

    public bool CloseTop()
    {
        var top = Top;
        if (top is null) return false;
        top.Close(false);
        return true;
    }

    private void OnDrawerClosed(Drawer drawer)
    {
        var index = _items.IndexOf(drawer);
        if (index < 0) return;

        // Bir çekmece kapanırken ONUN AÇTIKLARI ekranda kalamaz: üsttekiler
        // iptal edilir. Her biri buraya yeniden girip kendini listeden
        // düşürdüğü için döngü bittiğinde index hâlâ geçerli.
        for (var i = _items.Count - 1; i > index; i--)
            _items[i].Close(false);

        drawer.Closed -= OnDrawerClosed;
        _items.RemoveAt(index);
        Refresh();
    }

    private void Refresh()
    {
        for (var i = 0; i < _items.Count; i++)
            _items[i].IsTop = i == _items.Count - 1;

        OnPropertyChanged(nameof(Top));
        OnPropertyChanged(nameof(IsOpen));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
