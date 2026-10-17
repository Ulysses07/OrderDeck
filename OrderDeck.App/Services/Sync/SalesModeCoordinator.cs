using Microsoft.Extensions.Logging;
using OrderDeck.Core.Sales;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Core.Time;

namespace OrderDeck.App.Services.Sync;

/// <summary>Kip geçişi adımının sonucu (<see cref="SalesModeCoordinator.RunAsync"/>).</summary>
public enum SalesModeStep
{
    /// <summary>Geçiş yok: sunucu ile yerel kip uyuşuyor (ya da bu derlemenin işlemediği yön), state bu lisansın
    /// değil ya da kip okunamadı.</summary>
    None,

    /// <summary>Sunucu modu 1, yerel kip 0 kaldı: bir giden kuyruk boşalmadı ya da kilit altında yeniden dolu
    /// bulundu. Durum satırı "Senkron açılıyor; bekleyen gönderim var"; sonraki tur 1. adımdan yeniden dener.</summary>
    OpeningPending,

    /// <summary>Bu adımda yerel <c>Mode = 1</c> yazıldı; akış 0'dan, ilk yetişme başladı.</summary>
    Enabled,

    /// <summary><c>disable</c> turu (§8.5; W2-11): sunucu modu 0 ve yerel kip 1 (tam tur), ya da yerel kip 0 ve
    /// <c>modeChangedAt</c> yerelde son görülenden yeni (mod On dönemini hiç görmemiş Ws: 1. ve 2. adım). Tur son akışı
    /// boş sayfaya kadar dosyalarla uygular (1. adım), ardından <see cref="SalesModeCoordinator.CompleteModeOff"/>.</summary>
    Disabling,
}

/// <summary><see cref="SalesModeCoordinator.CompleteModeOff"/>'un sonucu.</summary>
/// <param name="Local">2. adım (<see cref="SalesFeedRepository.ApplyModeOff"/>); <see cref="SalesModeOffResult.Busy"/> ise
/// hiçbir şey yazılmadı, tur sonraki seferde 1. adımdan yeniden dener.</param>
/// <param name="Conversion">3. adım: bekleyen komutların yerel yazıma çevrilmesi.</param>
/// <param name="WasOn">Tur başında yerel kip 1'di (tam tur); değilse mod On dönemini görmemiş kopya (1. ve 2. adım).</param>
public sealed record SalesModeOffCompletion(SalesModeOffResult Local, SalesPendingConversion Conversion, bool WasOn);

/// <summary>
/// Satış senkronunun kip geçişleri (spec 2026-10-13 §8.4; W2-10). Satış akışı turu
/// (<see cref="SalesChangesPullService"/>) her turda, BAŞARILI <c>sales/state</c>'in hemen ardından, bekleyen
/// komutlardan ve akıştan önce çağırır.
///
/// <para><b>Mod On'daki ilk tur</b> (sunucu modu 1, yerel kip 0):
/// <list type="number">
///   <item>Bütün giden kuyruklar görülen kip 0 ile boşaltılır (<see cref="ISalesOpeningFlush"/>). Biri boşalmazsa ya
///   da yerel hata olursa yerel kip 0 kalır (<see cref="SalesModeStep.OpeningPending"/>).</item>
///   <item>Kargo ayarı aynı turun state'inde önbelleğe alındı (<see cref="SalesSyncStateService"/>, başarılı state
///   ön koşul). TEK işlemde, kilit altında kargo ve canlı olmayan yayınların etiket kuyruğu yeniden denetlenir;
///   boşsa <c>Mode = 1</c>, <c>ModeSeenAt = modeChangedAt</c>, devri yerelde bilinen kendi bitmiş yayınları
///   <c>server</c> (<see cref="SalesSyncStateRepository.TryEnableMode"/>).</item>
///   <item>Aynı işlemde akış imleci 0: tur akışı baştan, dosyalarla ve ilk yetişme sayımıyla uygular.</item>
///   <item>Ufukta (boş sayfa, bekleyen öğe yok) <see cref="CompleteFirstCatchUp"/>: toplamlar tek işlemde türetilir,
///   <c>FirstCatchUpAt</c> yazılır.</item>
/// </list>
/// O anda canlı bir yayın varsa yerel kalır ve normal yoldan (kapanış gönderimi) devreder.</para>
///
/// <para><b><c>disable</c> turu</b> (sunucu modu 0; §8.5, §2 "Görülen kip", M15; W2-11): yerel kip 1 ise ya da yerel kip 0
/// ve <c>modeChangedAt</c> yerelde son görülenden (<c>ModeSeenAt</c>) yeniyse <see cref="SalesModeStep.Disabling"/>:
/// <list type="number">
///   <item>Tur son akışı boş sayfaya kadar uygular — dosya öğeleri dahil, yalnız kendi satırları
///   (<see cref="SalesFeedApplyOptions.OwnRowsOnly"/>): tutulacak dosyaların sunucudaki son hâli (başka bilgisayarın
///   komutları, yeniden yazılmış <c>OriginReplicaId</c>) ve kendi etiketlerinin komut sonuçları iner.</item>
///   <item><see cref="CompleteModeOff"/>: tek kilitli işlemde yerel kip 0 ve görülen an, kendi yayınları yerel, uzak
///   satırlar silinir, toplamlar türetilir, başka kopyanın olacak açık dosyalar silinir; ardından lisans geneli kargo
///   ayarı yerel ayara (<see cref="SalesModeOffRound.ApplyLocal"/>).</item>
///   <item>Bekleyen komutlar kilit dışında olağan yerel yazıma çevrilir
///   (<see cref="SalesModeOffRound.ConvertPendingCommands"/>). Yarıda kalırsa açılışta ve her turun başında yeniden
///   (<see cref="ResumeModeOff"/>); görülen kip ancak yerel kip 0 ∧ kuyruk boş olunca 0 olur.</item>
/// </list>
/// Komut ucunun <c>mode-off</c>'u satış akışı turunu hemen tetikler
/// (<see cref="SalesChangesPullService.RequestModeOffRound"/>): state okunur, bu adım turu başlatır. Tur bileşeni
/// (<see cref="SalesModeOffRound"/>) verilmemişse (eski testler) <c>disable</c> turu hiç başlamaz.</para>
///
/// <para>Hatalar turu düşürmez (iptal hariç); günlük yalnız kimlik, kuyruk adı ve sayı taşır. Bekleyen gönderim
/// uyarısı bir serinin başında bir kez, arası Debug.</para>
/// </summary>
public sealed class SalesModeCoordinator
{
    private readonly SalesSyncStateService _state;
    private readonly SalesSyncStateRepository _stateRepo;
    private readonly SalesSyncEnvelope _envelope;
    private readonly ISalesOpeningFlush _flush;
    private readonly IClock _clock;
    private readonly ILogger<SalesModeCoordinator> _log;
    private readonly SalesModeOffRound? _modeOff;

    /// <summary>Süren bir "açılıyor" serisi: ilk turu Warning, arası Debug.</summary>
    private bool _openingLogged;

    /// <param name="modeOff"><c>disable</c> turunun yerel adımları (W2-11). DI tekil örneği verir; null ise (eski
    /// testler) <c>disable</c> turu hiç başlamaz.</param>
    public SalesModeCoordinator(
        SalesSyncStateService state,
        SalesSyncStateRepository stateRepo,
        SalesSyncEnvelope envelope,
        ISalesOpeningFlush flush,
        IClock clock,
        ILogger<SalesModeCoordinator> log,
        SalesModeOffRound? modeOff = null)
    {
        _state = state;
        _stateRepo = stateRepo;
        _envelope = envelope;
        _flush = flush;
        _clock = clock;
        _log = log;
        _modeOff = modeOff;
    }

    /// <summary>
    /// Bu turun kip adımı. Çağıran aynı turda başarılı bir <c>sales/state</c> almış olmalı (son durum
    /// <see cref="SalesSyncStateService.LastState"/>, bu lisansın). Satış akışı turunun tur kilidi altında koşar.
    ///
    /// <para><b>Geri yüklemeden sonra (§8.7):</b> adım, işe başlamadan zarfa bu (lisans, kopya) için "kip tazelendi"
    /// yazar (<see cref="SalesSyncEnvelope.IsReadyForPush"/>'un üçüncü koşulu): ondan önce geri yüklenmiş süreçte hiçbir
    /// satış gönderimi gitmez; ondan sonra açılışın kendi gönderimi (§8.4-1) de gidebilir.</para>
    /// </summary>
    public async Task<SalesModeStep> RunAsync(string licenseKey, CancellationToken ct)
    {
        if (_state.LastState is not { } snapshot || snapshot.LicenseKey != licenseKey) return SalesModeStep.None;
        MarkModeRefreshed(licenseKey);

        SalesSyncStateRow row;
        try { row = _stateRepo.Get(); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Satış senkronu kip adımı: yerel kip okunamadı; geçiş sonraki tura kaldı");
            return SalesModeStep.None;
        }

        if (snapshot.Mode == 0)
        {
            _openingLogged = false;
            return IsModeOffRoundDue(snapshot, row) ? SalesModeStep.Disabling : SalesModeStep.None;
        }
        if (snapshot.Mode != 1 || row.Mode != 0)
        {
            _openingLogged = false;
            return SalesModeStep.None;
        }
        return await OpenAsync(licenseKey, snapshot, ct).ConfigureAwait(false);
    }

    /// <summary>Sunucu modu 0: yerel kip 1 (tam tur) ya da yerel kip 0 ve <c>modeChangedAt</c> yerelde son görülenden yeni
    /// (mod On dönemini hiç görmemiş Ws, §8.5: dönemde başka bilgisayarın onun etiketleriyle açtığı dosya ona geçmiş
    /// olabilir). Tur bileşeni yoksa hiçbiri.</summary>
    private bool IsModeOffRoundDue(SalesStateSnapshot snapshot, SalesSyncStateRow row)
    {
        if (_modeOff is null) return false;
        if (row.Mode == 1) return true;
        return snapshot.ModeChangedAt is { } changed
               && (row.ModeSeenAt is not { } seen || changed.ToUnixTimeSeconds() > seen);
    }

    /// <summary>
    /// <c>disable</c> turunun 2. ve 3. adımı (§8.5): satış akışı turu son akışı boş sayfaya kadar uyguladığında çağırır.
    /// Çağıranın turundaki <c>sales/state</c> bu lisansın ve modu 0 olmalı. Hata turu düşürmez; günlük yalnız sayı.
    /// </summary>
    /// <returns>Adımın sonucu; tur bileşeni yoksa, state uymuyorsa ya da 2. adım yerel hatayla düştüyse <c>null</c>
    /// (tur sonraki seferde 1. adımdan yeniden dener).</returns>
    public SalesModeOffCompletion? CompleteModeOff(string licenseKey, Guid replicaId)
    {
        if (_modeOff is null) return null;
        if (_state.LastState is not { } snapshot || snapshot.LicenseKey != licenseKey || snapshot.Mode != 0) return null;

        bool wasOn;
        SalesModeOffResult local;
        try
        {
            wasOn = _stateRepo.GetMode() == 1;
            local = _modeOff.ApplyLocal(snapshot.ModeChangedAt?.ToUnixTimeSeconds(), replicaId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Satış senkronu Off turu: yerel adım yazılamadı; sonraki turda yeniden");
            return null;
        }
        if (local.Busy)
        {
            _log.LogInformation("Satış senkronu Off turu: uzak etiketlerin müşterisi ödeme akışında; sonraki turda yeniden");
            return new SalesModeOffCompletion(local, SalesPendingConversion.None, wasOn);
        }

        var conversion = ConvertPending();
        _log.LogInformation(
            "Satış senkronu mod Off'a döndü ({Round}): {ToLocal} kendi yayını yerel otoriteye döndü; {Sessions} uzak oturum, "
          + "{Labels} uzak etiket, {Live} canlı yayın başlığı ve {Shipments} başka kopyanın açık dosyası silindi; {Rows} "
          + "müşterinin toplamı türetildi; {Converted} bekleyen komut yerel yazıma çevrildi, {Remaining} kaldı",
            wasOn ? "tam tur" : "mod On dönemini görmemiş kopya", local.SessionsToLocal, local.Sessions, local.Labels,
            local.LiveSessions, local.Shipments, local.Aggregates.ChangedRows, conversion.Converted, conversion.Remaining);
        return new SalesModeOffCompletion(local, conversion, wasOn);
    }

    /// <summary>
    /// Açılışta ve her satış akışı turunun başında (§8.5): yerel kip 0 ∧ bekleyen komut dolu ise 3. adım yeniden koşar
    /// (süreç 2. ile 3. adım arasında kapanmış); yarıda kalmış kargo ayarı yazımı da. Hata turu düşürmez.
    /// </summary>
    public SalesPendingConversion ResumeModeOff()
    {
        if (_modeOff is null) return SalesPendingConversion.None;
        try { return _modeOff.Resume(); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Satış senkronu Off turu: bekleyen komutlar okunamadı; sonraki turda yeniden");
            return SalesPendingConversion.None;
        }
    }

    private SalesPendingConversion ConvertPending()
    {
        try { return _modeOff!.ConvertPendingCommands(); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Satış senkronu Off turu: bekleyen komutlar okunamadı; sonraki turda yeniden");
            return SalesPendingConversion.None;
        }
    }

    private async Task<SalesModeStep> OpenAsync(string licenseKey, SalesStateSnapshot snapshot, CancellationToken ct)
    {
        // 1) Giden kuyruklar, görülen kip 0 ile.
        SalesOpeningFlushResult flush;
        try
        {
            flush = await _flush.FlushAsync(licenseKey, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogPending(ex, "giden kuyruklar boşaltılırken yerel hata");
            return SalesModeStep.OpeningPending;
        }
        if (!flush.Flushed)
        {
            LogPending(null, $"{flush.BlockedQueue} kuyruğunda {flush.Remaining} kayıt gönderilemedi");
            return SalesModeStep.OpeningPending;
        }

        // 2–3) Kilit altında yeniden denetim ve tek işlemdeki yazım (imleç dahil).
        SalesModeEnableResult result;
        try
        {
            result = _stateRepo.TryEnableMode(snapshot.ModeChangedAt?.ToUnixTimeSeconds(),
                SalesChangesPullService.CursorName, licenseKey, _clock.UnixNow());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogPending(ex, "yerel kip yazılamadı");
            return SalesModeStep.OpeningPending;
        }

        switch (result.Status)
        {
            case SalesModeEnableStatus.Backlog:
                LogPending(null,
                    $"kilit altında {result.PendingShipments} kargo dosyası ve {result.PendingLabels} etiket yeniden bekliyor");
                return SalesModeStep.OpeningPending;
            case SalesModeEnableStatus.AlreadyOn:
                _openingLogged = false;
                return SalesModeStep.None;
            default:
                _openingLogged = false;
                _log.LogInformation(
                    "Satış senkronu mod On açıldı: giden kuyruklar boşaltıldı, {Sessions} kendi yayını sunucuya geçti; akış baştan alınıyor",
                    result.SessionsToServer);
                return SalesModeStep.Enabled;
        }
    }

    /// <summary>
    /// Açılışın 4. adımı (§8.4-4, §6.1): satış akışı turu akışı ufka kadar uyguladığında çağırır. İlk yetişme sürüyorsa
    /// (yerel kip 1, <c>FirstCatchUpAt</c> boş) toplamlar tek işlemde türetilir ve <c>FirstCatchUpAt</c> yazılır
    /// (<see cref="SalesSyncStateRepository.CompleteFirstCatchUp"/>). Hata turu düşürmez: sonraki ufukta yeniden.
    /// </summary>
    /// <returns>Bu çağrı ilk yetişmeyi tamamladıysa sonuç (üstüne yazım sayısıyla); aksi hâlde <c>null</c>.</returns>
    public SalesFirstCatchUpResult? CompleteFirstCatchUp()
    {
        SalesFirstCatchUpResult? result;
        try { result = _stateRepo.CompleteFirstCatchUp(_clock.UnixNow()); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Satış senkronunun ilk yetişmesi tamamlanamadı (toplamlar türetilemedi); sonraki turda yeniden");
            return null;
        }
        if (result is null) return null;

        _log.LogInformation(
            "Satış senkronunun ilk yetişmesi tamamlandı: {Rows} müşterinin toplamı türetildi (toplam fark {Diff:0.00} TL), {Overwrites} etikette sunucunun değeri alındı",
            result.Aggregates.ChangedRows, result.Aggregates.TotalAbsDiff, result.CatchUpOverwrites);
        return result;
    }

    /// <summary>Tazelenmiş kip bu (lisans, kopya) için görüldü: yalnız aynı süreçte state'i başarıyla gelmiş kopyada.</summary>
    private void MarkModeRefreshed(string licenseKey)
    {
        try
        {
            if (_envelope.ReplicaId is { } replica && _envelope.HasStateSucceeded(licenseKey, replica))
                _envelope.MarkModeRefreshed(licenseKey, replica);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogDebug(ex, "Satış senkronu kip adımı: kopya kimliği okunamadı; gönderim kapısı sonraki turda");
        }
    }

    private void LogPending(Exception? ex, string reason)
    {
        if (!_openingLogged)
        {
            _openingLogged = true;
            _log.LogWarning(ex,
                "Satış senkronu açılıyor (sunucu mod 1): {Reason}; yerel kip 0 kaldı, sonraki turda yeniden denenecek", reason);
        }
        else
            _log.LogDebug(ex, "Satış senkronu hâlâ açılıyor: {Reason}", reason);
    }
}
