using Microsoft.Extensions.Logging;
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
}

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
/// <para>Öbür yönler bu görevin dışındadır: sunucu modu 0 iken yerel kip 1 (<c>disable</c> turu, §8.5) burada
/// <see cref="SalesModeStep.None"/> döner.</para>
///
/// <para>Hatalar turu düşürmez (iptal hariç); günlük yalnız kimlik, kuyruk adı ve sayı taşır. Bekleyen gönderim
/// uyarısı bir serinin başında bir kez, arası Debug.</para>
/// </summary>
public sealed class SalesModeCoordinator
{
    private readonly SalesSyncStateService _state;
    private readonly SalesSyncStateRepository _stateRepo;
    private readonly ISalesOpeningFlush _flush;
    private readonly IClock _clock;
    private readonly ILogger<SalesModeCoordinator> _log;

    /// <summary>Süren bir "açılıyor" serisi: ilk turu Information, arası Debug.</summary>
    private bool _openingLogged;

    public SalesModeCoordinator(
        SalesSyncStateService state,
        SalesSyncStateRepository stateRepo,
        ISalesOpeningFlush flush,
        IClock clock,
        ILogger<SalesModeCoordinator> log)
    {
        _state = state;
        _stateRepo = stateRepo;
        _flush = flush;
        _clock = clock;
        _log = log;
    }

    /// <summary>
    /// Bu turun kip adımı. Çağıran aynı turda başarılı bir <c>sales/state</c> almış olmalı (son durum
    /// <see cref="SalesSyncStateService.LastState"/>, bu lisansın). Satış akışı turunun tur kilidi altında koşar.
    /// </summary>
    public async Task<SalesModeStep> RunAsync(string licenseKey, CancellationToken ct)
    {
        if (_state.LastState is not { } snapshot || snapshot.LicenseKey != licenseKey) return SalesModeStep.None;

        int local;
        try { local = _stateRepo.GetMode(); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Satış senkronu kip adımı: yerel kip okunamadı; geçiş sonraki tura kaldı");
            return SalesModeStep.None;
        }

        if (snapshot.Mode != 1 || local != 0)
        {
            _openingLogged = false;
            return SalesModeStep.None;
        }
        return await OpenAsync(licenseKey, snapshot, ct).ConfigureAwait(false);
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
