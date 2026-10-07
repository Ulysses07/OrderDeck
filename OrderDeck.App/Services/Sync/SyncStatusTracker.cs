namespace OrderDeck.App.Services.Sync;

/// <summary>Akışın bir öğede neden beklediği (C7 incelemesi I-1).</summary>
public enum SyncBlockReason
{
    /// <summary>Durma (U5): yerelde aynı kimlikte gönderilemeyen kopya var — gönderim onu götürene dek.</summary>
    Stalled,
    /// <summary>Meşgul müşteri (U13): taşınacak müşteri ödeme akışında.</summary>
    Busy,
}

/// <summary>Akışın uzun süredir takıldığı öğe: sunucu Id'si (N biçimi), sebep, ilk takıldığı an ve
/// takılmanın en son görüldüğü tur (her takılı turda tazelenir — D2 incelemesi I-2: tazelenmeyen
/// takılma, turlar sunucuya hiç ulaşamazken durum satırında "çevrimdışı"yı gizlemesin).</summary>
public sealed record SyncBlock(string ItemId, SyncBlockReason Reason, DateTimeOffset Since, DateTimeOffset LastSeenAt);

/// <summary>Durum satırının (D2) okuduğu her şey, TEK kilit altında (D2 incelemesi I-4): ayrı ayrı
/// okunan özellikler arasında bir tur bitip öncelik sırası yanlış dala düşmesin.</summary>
public readonly record struct SyncStatusSnapshot(
    DateTimeOffset? LastPullOkAt,
    DateTimeOffset? LastCatchUpProgressAt,
    SyncBlock? BlockedOn,
    DateTimeOffset TrackingSince);

/// <summary>
/// Sunucuyla son başarılı yetişmenin tek kaydı (Faz 0, D2–D4). Faz 1'de tek kaynağı
/// müşteri değişiklik akışı: bir tur akışı BOŞ sayfaya kadar durmadan uyguladıysa
/// "diğer bilgisayarlara yetişildi". Durma (U5), meşgul müşteri (U13) ve hata yetişme sayılmaz.
/// Faz 2/3'te yayın, etiket, ödeme ve kargo çekmeleri de buraya yazacak.
///
/// <para><see cref="BlockedOn"/>: akış aynı öğede uzun süredir (eşik turu) takılıysa o öğe —
/// durum satırı (D2) "çevrimdışı" yerine "müşteri güncellemeleri bekliyor" gösterebilsin. Eşikten
/// sonra her takılı turda tazelenir; öğe uygulanınca null.</para>
///
/// <para><see cref="LastCatchUpProgressAt"/>: akış bu turda ilerledi ama boş sayfaya varmadı
/// (<see cref="CustomerPullOutcome.MorePending"/> — büyük ilk yetişme, CursorReset — ya da sayfalar
/// uygulandıktan sonra 429/hata/takılma). Durum satırı (D2) bu bilgisayarı ilerleme taze kaldıkça
/// "çevrimdışı" yerine "güncelleniyor" gösterir; tam yetişme ve lisans değişimi siler.</para>
///
/// <para><see cref="TrackingSince"/>: izlemenin başladığı an (kuruluş ya da lisans değişimi). Hiç
/// yetişemeyen süreç de çevrimdışı görünebilsin diye durum satırının son dayanağı (I-1).</para>
///
/// <para>Bütün zamanlar duvar saatidir (<see cref="DateTimeOffset.UtcNow"/>, IClock değil).</para>
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
    private DateTimeOffset? _catchUpProgress;
    private DateTimeOffset _trackingSince = DateTimeOffset.UtcNow;

    public DateTimeOffset? LastPullOkAt { get { lock (_gate) return _lastPullOk; } }

    /// <summary>İzlemenin başladığı an: kuruluş ya da son lisans değişimi.</summary>
    public DateTimeOffset TrackingSince { get { lock (_gate) return _trackingSince; } }

    /// <summary>Akışı ilerletip boş sayfaya varmayan son turun anı; tam yetişmeden sonra null.</summary>
    public DateTimeOffset? LastCatchUpProgressAt { get { lock (_gate) return _catchUpProgress; } }
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
            _catchUpProgress = null;
        }
    }

    /// <summary>Tur akışı ilerletti ama boş sayfaya varmadı (sayfa sınırı, ya da sayfalar uygulanıp
    /// sonra 429/hata/takılma): yetişme sürüyor. Yetişme sayılmaz — <see cref="LastPullOkAt"/> değişmez.</summary>
    public void MarkCatchUpProgress(DateTimeOffset at)
    {
        lock (_gate) _catchUpProgress = at;
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
            _catchUpProgress = null;
            _trackingSince = DateTimeOffset.UtcNow;
        }
    }

    public void SetBlockedOn(SyncBlock? block)
    {
        lock (_gate) _blockedOn = block;
    }

    /// <summary>Durum satırının bütün girdileri tek kilit altında.</summary>
    public SyncStatusSnapshot Snapshot()
    {
        lock (_gate) return new(_lastPullOk, _catchUpProgress, _blockedOn, _trackingSince);
    }
}
