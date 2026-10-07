using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace OrderDeck.App.Services.Sync;

/// <summary>Kapanışta gönderilmemiş kayıt uyarısının sonucu (D5).</summary>
public enum CloseSyncChoice
{
    /// <summary>Sorulmadı ya da "Hayır: yine de kapat" — kayıtlar yerelde kalır, sonraki açılışta gider.</summary>
    Close,
    /// <summary>"İptal: kapatma".</summary>
    Cancel,
    /// <summary>"Evet: gönder ve kapat" — <see cref="SyncFlushService.FlushAsync"/>, sonra kapat.</summary>
    FlushThenClose,
}

/// <summary>
/// "Gönder ve kapat" (Faz 0, D5): gönderim servislerini birer kez, toplam süre sınırıyla koşar.
/// Servisler zamanlayıcıyla da koşuyor olabilir; müşteri gönderimi tek tur kilidinden geçer (C6 —
/// süren turu bekler, aynı parti iki kez gitmez), diğerlerinde aynı anda iki tur zararsız (sunucu
/// tarafı upsert'ler idempotent, onaylar Revision CAS'li). Gönderim yanıtındaki yönlendirmeler yerel
/// taşıma yapar; ödeme akışındaki müşteri ertelenir (U13) — zararsız.
///
/// <para><b>İş parçacığı ve bütçe:</b> her adım <see cref="Task.Run(Func{Task})"/> ile başlar —
/// müşteri gönderimi ilk gerçek <c>await</c>'ine kadar (tur kilidi, lisans, <c>GetForPush</c>,
/// taşımanın <c>CustomerBusySet</c> beklemesi) çağıranın iş parçacığında koşardı ve kapanış arayüz
/// iş parçacığından başlar (C4/C6 incelemesi). Bütçe KESİN: belirteci dinlemeyen ya da eşzamanlı
/// bekleyen bir adım beklenmez, kalan adımlar atlanır — kayıtlar kaybolmaz, sonraki açılışta gider.
/// Bırakılan adım arka planda biter ya da süreçle birlikte kesilir (SQLite yazımı işlem bütünlüğünde,
/// sunucu gönderimi idempotent).</para>
///
/// <para><b>Hata yalıtımı:</b> <see cref="FlushAsync"/> fırlatmaz; bir adımın hatası (ör. gönderimin
/// yerel SQLite hatası — HTTP hatasını servisler kendileri yutar) günlüğe yazılır, sıradakine
/// geçilir. Kapanışı hiçbir şey engellemez.</para>
/// </summary>
public sealed class SyncFlushService
{
    /// <summary>Kapanıştaki "gönder ve kapat"ın toplam süresi; uyarı metni de bunu söyler.</summary>
    public static readonly TimeSpan CloseBudget = TimeSpan.FromSeconds(30);

    private readonly IReadOnlyList<Func<CancellationToken, Task>> _steps;
    private readonly ILogger<SyncFlushService> _log;

    /// <param name="steps">Sırayla koşan gönderim adımları (AppHost: müşteri, oturum+etiket, ödeme,
    /// kargo — bekleyen sayıya giren her gönderim).</param>
    public SyncFlushService(IReadOnlyList<Func<CancellationToken, Task>> steps, ILogger<SyncFlushService>? log = null)
    {
        _steps = steps;
        _log = log ?? NullLogger<SyncFlushService>.Instance;
    }

    public async Task FlushAsync(TimeSpan budget)
    {
        // Bırakılan bir adım belirteci hâlâ tutuyor olabilir: kaynak yalnız hiçbir adım bırakılmadıysa
        // elden çıkarılır (süresi dolmuş zamanlayıcı kaynağı bekletmez).
        var cts = new CancellationTokenSource(budget);
        var abandoned = false;
        try
        {
            for (var i = 0; i < _steps.Count; i++)
            {
                if (cts.IsCancellationRequested)
                {
                    LogBudgetExhausted(i);
                    return;
                }
                var step = _steps[i];
                var run = Task.Run(() => step(cts.Token));
                try
                {
                    await run.WaitAsync(cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cts.IsCancellationRequested)
                {
                    abandoned = !run.IsCompleted;
                    // Bırakılan adımın sonraki hatası gözlenmemiş görev uyarısına düşmesin.
                    _ = run.ContinueWith(static t => _ = t.Exception, CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                    LogBudgetExhausted(i);
                    return;
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Kapanış gönderimi: adım {Step}/{Count} hata verdi; sıradakine geçiliyor",
                        i + 1, _steps.Count);
                }
            }
            _log.LogInformation("Kapanış gönderimi bitti ({Count} adım)", _steps.Count);
        }
        finally
        {
            if (!abandoned) cts.Dispose();
        }
    }

    private void LogBudgetExhausted(int step)
        => _log.LogWarning(
            "Kapanış gönderimi süre sınırında durdu (adım {Step}/{Count}); kalan kayıtlar sonraki açılışta gönderilir",
            step + 1, _steps.Count);
}
