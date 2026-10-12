using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace OrderDeck.App.Services.Drawers;

/// <summary>
/// Tek bir çekmecenin canlı örneği: başlık + içerik + kapanışı bekleyen görev.
///
/// <c>Window.ShowDialog()</c>'un karşılığı. Fark: pencere BLOKLUYORDU, çekmece
/// bloklamaz — çağıran <see cref="Completion"/>'ı await eder. Dönen bool eski
/// <c>ShowDialog() == true</c> ile birebir aynı anlamda: true onaylanarak
/// kapandı, false iptal/ESC. Sonucun kendisi (seçilen müşteri, karar, kayıt)
/// eskiden olduğu gibi içeriğin ViewModel'inden okunur; çekmece taşımaz.
/// Sözleşmeyi böyle tutmak Faz 2b'deki 26 çağrı yerini mekanik bir dönüşüme
/// indiriyor.
/// </summary>
public sealed class Drawer : INotifyPropertyChanged
{
    // RunContinuationsAsynchronously: Close() UI thread'inden çağrılıyor.
    // Bayrak olmasa await eden gövde Close()'un İÇİNDEN, yığın daha kendini
    // toparlamadan devam ederdi.
    private readonly TaskCompletionSource<bool> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private bool _isTop = true;
    private bool _closed;

    internal Drawer(string title) => Title = title;

    /// <summary>Çekmece başlığında görünen metin.</summary>
    public string Title { get; }

    /// <summary>Gövdeye yerleşen görsel içerik. Yığın, çekmeceyi listeye
    /// eklemeden hemen önce doldurur (fabrika Drawer'ın kendisini almalı ki
    /// içeriğin ViewModel'i <see cref="Close"/>'u tutabilsin).</summary>
    public object? Content { get; internal set; }

    /// <summary>Kapanınca tamamlanır. true = onay, false = iptal/ESC.</summary>
    public Task<bool> Completion => _completion.Task;

    /// <summary>Yığının en üstünde mi? Alttakiler soluklaşır ve tıklama almaz.
    /// (İç içe çekmece gerçek bir ihtiyaç: DekontEkle → KargoYönergesi →
    /// KargoEşiği zinciri bugün üç seviye derinleşiyor.)</summary>
    public bool IsTop
    {
        get => _isTop;
        internal set
        {
            if (_isTop == value) return;
            _isTop = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Kapanış BURADAN başlar; yığın <see cref="Closed"/>'ı dinleyip kendini
    /// günceller. İkinci çağrı sessizce yok sayılır — ESC ile kapatma düğmesi
    /// aynı karede gelebilir, ya da yığın üstteki çekmeceyi iptal ederken o
    /// çekmece zaten kapanıyor olabilir.
    /// </summary>
    public void Close(bool confirmed)
    {
        if (_closed) return;
        _closed = true;
        // Önce yığından düş, sonra sonucu ver: await eden kod uyandığında
        // ekranda kapanmış bir çekmece görmesin.
        Closed?.Invoke(this);
        _completion.TrySetResult(confirmed);
    }

    internal event Action<Drawer>? Closed;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
