using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace OrderDeck.App.Services.Gates;

/// <summary>
/// Açık gate'lerin yığını. Tek örnek (singleton): hem
/// <see cref="IAppGateService"/> olarak verilir hem de AppRootView'daki
/// GateHost'un DataContext'i olur.
///
/// NEDEN YIĞIN, tek yuva değil: sihirbazın 2. adımı lisans için LoginGate
/// açıyor ve kapanınca AYNI adıma dönmesi gerekiyor. Tek slot bunu ifade
/// edemez.
///
/// Thread: WPF UI thread'i. Kilit yok; ObservableCollection zaten tek
/// thread'e bağlı.
/// </summary>
public sealed class AppGateStack : IAppGateService, INotifyPropertyChanged
{
    private readonly ObservableCollection<AppGate> _items = new();

    public AppGateStack() => Items = new ReadOnlyObservableCollection<AppGate>(_items);

    /// <summary>Alttan üste sıralı açık gate'ler. Yalnız <see cref="Top"/>
    /// çizilir; liste kapanış sırasını yönetmek için tutuluyor.</summary>
    public ReadOnlyObservableCollection<AppGate> Items { get; }

    /// <summary>Ekranda görünen gate. GateHost buna bağlanır.</summary>
    public AppGate? Top => _items.Count == 0 ? null : _items[^1];

    /// <summary>Gate katmanı görünür mü? False ise shell'in önü açılır.</summary>
    public bool IsOpen => _items.Count > 0;

    public Task<bool> ShowAsync(Func<AppGate, object> buildContent)
    {
        var gate = new AppGate();
        gate.Content = buildContent(gate);
        gate.Closed += OnGateClosed;
        _items.Add(gate);
        Refresh();
        return gate.Completion;
    }

    private void OnGateClosed(AppGate gate)
    {
        var index = _items.IndexOf(gate);
        if (index < 0) return;

        // Bir gate kapanırken ONUN AÇTIKLARI ekranda kalamaz: üsttekiler iptal
        // edilir. Her biri buraya yeniden girip kendini listeden düşürdüğü için
        // döngü bittiğinde index hâlâ geçerli.
        for (var i = _items.Count - 1; i > index; i--)
            _items[i].Close(false);

        gate.Closed -= OnGateClosed;
        _items.RemoveAt(index);
        Refresh();
    }

    private void Refresh()
    {
        OnPropertyChanged(nameof(Top));
        OnPropertyChanged(nameof(IsOpen));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
