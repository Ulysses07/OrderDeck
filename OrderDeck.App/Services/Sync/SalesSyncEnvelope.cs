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
/// <para><b>Yetenek düzeyi:</b> bu derlemede 2 (W2: akış, komutlar, kip geçişi). YALNIZ W3 3'e çıkarır (§8.1-4): mod
/// kapısı her kopyada 3 istediği için yarım bir derleme sahaya inse bile modu açtıramaz.</para>
///
/// <para><b>Gönderime hazır mı:</b> bu süreç geri yüklenmiş bir veritabanıyla açıldıysa ("bu süreçte geri yüklendi":
/// açılışta bekleyen önceki kimlik vardı ya da geri yükleme işareti uygulandı, <see cref="CaptureRestoreAtBoot"/>;
/// bekleyen önceki kimlik sonradan görülürse ya da hello onu taşırsa da işaretlenir) bu (lisans, kopya) için ÜÇÜ
/// birden olana kadar hiçbir v2 gönderimi — oturum, etiket, kargo, geri doldurma, komut — yapılmaz (§8.7):
/// hello başarıyla gitti (sunucu önce önceki kimliği görmeli, yoksa bayat kuyruğu açılış penceresi sayabilirdi, §4.0),
/// bu süreçte bir <c>sales/state</c> başarıyla geldi ve kip adımı (<see cref="SalesModeCoordinator"/>) o tazelenmiş
/// kiple bir kez koştu (W2-10): <c>SalesSyncState</c> yedekten geldiği için yerel kip bayat olabilir. Kimlik eksik ya
/// da bozuksa da hazır değildir ve v2 isteği hiç gitmez. Geri yüklenmemiş süreçte bu kapı yoktur.</para>
///
/// <para><b>Sonraki açılışa kadar kapalı</b> (<see cref="HoldUntilRestart"/>): açılışta geri yükleme kimlik işareti
/// uygulanamadıysa (<c>RestoreReplicaMarker</c>) veritabanı geri yüklenmiştir ama önceki kimlik henüz yazılamadı.
/// O süreçte hello, state, geri doldurma ve gönderim hiç gitmez (v1'e de düşmez): sunucu kopyayı özgün sanıp
/// geri doldurmada yereli kazandırırdı (§4.3, §8.7, M20). Etiketleme ve baskı yereldir, etkilenmez; kuyruk sonraki
/// açılışta gider.</para>
///
/// <para>Günlük yalnız kimlik taşır: makine adı ve parmak izi yazılmaz (sunucudaki kuralla aynı).</para>
/// </summary>
public sealed class SalesSyncEnvelope
{
    public const int Protocol = 2;

    /// <summary>W1 = 1, W2 = 2, yalnız W3 = 3 (§8.1-4).</summary>
    public const int FeatureLevel = 2;

    /// <summary>Sunucunun <c>machineName</c> ve <c>hardwareFingerprint</c> sınırı (<c>LicensesSalesController</c>).</summary>
    public const int MaxFieldLength = 128;

    private readonly ReplicaIdentityRepository _identity;
    private readonly SalesSyncStateRepository _state;
    private readonly ICurrentLicenseProvider _license;
    private readonly ILogger<SalesSyncEnvelope> _log;
    private readonly Lazy<string?> _fingerprint;
    private readonly ConcurrentDictionary<(string LicenseKey, Guid ReplicaId), byte> _helloSucceeded = new();
    private readonly ConcurrentDictionary<(string LicenseKey, Guid ReplicaId), byte> _stateSucceeded = new();
    private readonly ConcurrentDictionary<(string LicenseKey, Guid ReplicaId), byte> _modeRefreshed = new();
    private int _replicaProblemLogged;
    private int _legacyFallbackLogged;
    private int _heldUntilRestart;
    private int _restored;

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

    /// <summary>Bu süreçte satış senkronu sunucuya hiç istek göndermez (<see cref="HoldUntilRestart"/>).</summary>
    public bool IsHeldUntilRestart => Volatile.Read(ref _heldUntilRestart) == 1;

    /// <summary>
    /// Bu süreç boyunca satış senkronunun bütün isteklerini kapatır (açılış, <c>AppHost</c>): geri yükleme kimlik
    /// işareti uygulanamadı. Geri dönüşü yok; sonraki açılış işareti yeniden dener. Süreç başına bir kez Error düşer.
    /// </summary>
    public void HoldUntilRestart()
    {
        if (Interlocked.Exchange(ref _heldUntilRestart, 1) == 0)
            _log.LogError(
                "Geri yüklenen veritabanının önceki kopya kimliği bu açılışta yazılamadı; satış senkronu (etiket, oturum, "
              + "kargo gönderimi ve geri doldurma) sonraki açılışa kadar sunucuya istek göndermiyor");
    }

    /// <summary>Bu süreç geri yüklenmiş bir veritabanıyla açıldı (§8.7): gönderim kip tazelenene kadar kapalı
    /// (<see cref="IsReadyForPush"/>). Bir kez işaretlenince süreç boyunca kalır.</summary>
    public bool IsRestoredInThisProcess => Volatile.Read(ref _restored) == 1;

    /// <summary>"Bu süreçte geri yüklendi"yi işaretler (geri dönüşü yok): hello önceki kimliği taşırken, bekleyen önceki
    /// kimlik görüldüğünde ve açılışta (<see cref="CaptureRestoreAtBoot"/>).</summary>
    public void NoteRestored()
    {
        if (Interlocked.Exchange(ref _restored, 1) == 0)
            _log.LogInformation(
                "Bu süreç geri yüklenmiş veritabanıyla açıldı: satış gönderimleri hello, sales/state ve kip adımından sonra başlayacak");
    }

    /// <summary>
    /// Açılışta, göç ve geri yükleme işaretinden sonra (<c>AppHost</c>): işaret uygulandıysa ya da bekleyen önceki kimlik
    /// varsa bu süreç geri yüklenmiştir (§8.7). İlk hello önceki kimliği silmeden önce okunur: yoksa hello ile ilk
    /// gönderim turu yarışınca işaret kaybolabilirdi. Kimlik okunamazsa güvenli yan: geri yüklenmiş sayılır.
    /// </summary>
    /// <param name="markerApplied">Göç 049 öncesi yedeğin işareti bu açılışta uygulandı.</param>
    public void CaptureRestoreAtBoot(bool markerApplied)
    {
        try
        {
            if (markerApplied || _identity.GetPendingPreviousReplicaId() is not null) NoteRestored();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Açılışta bekleyen önceki kopya kimliği okunamadı; süreç geri yüklenmiş sayılıyor");
            NoteRestored();
        }
    }

    /// <summary>
    /// v2 gönderimi yapılabilir mi: süreç kapalı değil (<see cref="HoldUntilRestart"/>), kimlik geçerli ve süreç geri
    /// yüklenmemiş ya da bu (lisans, kopya) için hello, state ve kip adımı tamam (<see cref="IsReadyFor(string, Guid)"/>).
    /// Gönderim turları kararı <see cref="ForPush"/>'tan alır.
    /// </summary>
    public bool IsReadyForPush => !IsHeldUntilRestart && ReplicaId is { } replica && IsReadyFor(replica);

    /// <summary>
    /// Bu (lisans, kopya) için satış isteği gönderilebilir mi (geri doldurma da sorar): süreç kapalı değil ve ya bu süreç
    /// geri yüklenmemiş ya da bu çift için hello başarıyla gitti, bir <c>sales/state</c> başarıyla geldi ve kip adımı o
    /// kiple bir kez koştu (§8.7).
    /// </summary>
    public bool IsReadyFor(string licenseKey, Guid replicaId) =>
        !IsHeldUntilRestart && (!RestoreGateApplies() || IsRefreshed(licenseKey, replicaId));

    /// <summary>
    /// Bir gönderim turunun zarf kararı (spec 2026-10-13 §5.2, §8.7; W1-06): <c>null</c> = turu atla (geri yüklenmiş
    /// süreçte bu (lisans, kopya) için hello, state ya da kip adımı henüz yok, <see cref="IsReadyForPush"/>; ya da süreç
    /// <see cref="HoldUntilRestart"/> ile kapalı). Komut istemcisi de buradan karar verir. Kimlik eksik ya da bozuksa
    /// gönderim DURMAZ:
    /// <see cref="SalesPushEnvelope.Legacy"/> döner — çağıran zarfsız (v1) gövde gönderir, sunucu onu bugünkü gibi
    /// yargılar. Bu bir hatadır ve seri başına bir kez Error düşer (değer düzelip yeniden bozulursa yeniden).
    /// </summary>
    public SalesPushEnvelope? ForPush()
    {
        if (IsHeldUntilRestart) return null;
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
        if (!RestoreGateApplies()) return true;
        var key = _license.CurrentLicenseKey;
        return !string.IsNullOrWhiteSpace(key) && IsRefreshed(key, replica);
    }

    /// <summary>Geri yükleme kapısı bu süreçte geçerli mi; bekleyen önceki kimlik görülürse süreç işaretlenir.</summary>
    private bool RestoreGateApplies()
    {
        if (_identity.GetPendingPreviousReplicaId() is not null) NoteRestored();
        return IsRestoredInThisProcess;
    }

    /// <summary>Bu (lisans, kopya) için hello, state ve kip adımı tamam (§8.7).</summary>
    private bool IsRefreshed(string licenseKey, Guid replicaId)
    {
        var key = (licenseKey, replicaId);
        return _helloSucceeded.ContainsKey(key) && _stateSucceeded.ContainsKey(key) && _modeRefreshed.ContainsKey(key);
    }

    public bool HasHelloSucceeded(string licenseKey, Guid replicaId) =>
        _helloSucceeded.ContainsKey((licenseKey, replicaId));

    internal void MarkHelloSucceeded(string licenseKey, Guid replicaId) =>
        _helloSucceeded.TryAdd((licenseKey, replicaId), 0);

    /// <summary>Bu (lisans, kopya) için bu süreçte bir <c>sales/state</c> başarıyla geldi mi (§8.7).</summary>
    public bool HasStateSucceeded(string licenseKey, Guid replicaId) =>
        _stateSucceeded.ContainsKey((licenseKey, replicaId));

    /// <summary><c>sales/state</c> başarıyla geldi ve yerelde saklandı (<see cref="SalesSyncStateService"/>).</summary>
    internal void MarkStateSucceeded(string licenseKey, Guid replicaId) =>
        _stateSucceeded.TryAdd((licenseKey, replicaId), 0);

    /// <summary>Kip adımı bu (lisans, kopya) için tazelenmiş kiple koştu (<see cref="SalesModeCoordinator"/>, §8.7).
    /// Adım kendi açılış gönderimini (§8.4-1) yapabilsin diye işi yapmadan önce işaretlenir.</summary>
    internal void MarkModeRefreshed(string licenseKey, Guid replicaId) =>
        _modeRefreshed.TryAdd((licenseKey, replicaId), 0);

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
