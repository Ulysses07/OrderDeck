namespace OrderDeck.App.Services.Sync;

/// <summary>Akışın bir öğede neden beklediği (C7 incelemesi I-1).</summary>
public enum SyncBlockReason
{
    /// <summary>Durma (U5): yerelde aynı kimlikte gönderilemeyen kopya var — gönderim onu götürene dek.</summary>
    Stalled,
    /// <summary>Meşgul müşteri (U13): taşınacak müşteri ödeme akışında.</summary>
    Busy,
}

/// <summary>Akışın uzun süredir takıldığı öğe: sunucu Id'si (N biçimi), sebep, ilk takıldığı an.</summary>
public sealed record SyncBlock(string ItemId, SyncBlockReason Reason, DateTimeOffset Since);

/// <summary>
/// Sunucuyla son başarılı yetişmenin tek kaydı (Faz 0, D2–D4). Faz 1'de tek kaynağı
/// müşteri değişiklik akışı: bir tur akışı BOŞ sayfaya kadar durmadan uyguladıysa
/// "diğer bilgisayarlara yetişildi". Durma (U5), meşgul müşteri (U13) ve hata yetişme sayılmaz.
/// Faz 2/3'te yayın, etiket, ödeme ve kargo çekmeleri de buraya yazacak.
///
/// <para><see cref="BlockedOn"/>: akış aynı öğede uzun süredir (eşik turu) takılıysa o öğe —
/// durum satırı (D2) "çevrimdışı" yerine "akış X için bekliyor" gösterebilsin. Öğe uygulanınca
/// null.</para>
///
/// <para>Yetişme LİSANSA bağlıdır (C10 incelemesi): form oynatmasının işareti lisans anahtarına
/// bağlı, yetişme ise süreç içi. Lisans değişince akış servisi durumu sıfırlar
/// (<see cref="ResetForLicenseChange"/>); değişimi henüz görmemişken (≤ bir akış turu) gelen form
/// turu da <see cref="IsInitialCatchUpDoneFor"/> ile önceki lisansın yetişmesini kendi yetişmesi
/// sanmaz.</para>
/// </summary>
public sealed class SyncStatusTracker
{
    private readonly object _gate = new();
    private DateTimeOffset? _lastPullOk;
    private string? _caughtUpLicense;
    private SyncBlock? _blockedOn;

    public DateTimeOffset? LastPullOkAt { get { lock (_gate) return _lastPullOk; } }
    public bool IsInitialCatchUpDone => LastPullOkAt is not null;

    /// <summary>Bu süreçte <paramref name="licenseKey"/>'in akışı boş sayfaya kadar yetişti mi.</summary>
    public bool IsInitialCatchUpDoneFor(string licenseKey)
    {
        lock (_gate)
            return _lastPullOk is not null && string.Equals(_caughtUpLicense, licenseKey, StringComparison.Ordinal);
    }

    public SyncBlock? BlockedOn { get { lock (_gate) return _blockedOn; } }

    public void MarkPullSucceeded(DateTimeOffset at, string licenseKey)
    {
        lock (_gate)
        {
            _lastPullOk = at;
            _caughtUpLicense = licenseKey;
        }
    }

    /// <summary>Lisans değişti: önceki lisansın yetişmesi ve takılma durumu yeni lisansı anlatmaz —
    /// yeni lisansın form oynatması KENDİ akışını bekler.</summary>
    public void ResetForLicenseChange()
    {
        lock (_gate)
        {
            _lastPullOk = null;
            _caughtUpLicense = null;
            _blockedOn = null;
        }
    }

    public void SetBlockedOn(SyncBlock? block)
    {
        lock (_gate) _blockedOn = block;
    }
}
