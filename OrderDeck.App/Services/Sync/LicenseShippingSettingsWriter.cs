using Microsoft.Extensions.Logging;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Licensing.Api;
using OrderDeck.Licensing.Api.Models;

namespace OrderDeck.App.Services.Sync;

/// <summary>Ayar ekranının lisans geneli kargo yazımının sonucu (<see cref="LicenseShippingSettingsWriter"/>).</summary>
public enum ShippingSettingsSaveStatus
{
    /// <summary>200: sunucu yazdı, önbellek yanıtın satırıyla güncellendi.</summary>
    Saved,

    /// <summary>409 <c>stale</c>: ayar bu arada başka bilgisayarda değişti; önbellek <c>current</c> ile güncellendi.</summary>
    Stale,

    /// <summary>409 <c>mode-off</c>: istemci modu yanlış biliyordu; Off turu istendi, kargo yazılmadı.</summary>
    ModeOff,

    /// <summary>v2 zarfı hazır değil (kopya kimliği yok, geri yüklemeden sonra kip tazelenmedi, süreç kapalı) ya da
    /// lisans çözülemedi: istek gitmedi.</summary>
    NotReady,

    /// <summary>Sunucuya ulaşılamadı (lisans çözümü ya da PUT).</summary>
    Offline,

    /// <summary>5xx ya da çözümlenemeyen yanıt: yazım uygulanmış olabilir; sonraki <c>sales/state</c> turu önbelleği
    /// düzeltir, eski sürümle giden yeni yazım <c>stale</c> alır.</summary>
    Failed,

    /// <summary>Uç yok (S2 öncesi imaj ya da otomatik geri alma penceresi, §8.6).</summary>
    EndpointMissing,

    /// <summary>Sunucu isteği reddetti (400 ya da beklenmeyen 409): istemci hatası, yeniden denenmez.</summary>
    Rejected,
}

/// <summary>Ayar ekranının lisans geneli kargo yazımının sonucu.</summary>
/// <param name="Message">Operatöre görünen satır içi metin (<see cref="SalesCommandMessages"/>); başarıda null.</param>
/// <param name="Current">Sunucunun satırı: <see cref="ShippingSettingsSaveStatus.Saved"/>'de yazılan,
/// <see cref="ShippingSettingsSaveStatus.Stale"/>'de güncel olan; öbürlerinde null.</param>
public sealed record ShippingSettingsSaveOutcome(
    ShippingSettingsSaveStatus Status,
    string? Message,
    ShippingSettingsSnapshot? Current = null);

/// <summary>
/// Lisans geneli kargo ayarının yazıcısı (spec 2026-10-13 §4.8, §6.2; sahip kararı Y4; W3-03): ayar ekranı mod On'da
/// kargo kısmını yerel <c>AppSettings.Shipping</c>'e değil buraya yazar — <c>PUT shipping-settings</c>
/// <c>{expectedVersion, freeShippingThreshold, fee}</c> ve v2 zarfı (<see cref="SalesSyncEnvelope.ForPush"/>).
///
/// <para><b>Sonuçlar:</b> 200 ve 409 <c>stale</c> sunucunun döndürdüğü satırı yerel önbelleğe yazar
/// (<see cref="SalesSyncStateRepository.SaveShippingCache"/>; <c>ShippingSettingsProvider</c> mod On'da oradan okur);
/// yerel ayara hiçbir yolda dokunulmaz. 409 <c>mode-off</c> Off turunu ister (§5.3, §8.5; turun sonunda sunucu değeri
/// yerel ayara kopyalanır). Çevrimdışı, sunucu hatası ve uç yok yazmaz; metinler §5.3/§5.7 ailesinden.</para>
///
/// <para>Ağ ve veritabanı işi arayüz iş parçacığında koşmaz (<see cref="SaveAsync"/> arka plana geçer); iptal dışında
/// fırlatmaz. Günlük yalnız sürüm ve durum kodu taşır: tutar yazılmaz.</para>
/// </summary>
public sealed class LicenseShippingSettingsWriter
{
    private readonly LicenseApiClient _api;
    private readonly SalesSyncEnvelope _envelope;
    private readonly SalesSyncStateRepository _state;
    private readonly ICurrentLicenseProvider _license;
    private readonly Action _requestModeOffRound;
    private readonly ILogger<LicenseShippingSettingsWriter> _log;

    private Guid? _cachedLicenseId;
    private string? _cachedLicenseKey;

    /// <param name="requestModeOffRound">409 <c>mode-off</c>'ta çağrılır: <c>SalesChangesPullService.RequestModeOffRound</c>
    /// (AppHost tembel çözer). Beklemez; turu arka planda başlatır.</param>
    public LicenseShippingSettingsWriter(
        LicenseApiClient api,
        SalesSyncEnvelope envelope,
        SalesSyncStateRepository state,
        ICurrentLicenseProvider license,
        Action requestModeOffRound,
        ILogger<LicenseShippingSettingsWriter> log)
    {
        _api = api;
        _envelope = envelope;
        _state = state;
        _license = license;
        _requestModeOffRound = requestModeOffRound;
        _log = log;
    }

    /// <summary>
    /// Ayarı yazar. Değerler çağıranda doğrulanmış olmalı (ikisi boş ya da ikisi &gt; 0, en çok iki ondalık; sunucu aksi
    /// hâlde 400 <c>invalid-shipping</c> verir, sonuç <see cref="ShippingSettingsSaveStatus.Rejected"/>).
    /// </summary>
    /// <param name="expectedVersion">Formun gösterdiği değerin sürümü (önbellekten; ayar hiç yazılmadıysa 0). Formu açan
    /// sürüm gider, kaydet anındaki değil: arada başka bilgisayarın yazdığı değer sessizce ezilmesin, <c>stale</c>
    /// gelsin.</param>
    public Task<ShippingSettingsSaveOutcome> SaveAsync(
        int expectedVersion, decimal? threshold, decimal? fee, CancellationToken ct = default)
        => Task.Run(() => SaveCoreAsync(expectedVersion, threshold, fee, ct), ct);

    private async Task<ShippingSettingsSaveOutcome> SaveCoreAsync(
        int expectedVersion, decimal? threshold, decimal? fee, CancellationToken ct)
    {
        try
        {
            if (_envelope.ForPush() is not { IsV2: true } envelope)
            {
                _log.LogInformation("Kargo ayarı yazılmadı: v2 zarfı hazır değil");
                return new(ShippingSettingsSaveStatus.NotReady, SalesCommandMessages.NotReady);
            }
            var licenseKey = _license.CurrentLicenseKey;
            if (string.IsNullOrWhiteSpace(licenseKey))
                return new(ShippingSettingsSaveStatus.NotReady, SalesCommandMessages.NotReady);
            var (licenseId, offline) = await ResolveLicenseIdAsync(licenseKey, ct).ConfigureAwait(false);
            if (licenseId is null)
                return offline
                    ? new(ShippingSettingsSaveStatus.Offline, SalesCommandMessages.Offline)
                    : new(ShippingSettingsSaveStatus.NotReady, SalesCommandMessages.NotReady);

            var request = new ShippingSettingsPutRequest(
                expectedVersion, threshold, fee, envelope.ReplicaId!.Value, SalesSyncEnvelope.Protocol, envelope.SeenMode);
            var http = await _api.PutShippingSettingsAsync(licenseId.Value, request, ct).ConfigureAwait(false);
            switch (http.Transport)
            {
                case PaymentRequestTransport.Ok:
                    SaveCache(http.Response!);
                    _log.LogInformation("Kargo ayarı lisans geneli yazıldı: sürüm {Expected} → {Version}",
                        expectedVersion, http.Response!.Version);
                    return new(ShippingSettingsSaveStatus.Saved, null, http.Response);
                case PaymentRequestTransport.EndpointMissing:
                    _log.LogWarning("Kargo ayarı yazılmadı: shipping-settings ucu yok (404)");
                    return new(ShippingSettingsSaveStatus.EndpointMissing, SalesCommandMessages.EndpointMissing);
                case PaymentRequestTransport.Offline:
                    _log.LogInformation("Kargo ayarı yazılmadı: sunucuya ulaşılamadı");
                    return new(ShippingSettingsSaveStatus.Offline, SalesCommandMessages.Offline);
                case PaymentRequestTransport.Failed:
                    _log.LogWarning("Kargo ayarı yazılmadı: sunucu hatası (HTTP {Status}): {Error}", http.StatusCode, http.Error);
                    return new(ShippingSettingsSaveStatus.Failed, SalesCommandMessages.Offline);
            }

            var problem = http.Problem!;
            switch (problem.Code)
            {
                case PaymentRequestProblemCodes.Stale:
                    SaveCache(problem.Current!);
                    _log.LogInformation("Kargo ayarı bayat: beklenen sürüm {Expected}, güncel {Version}; önbellek güncellendi",
                        expectedVersion, problem.Current!.Version);
                    return new(ShippingSettingsSaveStatus.Stale, SalesCommandMessages.ShippingSettingsStale, problem.Current);
                case PaymentRequestProblemCodes.ModeOff:
                    _log.LogInformation("Kargo ayarı mode-off aldı: Off turu isteniyor, kargo yazılmadı");
                    RequestModeOffRound();
                    return new(ShippingSettingsSaveStatus.ModeOff, SalesCommandMessages.ModeOff);
                default:
                    _log.LogWarning("Kargo ayarı reddedildi (HTTP {Status}, {Code})", http.StatusCode, problem.Code);
                    return new(ShippingSettingsSaveStatus.Rejected, SalesCommandMessages.ContentConflict);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Kargo ayarı yazımı beklenmedik hatayla düştü");
            return new(ShippingSettingsSaveStatus.Failed, SalesCommandMessages.Offline);
        }
    }

    /// <summary>Sunucunun satırı önbelleğe. Yazılamazsa sonuç değişmez (sunucu yazdı ya da güncel satırı verdi); sonraki
    /// <c>sales/state</c> turu önbelleği zaten yeniler.</summary>
    private void SaveCache(ShippingSettingsSnapshot row)
    {
        try { _state.SaveShippingCache(row.Version, row.FreeShippingThreshold, row.Fee); }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Kargo ayarının önbelleği yazılamadı (sürüm {Version}); sonraki sales/state turu yeniler",
                row.Version);
        }
    }

    private void RequestModeOffRound()
    {
        try { _requestModeOffRound(); }
        catch (Exception ex) { _log.LogWarning(ex, "Kargo ayarı: mode-off sonrası Off turu istenemedi; sonraki state turu görür"); }
    }

    /// <returns>LicenseId; çözülemezse null ve sebebin ağ olup olmadığı.</returns>
    private async Task<(Guid? Id, bool Offline)> ResolveLicenseIdAsync(string licenseKey, CancellationToken ct)
    {
        if (_cachedLicenseId is { } cached && _cachedLicenseKey == licenseKey) return (cached, false);
        try
        {
            var licenses = await _api.GetMyLicensesAsync(ct).ConfigureAwait(false);
            var match = licenses.FirstOrDefault(l => l.LicenseKey == licenseKey);
            if (match?.Id is null) return (null, false);
            _cachedLicenseId = match.Id;
            _cachedLicenseKey = licenseKey;
            return (match.Id, false);
        }
        catch (LicenseApiNetworkException)
        {
            return (null, true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogDebug(ex, "Kargo ayarı: LicenseId çözülemedi");
            return (null, false);
        }
    }
}
