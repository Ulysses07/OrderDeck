using OrderDeck.App.Services.Sync;

namespace OrderDeck.App.Services.Sales;

/// <summary>
/// Düzenleme yönlendiricisinin bağlantı ölçütü (spec 2026-10-13 §5.3, §5.7, §5.8): çevrimiçi = bu lisansın son başarılı
/// <c>sales/state</c>'i (<see cref="SalesSyncStateService.LastState"/>, 30 sn'lik tur) yaklaşık iki tur içinde
/// (<see cref="OnlineWindow"/>) ve ondan sonra hiçbir komut ağa takılmadı. Komutun sonucu da ölçüyü günceller
/// (<see cref="Report"/>): ağ hatası çevrimdışı yapar (sonraki başarılı state ya da komut yanıtına kadar); 404 komut
/// ucunu bir süre (<see cref="EndpointMissingHold"/>) yok sayar — sunucu geri alınmış olabilir (§8.6).
///
/// <para>Yalnız bellekten okur; ağ ve veritabanı yok. Saat yalnız testte değişir.</para>
/// </summary>
public sealed class SalesConnectivity
{
    /// <summary>Son başarılı state'in geçerlilik süresi: iki tur (2 × 30 sn) ve gecikme payı.</summary>
    public static readonly TimeSpan OnlineWindow = TimeSpan.FromSeconds(75);

    /// <summary>404'ten sonra komut ucunun yok sayıldığı süre; sonra yeniden denenir.</summary>
    public static readonly TimeSpan EndpointMissingHold = TimeSpan.FromSeconds(60);

    private readonly Func<SalesStateSnapshot?> _lastState;
    private readonly Func<string?> _licenseKey;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly object _lock = new();
    private DateTimeOffset? _offlineAt;
    private DateTimeOffset? _endpointMissingAt;

    public SalesConnectivity(
        Func<SalesStateSnapshot?> lastState, Func<string?> licenseKey, Func<DateTimeOffset>? utcNow = null)
    {
        _lastState = lastState;
        _licenseKey = licenseKey;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>Bu lisansın son başarılı state'i taze ve ondan sonra ağ hatası görülmedi.</summary>
    public bool IsOnline
    {
        get
        {
            var state = _lastState();
            var key = _licenseKey();
            if (state is null || string.IsNullOrWhiteSpace(key) || state.LicenseKey != key) return false;
            var now = _utcNow();
            if (now - state.ReceivedAt > OnlineWindow) return false;
            lock (_lock) return _offlineAt is not { } offline || offline < state.ReceivedAt;
        }
    }

    /// <summary>Komut ucu az önce 404 döndü (sunucu güncelleniyor ya da geri alındı).</summary>
    public bool IsEndpointMissing
    {
        get
        {
            lock (_lock) return _endpointMissingAt is { } at && _utcNow() - at < EndpointMissingHold;
        }
    }

    /// <summary>Sunucu komut alamıyorsa araç ipucu (§5.7), alabiliyorsa null.</summary>
    public string? UnavailableReason =>
        !IsOnline ? SalesCommandMessages.Offline
        : IsEndpointMissing ? SalesCommandMessages.EndpointMissing
        : null;

    /// <summary>Bir komutun sonucunu ölçüye işler.</summary>
    public void Report(SalesCommandStatus status)
    {
        var now = _utcNow();
        lock (_lock)
        {
            switch (status)
            {
                case SalesCommandStatus.Offline:
                    _offlineAt = now;
                    break;
                case SalesCommandStatus.EndpointMissing:
                    _offlineAt = null;
                    _endpointMissingAt = now;
                    break;
                case SalesCommandStatus.NotReady:
                    break;   // istek gitmedi: ölçü değişmez
                default:
                    // Sunucu yanıt verdi (200 ya da bir ret): bağlantı ve uç var.
                    _offlineAt = null;
                    _endpointMissingAt = null;
                    break;
            }
        }
    }
}
