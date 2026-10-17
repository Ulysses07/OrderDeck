using Microsoft.Extensions.Logging;
using OrderDeck.Core.Settings;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Licensing.Api;
using OrderDeck.Licensing.Api.Models;

namespace OrderDeck.App.Services.Sync;

/// <summary>Bir <see cref="SalesSyncStateService"/> turunun sonucu.</summary>
public enum SalesStateTourOutcome
{
    /// <summary>Lisans yok ya da LicenseId çözülemedi — istek gitmedi.</summary>
    NoLicense,
    /// <summary>Yerel kopya kimliği eksik ya da bozuk — hiçbir v2 isteği gitmedi.</summary>
    NoReplica,
    /// <summary>Geri yükleme kimlik işareti bu açılışta uygulanamadı (<see cref="SalesSyncEnvelope.HoldUntilRestart"/>)
    /// — sonraki açılışa kadar hiçbir istek gitmez.</summary>
    RestoreUnresolved,
    /// <summary>hello ya da state başarısız (ağ, 5xx, 4xx, yerel yazım); sonraki turda yeniden denenir.</summary>
    Failed,
    /// <summary>hello (gerekiyorsa) ve state başarılı.</summary>
    Ok,
}

/// <summary>
/// Son <c>sales/state</c> yanıtının bellekteki kopyası (§4.5). W1 yalnız saklar; mod, canlı yayın başlıkları,
/// asgari sürüm ve sessizlik eşiği W2'nin (kip geçişleri, çip, force-end) girdisidir.
/// </summary>
public sealed record SalesStateSnapshot(
    string LicenseKey,
    int Mode,
    DateTimeOffset? ModeChangedAt,
    string? MinClientVersion,
    IReadOnlyList<LiveSessionDto> LiveSessions,
    int ForceEndSilenceMinutes,
    IReadOnlyList<LegacyMachineDto> LegacyMachines,
    DateTimeOffset ReceivedAt);

/// <summary>
/// Satış senkronunun kopya turu (spec 2026-10-13 §4.5, §8.2-1, §8.7): <c>sales/hello</c> ve her turda
/// <c>sales/state</c> (kalp atışı). Tur 30 sn'de bir ve açılışta hemen; sürücüsü satış akışı turudur, onun ilk adımı
/// (<see cref="SalesChangesPullService"/>, <see cref="SalesChangesPullHostedService"/>; W2): turda tam bir state.
///
/// <para><b>hello:</b> süreç başına (lisans, kopya) çifti için bir kez; lisans değişince, kimlik dönünce ya da yerel
/// kargo ayarı son başarılı hello'nun bildirdiğinden farklılaşınca yeniden (<see cref="HelloIsCurrent"/>). Gövde: kimlik, protokol 2, yetenek düzeyi, görülen kip, makine adı, donanım parmak izi, yerel kargo
/// ayarı (<c>AppSettings.Shipping</c>; özellik kapalıyken iki alan da boş) ve bekleyen önceki kimlik (§8.7).
/// 2xx önceki kimliği siler (karşılaştır ve sil) ve zarfta bu (lisans, kopya) için gönderimi açar
/// (<see cref="SalesSyncEnvelope.IsReadyForPush"/>). Başarısız hello sonraki turda yeniden denenir.</para>
///
/// <para><b>state:</b> her turda, hello'nun sonucundan bağımsız: kalp atışı kesilirse başka bilgisayar bu
/// bilgisayarın canlı yayınını sahibi susmuş sanıp bitirebilirdi (Y7). Kargo ayarının önbelleği ve KVKK boşaltma
/// ayarı <c>SalesSyncState</c>'e yazılır; mod, canlı yayınlar, asgari sürüm ve sessizlik eşiği bellekte kalır
/// (<see cref="LastState"/>). Yerel kipi bu servis yazmaz: kip geçişi satış akışı turunda, başarılı state'in hemen
/// ardından açılış turunun işidir (<c>SalesModeCoordinator</c>, §8.4).</para>
///
/// <para><b>Canlı yol beklemez:</b> tur arka plan iş parçacığında koşar, her <c>await</c>
/// <c>ConfigureAwait(false)</c>; ağ isteği uçarken hiçbir SQLite bağlantısı ya da yazma kilidi açık kalmaz. Yavaş ya
/// da düşmüş sunucu etiket yazmayı, baskıyı ve yayın açıp kapamayı durdurmaz.</para>
///
/// <para><b>Hatalar</b> yutulur; uyarı bir hata serisinin başında ve sürdükçe en çok
/// <see cref="FailureWarningInterval"/>'da bir yazılır, arası Debug. Günlük yalnız kimlik ve sayı taşır: müşteri,
/// makine adı, parmak izi ve lisans anahtarı yazılmaz.</para>
/// </summary>
public sealed class SalesSyncStateService
{
    /// <summary>Süren bir hata serisinde iki uyarı arasındaki en kısa süre.</summary>
    internal static readonly TimeSpan FailureWarningInterval = TimeSpan.FromMinutes(10);

    private readonly LicenseApiClient _api;
    private readonly SalesSyncEnvelope _envelope;
    private readonly ReplicaIdentityRepository _identity;
    private readonly SalesSyncStateRepository _state;
    private readonly ICurrentLicenseProvider _licenseProvider;
    private readonly Func<AppSettings> _settings;
    private readonly ILogger<SalesSyncStateService> _log;

    /// <summary>Tur tekdir: zamanlayıcı ile ileride elle tetiklenen tur (W2) üst üste binmez.</summary>
    private readonly SemaphoreSlim _tourGate = new(1, 1);

    private readonly FailureSeries _helloFailures = new();
    private readonly FailureSeries _stateFailures = new();

    private Guid? _cachedLicenseId;
    private string? _cachedLicenseKey;
    private (string LicenseKey, Guid ReplicaId)? _helloSentFor;
    /// <summary>Son başarılı hello'nun bildirdiği yerel kargo ayarı (<see cref="HelloIsCurrent"/>).</summary>
    private ReportedShippingDto? _helloShipping;
    private volatile SalesStateSnapshot? _last;

    public SalesSyncStateService(
        LicenseApiClient api,
        SalesSyncEnvelope envelope,
        ReplicaIdentityRepository identity,
        SalesSyncStateRepository state,
        ICurrentLicenseProvider licenseProvider,
        Func<AppSettings> settings,
        ILogger<SalesSyncStateService> log)
    {
        _api = api;
        _envelope = envelope;
        _identity = identity;
        _state = state;
        _licenseProvider = licenseProvider;
        _settings = settings;
        _log = log;
    }

    /// <summary>Son başarılı <c>sales/state</c> yanıtı; henüz yoksa <c>null</c>. Lisans değişse de önceki lisansın
    /// kopyası kalabilir — <see cref="SalesStateSnapshot.LicenseKey"/>'e bakılmalı.</summary>
    public SalesStateSnapshot? LastState => _last;

    public async Task<SalesStateTourOutcome> TourOnceAsync(CancellationToken ct)
    {
        await _tourGate.WaitAsync(ct).ConfigureAwait(false);
        try { return await TourCoreAsync(ct).ConfigureAwait(false); }
        finally { _tourGate.Release(); }
    }

    private async Task<SalesStateTourOutcome> TourCoreAsync(CancellationToken ct)
    {
        var licenseKey = _licenseProvider.CurrentLicenseKey;
        if (string.IsNullOrWhiteSpace(licenseKey)) return SalesStateTourOutcome.NoLicense;
        // Geri yüklenmiş kopya önceki kimliği olmadan konuşmaz: sunucu onu özgün sanardı (§8.7, M20).
        if (_envelope.IsHeldUntilRestart) return SalesStateTourOutcome.RestoreUnresolved;

        Guid replicaId;
        try
        {
            // Eksik/bozuk kimlik: zarf bir kez uyarır; hiçbir v2 isteği gitmez.
            if (_envelope.ReplicaId is not { } id) return SalesStateTourOutcome.NoReplica;
            replicaId = id;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            WarnOrDebug(_stateFailures, ex, "kopya kimliği okunamadı");
            return SalesStateTourOutcome.Failed;
        }

        var licenseId = await ResolveLicenseIdAsync(licenseKey, ct).ConfigureAwait(false);
        if (licenseId is null) return SalesStateTourOutcome.NoLicense;

        var helloOk = HelloIsCurrent(licenseKey, replicaId)
            || await HelloAsync(licenseKey, licenseId.Value, replicaId, ct).ConfigureAwait(false);
        var stateOk = await StateAsync(licenseKey, licenseId.Value, replicaId, ct).ConfigureAwait(false);
        return helloOk && stateOk ? SalesStateTourOutcome.Ok : SalesStateTourOutcome.Failed;
    }

    /// <summary>
    /// Geri doldurmanın tamamlandığını bildirir (spec 2026-10-13 §8.2-5; W1-09): <c>hello {backfillCompleted: true}</c>,
    /// gövdenin geri kalanı turun hello'suyla aynı. Turla aynı kapıdan geçer. Geçerli lisans ya da kopya kimliği
    /// geri doldurmanın yapıldığı (lisans, kopya) değilse istek gitmez: bayrak yanlış kopyaya yazılmasın.
    /// </summary>
    /// <returns>2xx alındı mı; değilse çağıran sonraki turda yeniden dener.</returns>
    public async Task<bool> ReportBackfillCompletedAsync(
        string licenseKey, Guid licenseId, Guid replicaId, CancellationToken ct)
    {
        await _tourGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_envelope.IsHeldUntilRestart) return false;
            if (_licenseProvider.CurrentLicenseKey != licenseKey || _envelope.ReplicaId != replicaId) return false;
            return await HelloAsync(licenseKey, licenseId, replicaId, ct, backfillCompleted: true).ConfigureAwait(false);
        }
        finally
        {
            _tourGate.Release();
        }
    }

    /// <summary>
    /// Bu (lisans, kopya) için hello gitti ve bildirdiği yerel kargo ayarı hâlâ geçerli mi. Ayar süreç içinde
    /// değiştiyse (ayar ekranı) hello yeniden gider: yoksa sunucunun <c>ReportedThreshold</c>/<c>ReportedFee</c>'si
    /// yeniden açılışa dek eski kalır ve <c>sales-sync status</c> bayat değeri gösterirdi (§6.2 "Geçiş", §8.1-6).
    /// Ölçüt son BAŞARILI hello'dur; düşen bildirim sonraki turda yeniden denenir.
    /// </summary>
    private bool HelloIsCurrent(string licenseKey, Guid replicaId)
    {
        if (_helloSentFor != (licenseKey, replicaId)) return false;
        try
        {
            return ReportedShipping() == _helloShipping;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Ayar okunamadı: hello denenir; hazırlanırken aynı hata uyarı serisine düşer.
            return false;
        }
    }

    /// <summary>hello'nun bildirdiği yerel kargo ayarı (<c>AppSettings.Shipping</c>); özellik kapalıyken iki alan da
    /// boş.</summary>
    private ReportedShippingDto ReportedShipping()
    {
        var shipping = _settings().Shipping;
        return shipping.IsEnabled
            ? new ReportedShippingDto(shipping.FreeShippingThreshold, shipping.ShippingFee)
            : new ReportedShippingDto(null, null);
    }

    private async Task<bool> HelloAsync(
        string licenseKey, Guid licenseId, Guid replicaId, CancellationToken ct, bool backfillCompleted = false)
    {
        Guid? previous;
        SalesHelloRequest request;
        try
        {
            previous = _identity.GetPendingPreviousReplicaId();
            if (previous == replicaId)
            {
                // Kopyanın kendisi önceki kimlik olamaz (sunucu 400 döner); anlamsız değer düşer.
                _identity.ClearPendingPreviousReplicaId(replicaId);
                _log.LogWarning(
                    "Bekleyen önceki kopya kimliği kopyanın kendisiyle aynıydı; silindi (replica={ReplicaId})", replicaId);
                previous = null;
            }

            request = new SalesHelloRequest(
                ReplicaId: replicaId,
                PreviousReplicaId: previous,
                MachineName: _envelope.MachineName,
                HardwareFingerprint: _envelope.HardwareFingerprint,
                Protocol: SalesSyncEnvelope.Protocol,
                FeatureLevel: SalesSyncEnvelope.FeatureLevel,
                SeenMode: _envelope.SeenMode,
                ReportedShipping: ReportedShipping(),
                BackfillCompleted: backfillCompleted ? true : null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            WarnOrDebug(_helloFailures, ex, "sales/hello hazırlanamadı");
            return false;
        }

        try
        {
            await _api.PostSalesHelloAsync(licenseId, request, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            WarnOrDebug(_helloFailures, ex, "sales/hello gönderilemedi");
            return false;
        }

        _helloSentFor = (licenseKey, replicaId);
        _helloShipping = request.ReportedShipping;
        _envelope.MarkHelloSucceeded(licenseKey, replicaId);
        _helloFailures.Succeeded();

        if (previous is { } sent)
        {
            try
            {
                _identity.ClearPendingPreviousReplicaId(sent);
                _log.LogInformation(
                    "sales/hello: geri yüklemeden önceki kopya kimliği sunucuya bildirildi (replica={ReplicaId}, previous={PreviousReplicaId})",
                    replicaId, sent);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Sunucu önceki kimliği zaten aldı (bir kez yazar); değer bir sonraki açılışın hello'sunda yeniden
                // gider ve orada silinir. Bu süreçte gönderim açık (hello başarılı).
                _log.LogWarning(ex,
                    "sales/hello başarılı ama bekleyen önceki kopya kimliği silinemedi (replica={ReplicaId})", replicaId);
            }
        }
        else
        {
            _log.LogInformation(
                "sales/hello gönderildi (replica={ReplicaId}, featureLevel={FeatureLevel}, seenMode={SeenMode}, "
              + "backfillCompleted={BackfillCompleted})",
                replicaId, request.FeatureLevel, request.SeenMode, backfillCompleted);
        }
        return true;
    }

    private async Task<bool> StateAsync(string licenseKey, Guid licenseId, Guid replicaId, CancellationToken ct)
    {
        SalesStateDto state;
        try
        {
            var seenMode = _envelope.SeenMode;
            state = await _api.GetSalesStateAsync(licenseId, replicaId, seenMode, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            WarnOrDebug(_stateFailures, ex, "sales/state okunamadı");
            return false;
        }

        _last = new SalesStateSnapshot(
            licenseKey,
            state.Mode,
            state.ModeChangedAt,
            state.MinClientVersion,
            state.LiveSessions ?? Array.Empty<LiveSessionDto>(),
            state.ForceEndSilenceMinutes,
            state.LegacyMachines ?? Array.Empty<LegacyMachineDto>(),
            DateTimeOffset.UtcNow);

        try
        {
            _state.SaveServerState(
                state.Shipping?.Version, state.Shipping?.FreeShippingThreshold, state.Shipping?.Fee,
                state.ScrubOrderTextOnPurge);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            WarnOrDebug(_stateFailures, ex, "sales/state yerelde saklanamadı");
            return false;
        }

        _stateFailures.Succeeded();
        return true;
    }

    private void WarnOrDebug(FailureSeries series, Exception ex, string what)
    {
        var code = ex is LicenseApiException api ? api.Code : ex.GetType().Name;
        if (series.ShouldWarn(DateTimeOffset.UtcNow, FailureWarningInterval))
            _log.LogWarning(ex, "Satış durumu turu: {What} ({Code}, art arda {Failures}. hata); sonraki turda yeniden denenecek",
                what, code, series.Count);
        else
            _log.LogDebug(ex, "Satış durumu turu: {What} ({Code}, art arda {Failures}. hata)", what, code, series.Count);
    }

    private async Task<Guid?> ResolveLicenseIdAsync(string licenseKey, CancellationToken ct)
    {
        if (_cachedLicenseId is not null && _cachedLicenseKey == licenseKey) return _cachedLicenseId;
        try
        {
            var licenses = await _api.GetMyLicensesAsync(ct).ConfigureAwait(false);
            var match = licenses.FirstOrDefault(l => l.LicenseKey == licenseKey);
            if (match?.Id is null)
            {
                _log.LogDebug("Satış durumu turu: geçerli lisans anahtarının LicenseId'si bulunamadı");
                return null;
            }
            _cachedLicenseId = match.Id;
            _cachedLicenseKey = licenseKey;
            return _cachedLicenseId;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogDebug(ex, "Satış durumu turu: LicenseId çözülemedi");
            return null;
        }
    }

    /// <summary>Art arda hata sayacı: serinin ilk hatası ve sonra en çok aralıkta bir uyarı.</summary>
    private sealed class FailureSeries
    {
        private DateTimeOffset? _lastWarnAt;

        public int Count { get; private set; }

        public bool ShouldWarn(DateTimeOffset now, TimeSpan interval)
        {
            Count++;
            if (_lastWarnAt is { } last && now - last < interval) return false;
            _lastWarnAt = now;
            return true;
        }

        public void Succeeded()
        {
            Count = 0;
            _lastWarnAt = null;
        }
    }
}
