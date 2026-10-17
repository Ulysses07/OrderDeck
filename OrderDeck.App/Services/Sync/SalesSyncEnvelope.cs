using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Licensing;

namespace OrderDeck.App.Services.Sync;

/// <summary>
/// Satış senkronunun v2 zarfı (spec 2026-10-13 §2, §4.0): her v2 isteğinin taşıdığı kopya kimliği, protokol ve
/// görülen kip; hello'nun makine adı, donanım parmak izi ve yetenek düzeyi. Tek örnek (DI): hello'nun başarısı
/// burada tutulur ve gönderim servisleri (W1-06..09) aynı örnekten <see cref="IsReadyForPush"/>'u okur.
///
/// <para><b>Yetenek düzeyi:</b> bu derlemede 1. W2 2'ye, YALNIZ W3 3'e çıkarır (§8.1-4): mod kapısı her kopyada
/// 3 istediği için yarım bir derleme sahaya inse bile modu açtıramaz.</para>
///
/// <para><b>Gönderime hazır mı:</b> geri yüklemeden sonra (<c>_meta.PreviousReplicaId</c> dolu) bu (lisans,
/// kopya) için bir hello başarıyla gidene kadar hiçbir v2 gönderimi yapılmaz (§8.7): sunucu önce önceki kimliği
/// görmeli, yoksa geri yüklenmiş kopyanın bayat kuyruğunu açılış penceresi sayabilirdi (§4.0). Kimlik eksik ya da
/// bozuksa da hazır değildir ve v2 isteği hiç gitmez. W2 bunun üstüne kip tazelemesini ekler.</para>
///
/// <para>Günlük yalnız kimlik taşır: makine adı ve parmak izi yazılmaz (sunucudaki kuralla aynı).</para>
/// </summary>
public sealed class SalesSyncEnvelope
{
    public const int Protocol = 2;

    /// <summary>W1 = 1, W2 = 2, yalnız W3 = 3 (§8.1-4).</summary>
    public const int FeatureLevel = 1;

    /// <summary>Sunucunun <c>machineName</c> ve <c>hardwareFingerprint</c> sınırı (<c>LicensesSalesController</c>).</summary>
    public const int MaxFieldLength = 128;

    private readonly ReplicaIdentityRepository _identity;
    private readonly SalesSyncStateRepository _state;
    private readonly ICurrentLicenseProvider _license;
    private readonly ILogger<SalesSyncEnvelope> _log;
    private readonly Lazy<string?> _fingerprint;
    private readonly ConcurrentDictionary<(string LicenseKey, Guid ReplicaId), byte> _helloSucceeded = new();
    private int _replicaProblemLogged;
    private int _legacyFallbackLogged;

    public SalesSyncEnvelope(
        ReplicaIdentityRepository identity,
        SalesSyncStateRepository state,
        IHardwareIdProvider hardware,
        ICurrentLicenseProvider license,
        ILogger<SalesSyncEnvelope> log)
    {
        _identity = identity;
        _state = state;
        _license = license;
        _log = log;
        MachineName = Truncate(Environment.MachineName) ?? string.Empty;
        // Tembel: gerçek sağlayıcı WMI okur (yavaş). İlk kez hello'da, arka plan iş parçacığında hesaplanır.
        _fingerprint = new Lazy<string?>(() =>
        {
            try { return Truncate(hardware.GetHardwareId()); }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Donanım parmak izi okunamadı; sales/hello parmak izsiz gönderilecek");
                return null;
            }
        }, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary><c>Environment.MachineName</c>, en çok <see cref="MaxFieldLength"/> karakter.</summary>
    public string MachineName { get; }

    /// <summary>Lisans etkinleştirmesindeki parmak iziyle aynı değer (<see cref="IHardwareIdProvider.GetHardwareId"/>),
    /// en çok <see cref="MaxFieldLength"/> karakter; okunamazsa <c>null</c>.</summary>
    public string? HardwareFingerprint => _fingerprint.Value;

    /// <summary>
    /// Bu veritabanının kopya kimliği; eksik ya da bozuksa <c>null</c> (bir kez uyarı — değer düzelip yeniden
    /// bozulursa yeniden). Her okumada veritabanından: geri yükleme kimliği döndürünce bellekte eski kimlik kalmaz.
    /// </summary>
    public Guid? ReplicaId
    {
        get
        {
            var id = _identity.GetReplicaId();
            if (id is null)
            {
                if (Interlocked.Exchange(ref _replicaProblemLogged, 1) == 0)
                    _log.LogWarning(
                        "Yerel kopya kimliği (_meta.ReplicaId) eksik ya da bozuk; satış senkronunun v2 istekleri gönderilmiyor");
                return null;
            }
            Interlocked.Exchange(ref _replicaProblemLogged, 0);
            return id;
        }
    }

    /// <summary>Görülen kip (§2): yerel <c>Mode = 0</c> ∧ bekleyen satış komutu yok ise 0, aksi hâlde 1.</summary>
    public int SeenMode => _state.GetSeenMode();

    /// <summary>
    /// v2 gönderimi yapılabilir mi: kimlik geçerli ve ya bekleyen önceki kimlik yok ya da bu (lisans, kopya) için
    /// hello başarıyla gitti. Gönderim turları kararı <see cref="ForPush"/>'tan alır.
    /// </summary>
    public bool IsReadyForPush => ReplicaId is { } replica && IsReadyFor(replica);

    /// <summary>
    /// Bir gönderim turunun zarf kararı (spec 2026-10-13 §5.2, §8.7; W1-06): <c>null</c> = turu atla (geri yüklemeden
    /// sonra bu (lisans, kopya) için hello henüz başarılı değil, <see cref="IsReadyForPush"/>). Kimlik eksik ya da
    /// bozuksa gönderim DURMAZ: <see cref="SalesPushEnvelope.Legacy"/> döner — çağıran zarfsız (v1) gövde gönderir,
    /// sunucu onu bugünkü gibi yargılar. Bu bir hatadır ve seri başına bir kez Error düşer (değer düzelip yeniden
    /// bozulursa yeniden).
    /// </summary>
    public SalesPushEnvelope? ForPush()
    {
        if (ReplicaId is not { } replica)
        {
            if (Interlocked.Exchange(ref _legacyFallbackLogged, 1) == 0)
                _log.LogError(
                    "Yerel kopya kimliği (_meta.ReplicaId) eksik ya da bozuk; etiket ve kargo gönderimi v2 zarfı olmadan "
                  + "(v1) sürüyor — sunucu sahipliği ve görülen kipi bu bilgisayardan öğrenemez");
            return SalesPushEnvelope.Legacy;
        }
        Interlocked.Exchange(ref _legacyFallbackLogged, 0);
        return IsReadyFor(replica) ? new SalesPushEnvelope(replica, SeenMode) : null;
    }

    private bool IsReadyFor(Guid replica)
    {
        if (_identity.GetPendingPreviousReplicaId() is null) return true;
        var key = _license.CurrentLicenseKey;
        return !string.IsNullOrWhiteSpace(key) && HasHelloSucceeded(key, replica);
    }

    public bool HasHelloSucceeded(string licenseKey, Guid replicaId) =>
        _helloSucceeded.ContainsKey((licenseKey, replicaId));

    internal void MarkHelloSucceeded(string licenseKey, Guid replicaId) =>
        _helloSucceeded.TryAdd((licenseKey, replicaId), 0);

    private static string? Truncate(string? value) =>
        value is { Length: > MaxFieldLength } ? value[..MaxFieldLength] : value;
}

/// <summary>Bir gönderim isteğinin zarfı (<see cref="SalesSyncEnvelope.ForPush"/>).</summary>
/// <param name="ReplicaId">Boşsa zarfsız (v1) gövde: kimlik eksik ya da bozuk.</param>
/// <param name="SeenMode">Görülen kip (§2); v1'de anlamsız.</param>
public readonly record struct SalesPushEnvelope(Guid? ReplicaId, int SeenMode)
{
    public static readonly SalesPushEnvelope Legacy = new(null, 0);

    public bool IsV2 => ReplicaId is not null;

    /// <summary>İstekteki <c>protocol</c>: v2'de 2, v1'de yazılmaz.</summary>
    public int? Protocol => IsV2 ? SalesSyncEnvelope.Protocol : null;

    /// <summary>İstekteki <c>seenMode</c>: v2'de değer, v1'de yazılmaz.</summary>
    public int? SeenModeField => IsV2 ? SeenMode : null;
}
