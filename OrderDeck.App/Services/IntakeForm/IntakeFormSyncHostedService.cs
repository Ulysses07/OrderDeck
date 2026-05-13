using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace OrderDeck.App.Services.IntakeForm;

/// <summary>
/// PeriodicTimer loop calling IntakeFormSyncService.SyncOnceAsync.
/// Phase 4b HeartbeatHostedService pattern.
///
/// UI freeze fix (2026-05-13): adaptive backoff. Auth hatası art arda gelirse
/// normal aralık 2 dk yerine 15 dk'ya çıkar — 401 exception storm UI thread'e
/// yansıyıp donma yapıyordu (dünkü 20:09-20:41 yayın penceresinde 13 art arda
/// 401 hatası vardı). Kullanıcı tekrar login olunca veya başka bir sync başarılı
/// olunca normal aralığa döner.
/// </summary>
public sealed class IntakeFormSyncHostedService : BackgroundService
{
    private static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan AuthFailureInterval = TimeSpan.FromMinutes(15);

    private readonly IntakeFormSyncService _syncService;
    private readonly ILogger<IntakeFormSyncHostedService> _log;
    private readonly TimeSpan _normalInterval;
    private readonly TimeSpan _authFailureInterval;

    public IntakeFormSyncHostedService(
        IntakeFormSyncService syncService,
        ILogger<IntakeFormSyncHostedService> log)
        : this(syncService, log, DefaultInterval, AuthFailureInterval) { }

    internal IntakeFormSyncHostedService(
        IntakeFormSyncService syncService,
        ILogger<IntakeFormSyncHostedService> log,
        TimeSpan normalInterval,
        TimeSpan? authFailureInterval = null)
    {
        _syncService = syncService;
        _log = log;
        _normalInterval = normalInterval;
        _authFailureInterval = authFailureInterval ?? AuthFailureInterval;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // PeriodicTimer ile değil, dinamik aralıklı Task.Delay kullanıyoruz
        // çünkü auth-failure sonrası aralık 2 dk yerine 15 dk olmalı.
        while (!stoppingToken.IsCancellationRequested)
        {
            var nextDelay = _syncService.LastSyncWasAuthFailure
                ? _authFailureInterval
                : _normalInterval;

            try
            {
                await Task.Delay(nextDelay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                await _syncService.SyncOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Intake form sync tick failed; will retry next interval");
            }
        }
    }
}
