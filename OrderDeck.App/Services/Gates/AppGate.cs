namespace OrderDeck.App.Services.Gates;

/// <summary>
/// Tek bir açılış durumunun (gate) canlı örneği: içerik + kapanışı bekleyen
/// görev.
///
/// <see cref="OrderDeck.App.Services.Drawers.Drawer"/>'ın modal kardeşi. İki
/// fark var ve ikisi de gate'lerin modal olmasından geliyor:
/// · <c>Title</c> yok — gate'in başlık şeridi yok, ekranın tamamı içerik.
/// · <c>IsTop</c> yok — yalnız en üstteki çizilir, alttakiler soluklaşmaz.
///
/// Dönen bool eski <c>ShowDialog() == true</c> ile birebir aynı anlamda.
/// </summary>
public sealed class AppGate
{
    // RunContinuationsAsynchronously: Close() UI thread'inden çağrılıyor.
    // Bayrak olmasa await eden gövde Close()'un İÇİNDEN, yığın daha kendini
    // toparlamadan devam ederdi.
    private readonly TaskCompletionSource<bool> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private bool _closed;

    internal AppGate() { }

    /// <summary>Ekrana çizilen görsel içerik. Yığın, gate'i listeye eklemeden
    /// hemen önce doldurur (fabrika gate'in kendisini almalı ki içerik
    /// <see cref="Close"/>'u tutabilsin).</summary>
    public object? Content { get; internal set; }

    /// <summary>Kapanınca tamamlanır. true = onay, false = iptal.</summary>
    public Task<bool> Completion => _completion.Task;

    /// <summary>
    /// Kapanış BURADAN başlar; yığın <see cref="Closed"/>'ı dinleyip kendini
    /// günceller. İkinci çağrı sessizce yok sayılır — yığın üstteki gate'i
    /// iptal ederken o gate zaten kapanıyor olabilir.
    /// </summary>
    public void Close(bool confirmed)
    {
        if (_closed) return;
        _closed = true;
        // Önce yığından düş, sonra sonucu ver: await eden kod uyandığında
        // ekranda kapanmış bir gate görmesin.
        Closed?.Invoke(this);
        _completion.TrySetResult(confirmed);
    }

    internal event Action<AppGate>? Closed;
}
