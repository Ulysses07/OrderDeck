using System.Diagnostics;
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

/// <summary>"Gönder ve kapat"ın bir adımı.</summary>
/// <param name="Name">Günlükte adımın adı (kişisel veri yok).</param>
/// <param name="Run">Gönderim; belirteç adımın payı dolunca iptal olur.</param>
/// <param name="MaxShare">Toplam bütçenin bu adıma düşen en büyük payı (0–1). null = kalan süreyi kalan
/// adımlarla eşit paylaşır (<see cref="SyncFlushService.StepAllowance"/>).</param>
public sealed record FlushStep(string Name, Func<CancellationToken, Task> Run, double? MaxShare = null);

/// <summary>
/// "Gönder ve kapat" (Faz 0, D5): gönderim servislerini sırayla, toplam süre sınırıyla koşar.
/// Servisler zamanlayıcıyla da koşuyor olabilir; müşteri gönderimi tek tur kilidinden geçer (C6 —
/// süren turu bekler, aynı parti iki kez gitmez), diğerlerinde aynı anda iki tur zararsız (sunucu
/// tarafı upsert'ler idempotent, onaylar Revision CAS'li). Gönderim yanıtındaki yönlendirmeler yerel
/// taşıma yapar; ödeme akışındaki müşteri ertelenir (U13) — zararsız.
///
/// <para><b>Kuyruğu boşaltma:</b> müşteri gönderimi tur içinde parti parti boşaltır; oturum+etiket
/// (50/100), ödeme (50) ve kargo (50) turda TEK parti gönderir — kapanışta <see cref="Drain"/> ile
/// gönderdikçe yinelenir.</para>
///
/// <para><b>Bütçe payı:</b> müşteri adımı toplam bütçenin en çok yarısını alır
/// (<see cref="CustomerShare"/>; D5b'den ya da ilk biçim-2 gönderiminden sonra kuyruğu çok büyük
/// olabilir), kalan adımlar kalan süreyi paylaşır — erken biten adımın artığı sonrakilere kalır.
/// Payı dolan adım bırakılır, sıradakine geçilir.</para>
///
/// <para><b>İş parçacığı ve bütçe:</b> her adım <see cref="Task.Run(Func{Task})"/> ile başlar —
/// müşteri gönderimi ilk gerçek <c>await</c>'ine kadar (tur kilidi, lisans, <c>GetForPush</c>,
/// taşımanın <c>CustomerBusySet</c> beklemesi) çağıranın iş parçacığında koşardı ve kapanış arayüz
/// iş parçacığından başlar (C4/C6 incelemesi). Bütçe KESİN: belirteci dinlemeyen ya da eşzamanlı
/// bekleyen bir adım beklenmez — kayıtlar kaybolmaz, sonraki açılışta gider. Bırakılan adım arka
/// planda biter ya da süreçle birlikte kesilir (SQLite yazımı işlem bütünlüğünde, sunucu gönderimi
/// idempotent).</para>
///
/// <para><b>Hata yalıtımı:</b> <see cref="FlushAsync"/> fırlatmaz; bir adımın hatası (ör. gönderimin
/// yerel SQLite hatası — HTTP hatasını servisler kendileri yutar) günlüğe yazılır, sıradakine
/// geçilir. Kapanışı hiçbir şey engellemez.</para>
/// </summary>
public sealed class SyncFlushService
{
    /// <summary>Kapanıştaki "gönder ve kapat"ın toplam süresi; uyarı metni de bunu söyler.</summary>
    public static readonly TimeSpan CloseBudget = TimeSpan.FromSeconds(30);

    /// <summary>Müşteri adımının toplam bütçedeki en büyük payı.</summary>
    public const double CustomerShare = 0.5;

    /// <summary><see cref="Drain"/>'in en çok tur sayısı: onayı hep ıskalayan bir satır (MarkSynced'in
    /// Revision CAS'i tutmuyor) her turda yeniden gider — sonsuz döngü olmasın.</summary>
    public const int MaxDrainRounds = 20;

    private readonly IReadOnlyList<FlushStep> _steps;
    private readonly ILogger<SyncFlushService> _log;

    public SyncFlushService(IReadOnlyList<FlushStep> steps, ILogger<SyncFlushService>? log = null)
    {
        _steps = steps;
        _log = log ?? NullLogger<SyncFlushService>.Instance;
    }

    /// <summary>Kapanış gönderimi: bekleyen sayıya (D1) giren her gönderim, bu sırayla. Sunucu
    /// siparişleri oturumdan sonra kabul ediyor (unknown-session); müşteriler siparişlerden önce
    /// yönlensin diye en başta.</summary>
    public static SyncFlushService ForClose(
        WpfCustomerProjectionSyncService customers,
        SessionOrderSyncService sessionsAndOrders,
        PaymentSyncService payments,
        ShipmentSyncService shipments,
        ILogger<SyncFlushService>? log = null)
        => new(new FlushStep[]
        {
            new("müşteri", ct => customers.SyncOnceAsync(ct), CustomerShare),
            new("oturum ve etiket", Drain(async ct =>
            {
                var r = await sessionsAndOrders.SyncOnceAsync(ct).ConfigureAwait(false);
                return r.SessionsPushed + r.OrdersPushed;
            })),
            new("ödeme", Drain(async ct => (await payments.SyncOnceAsync(ct).ConfigureAwait(false)).Pushed)),
            new("kargo", Drain(async ct => (await shipments.SyncOnceAsync(ct).ConfigureAwait(false)).Pushed)),
        }, log);

    /// <summary>Turda tek parti gönderen servis için: tur bir şey gönderdikçe, belirteç canlıyken ve
    /// en çok <paramref name="maxRounds"/> kez yineler.</summary>
    /// <param name="pushOnce">Bir tur; gönderilen kayıt sayısını döner.</param>
    public static Func<CancellationToken, Task> Drain(
        Func<CancellationToken, Task<int>> pushOnce, int maxRounds = MaxDrainRounds)
        => async ct =>
        {
            for (var round = 0; round < maxRounds && !ct.IsCancellationRequested; round++)
                if (await pushOnce(ct).ConfigureAwait(false) <= 0) return;
        };

    /// <summary>Adımın payı: açık payı varsa toplamın o kadarı, yoksa kalan sürenin kalan adımlara
    /// (bu adım dahil) eşit bölümü; hiçbir zaman kalan süreden çok değil.</summary>
    internal static TimeSpan StepAllowance(TimeSpan budget, TimeSpan remaining, double? maxShare, int stepsLeft)
    {
        if (remaining <= TimeSpan.Zero) return TimeSpan.Zero;
        var share = maxShare is { } s ? budget * s : remaining / Math.Max(1, stepsLeft);
        return share < remaining ? share : remaining;
    }

    public async Task FlushAsync(TimeSpan budget)
    {
        var elapsed = Stopwatch.StartNew();
        // Bırakılan bir adım belirteci hâlâ tutuyor olabilir: kaynaklar yalnız hiçbir adım
        // bırakılmadıysa elden çıkarılır (süresi dolmuş zamanlayıcı kaynağı bekletmez).
        var overall = new CancellationTokenSource(budget);
        var abandoned = false;
        try
        {
            for (var i = 0; i < _steps.Count; i++)
            {
                var allowance = StepAllowance(budget, budget - elapsed.Elapsed, _steps[i].MaxShare, _steps.Count - i);
                if (overall.IsCancellationRequested || allowance <= TimeSpan.Zero)
                {
                    LogBudgetExhausted(i);
                    return;
                }
                var step = _steps[i];
                var stepCts = CancellationTokenSource.CreateLinkedTokenSource(overall.Token);
                stepCts.CancelAfter(allowance);
                var run = Task.Run(() => step.Run(stepCts.Token));
                try
                {
                    await run.WaitAsync(stepCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stepCts.IsCancellationRequested)
                {
                    if (!run.IsCompleted)
                    {
                        abandoned = true;
                        // Bırakılan adımın sonraki hatası gözlenmemiş görev uyarısına düşmesin.
                        _ = run.ContinueWith(static t => _ = t.Exception, CancellationToken.None,
                            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                            TaskScheduler.Default);
                    }
                    else stepCts.Dispose();
                    if (overall.IsCancellationRequested)
                    {
                        LogBudgetExhausted(i);
                        return;
                    }
                    _log.LogWarning(
                        "Kapanış gönderimi: {Step} payına düşen sürede bitmedi; kalanı sonraki açılışta, sıradakine geçiliyor",
                        step.Name);
                    continue;
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Kapanış gönderimi: {Step} hata verdi; sıradakine geçiliyor", step.Name);
                }
                stepCts.Dispose();                           // adım bitti (başarıyla ya da hatayla)
            }
            _log.LogInformation("Kapanış gönderimi bitti ({Count} adım)", _steps.Count);
        }
        finally
        {
            if (!abandoned) overall.Dispose();
        }
    }

    private void LogBudgetExhausted(int step)
        => _log.LogWarning(
            "Kapanış gönderimi süre sınırında durdu ({Step} ve sonrası); kalan kayıtlar sonraki açılışta gönderilir",
            _steps[step].Name);
}
