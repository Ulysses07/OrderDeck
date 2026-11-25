namespace OrderDeck.Chat.Ingestors.Instagram;

/// <summary>
/// Instagram döngülerinin uyandırılabilir beklemesi: yayın oturumu başlayınca ve "Sohbeti yenile"
/// istenince <see cref="Signal"/> çağrılır, o an bekleyen bütün döngüler (üst döngü ve hesap
/// döngüleri) birden uyanır.
///
/// <para><b>Sinyal hatırlanmaz.</b> Kimse beklemiyorken gelen sinyal, ileride başlayacak ilgisiz bir
/// beklemeyi kesmez.</para>
///
/// <para><b>Kaçan sinyal olmaz.</b> Döngü "bekleyeceğim" kararını bir şeye bakarak verir (oturum var
/// mı, hesap çözüldü mü). Sinyal o bakıştan sonra, bekleme başlamadan önce gelirse kaybolurdu ve döngü
/// uzun beklerdi. Bunun için döngü bakmadan ÖNCE <see cref="Epoch"/>'u okur ve beklerken verir: o
/// arada sinyal geldiyse bekleme hemen döner.</para>
/// </summary>
internal sealed class InstagramWake
{
    private readonly Lock _gate = new();
    private TaskCompletionSource _signal = NewSignal();
    private long _epoch;
    private int _waiting;

    /// <summary>Şimdiye dek gelen sinyal sayısı. Bekleyecek olan, kararını vermeden önce okur.</summary>
    public long Epoch
    {
        get { lock (_gate) return _epoch; }
    }

    /// <summary>O an bekleyen bütün <see cref="DelayAsync"/> çağrılarını uyandırır. Beklemez; uyananların
    /// devam kodu çağıranın iş parçacığında koşmaz.</summary>
    /// <returns>Uyandırılan bekleme sayısı.</returns>
    public int Signal()
    {
        TaskCompletionSource fired;
        int waiting;
        lock (_gate)
        {
            fired = _signal;
            _signal = NewSignal();
            _epoch++;
            waiting = _waiting;
        }

        fired.TrySetResult();
        return waiting;
    }

    /// <summary>Süre dolana ya da sinyal gelene dek bekler.</summary>
    /// <param name="duration">En çok beklenecek süre.</param>
    /// <param name="time">Saat kaynağı.</param>
    /// <param name="ct">Beklemeyi iptal eder (fırlatır).</param>
    /// <param name="sinceEpoch">Verilirse: çağıranın kararını vermeden önce okuduğu <see cref="Epoch"/>.
    /// O andan beri sinyal geldiyse beklenmez.</param>
    /// <returns>Sinyalle uyanıldıysa (ya da <paramref name="sinceEpoch"/>'tan beri sinyal geldiyse) true;
    /// süre dolduysa false.</returns>
    public async Task<bool> DelayAsync(
        TimeSpan duration, TimeProvider time, CancellationToken ct, long? sinceEpoch = null)
    {
        ct.ThrowIfCancellationRequested();

        Task signal;
        lock (_gate)
        {
            if (sinceEpoch is { } since && since != _epoch) return true;

            signal = _signal.Task;
            _waiting++;
        }

        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var delay = Task.Delay(duration, time, linked.Token);
            if (await Task.WhenAny(delay, signal).ConfigureAwait(false) == signal)
            {
                // Zamanlayıcı bırakılır; asıl iptal o arada geldiyse o kazanır.
                linked.Cancel();
                try { await delay.ConfigureAwait(false); }
                catch (OperationCanceledException) { /* zamanlayıcı bilerek kesildi */ }
                ct.ThrowIfCancellationRequested();
                return true;
            }

            await delay.ConfigureAwait(false); // iptal edildiyse burada fırlatır
            return false;
        }
        finally
        {
            lock (_gate) _waiting--;
        }
    }

    // RunContinuationsAsynchronously: sinyali veren (arayüz iş parçacığı) uyananların devam kodunu
    // kendi üstünde koşturmasın.
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
