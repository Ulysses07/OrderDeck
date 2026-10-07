namespace OrderDeck.App.Services.Sync;

/// <summary>
/// Sunucuyla son başarılı yetişmenin tek kaydı (Faz 0, D2–D4). Faz 1'de tek kaynağı
/// müşteri değişiklik akışı: bir tur akışı BOŞ sayfaya kadar durmadan uyguladıysa
/// "diğer bilgisayarlara yetişildi". Durma (U5), meşgul müşteri (U13) ve hata yetişme sayılmaz.
/// Faz 2/3'te yayın, etiket, ödeme ve kargo çekmeleri de buraya yazacak.
/// </summary>
public sealed class SyncStatusTracker
{
    private readonly object _gate = new();
    private DateTimeOffset? _lastPullOk;

    public DateTimeOffset? LastPullOkAt { get { lock (_gate) return _lastPullOk; } }
    public bool IsInitialCatchUpDone => LastPullOkAt is not null;

    public void MarkPullSucceeded(DateTimeOffset at)
    {
        lock (_gate) _lastPullOk = at;
    }
}
