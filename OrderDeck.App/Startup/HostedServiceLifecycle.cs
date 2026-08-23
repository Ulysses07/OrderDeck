using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace OrderDeck.App.Startup;

/// <summary>
/// Arka plan servislerinin ömür defteri: ne başlatıldıysa sırasıyla kaydeder,
/// kapanışta TERS SIRAYLA ve bir zaman bütçesi içinde durdurur.
///
/// NEDEN AYRI SINIF: <see cref="WpfStartupEnvironment"/> MessageBox ve Window
/// tanıyor, yani test edilemiyor. Buradaki iş ise saf sıra/zaman aşımı/hata
/// gözleme mantığı — test edilmezse yerine geçtiği fire-and-forget koddan
/// daha güvenilir olduğunu iddia edemeyiz.
///
/// ÜÇ AYRI DELİĞİ KAPATIYOR (hepsi aynı yerden geliyordu:
/// <c>_ = svc.StartAsync(...)</c>):
/// 1. Başlatma hiç beklenmiyordu; <c>try/catch</c> yalnız senkron hatayı
///    görüyordu.
/// 2. <see cref="BackgroundService"/> ilk <c>await</c>'ten SONRA düşerse
///    servis sessizce ölüyordu — <c>StartAsync</c>'i beklemek bunu GÖRMEZ,
///    <see cref="BackgroundService.ExecuteTask"/> gözlenmek zorunda.
/// 3. Kapanışta 15 hosted service'ten yalnız 2'si durduruluyordu; kalan 13'ü
///    (9 sync servisi dahil) sürecin ölmesiyle yarım DB/ağ işiyle kalıyordu.
///
/// TEK İŞ PARÇACIĞI varsayar: başlatma açılış akışından, durdurma
/// <c>App.OnExit</c>'ten çağrılıyor ve ikisi aynı anda koşamaz.
/// </summary>
public sealed class HostedServiceLifecycle
{
    private readonly record struct Entry(string Name, Func<CancellationToken, Task> Stop);

    private readonly List<Entry> _entries = new();
    private readonly ILogger _log;
    private readonly TimeSpan _startBudget;
    private readonly TimeSpan _stopBudget;

    /// <param name="startBudget">
    /// TÜM başlatma döngüsü için toplam bekleme tavanı, servis başına değil.
    /// Servis başına olsaydı 13 patolojik servis açılışı bütçenin 13 katı
    /// kadar kilitlerdi; operatörün gözünde bu, düzeltmeye çalıştığımız
    /// hatadan daha kötü. Bütçe dolunca kalanlar beklenmeden başlatılır —
    /// yani en kötü durumda bugünkü davranışa dönülür, daha kötüsüne değil.
    /// </param>
    /// <param name="stopBudget">
    /// TÜM kapanış döngüsü için toplam tavan. <c>App.OnExit</c> senkron
    /// bloklandığı için bu doğrudan "kapat"a basınca donan saniye sayısı.
    /// </param>
    public HostedServiceLifecycle(
        ILogger log,
        TimeSpan? startBudget = null,
        TimeSpan? stopBudget = null)
    {
        _log = log;
        _startBudget = startBudget ?? TimeSpan.FromSeconds(10);
        _stopBudget = stopBudget ?? TimeSpan.FromSeconds(5);
    }

    /// <summary>
    /// Kendi başlatma yolu olan (dolayısıyla <see cref="IHostedService"/>
    /// olmayan) bir bileşeni deftere yazar — overlay ve köprü böyle: ikisinin
    /// de kendine ait hata mesajı ve port çakışması dalı var, o metni yalnızca
    /// <see cref="WpfStartupEnvironment"/> biliyor.
    ///
    /// Başlatma DENENMEDEN önce çağrılmalı: başlatma yarıda patlarsa geriye
    /// yarım durum kalmış olabilir ve onu da durdurmak gerekir.
    /// </summary>
    public void Track(string name, Func<CancellationToken, Task> stop) =>
        _entries.Add(new Entry(name, stop));

    /// <summary>
    /// Verilen sırayla başlatır. Sıra sözleşmenin parçası:
    /// <see cref="StopAllAsync"/> tam tersini uyguluyor.
    /// </summary>
    public async Task StartAllAsync(IEnumerable<IHostedService> services)
    {
        var elapsed = Stopwatch.StartNew();

        foreach (var svc in services)
        {
            var name = svc.GetType().Name;
            Track(name, ct => svc.StopAsync(ct));

            Task start;
            try
            {
                // TUZAK — buraya zaman aşımlı bir token GEÇİLEMEZ.
                // BackgroundService.StartAsync bu token'dan LINKED bir kaynak
                // türetip ExecuteAsync'e onu veriyor; yani süreli bir token
                // servisin kendisini o süre dolunca kapatırdı. Başlatma token'ı
                // servisin ÖMRÜ, bekleme süresi değil. Bekleme aşağıda
                // Task.WhenAny ile sınırlanıyor.
                start = svc.StartAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                _log.LogError(ex,
                    "Hosted service {Service} başlatılamadı (senkron hata); diğerleriyle devam", name);
                continue;
            }

            var remaining = _startBudget - elapsed.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                ObserveLater(name, start, "başlatması");
                continue;
            }

            var finished = await Task.WhenAny(start, Task.Delay(remaining)).ConfigureAwait(false);
            if (!ReferenceEquals(finished, start))
            {
                _log.LogWarning(
                    "Hosted service {Service} başlatma bütçesi içinde dönmedi; beklemeden devam", name);
                ObserveLater(name, start, "başlatması");
                continue;
            }

            if (start.IsFaulted)
            {
                _log.LogError(start.Exception,
                    "Hosted service {Service} başlatılamadı; diğerleriyle devam", name);
                continue;
            }

            ObserveExecuteTask(svc, name);
        }
    }

    /// <summary>
    /// Ters sırayla durdurur. Bütçe TÜM döngü için ortak: ilk servis bütçeyi
    /// yerse kalanlar iptal edilmiş token'la çağrılır — ama yine de ÇAĞRILIR,
    /// çünkü <see cref="BackgroundService.StopAsync"/> token'a bakmadan önce
    /// durdurma sinyalini veriyor. Yani her servis her hâlükârda "dur" duyar,
    /// yalnız temiz inmesini bekleme süresi kısalır.
    /// </summary>
    public async Task StopAllAsync()
    {
        using var cts = new CancellationTokenSource(_stopBudget);

        for (var i = _entries.Count - 1; i >= 0; i--)
        {
            var entry = _entries[i];
            try
            {
                await entry.Stop(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                _log.LogWarning("Kapanış bütçesi doldu; {Service} temiz duramadı", entry.Name);
            }
            catch (Exception ex)
            {
                // Her servisin hatası AYRI loglanıyor: tek bir toplu catch,
                // ilk patlayandan sonrakilerin durdurulmadığını da gizlerdi.
                _log.LogWarning(ex, "{Service} durdurulurken hata", entry.Name);
            }
        }

        _entries.Clear();
    }

    /// <summary>
    /// <see cref="BackgroundService"/>, <c>ExecuteAsync</c>'i başlatıp
    /// beklemiyor: ilk <c>await</c>'ten sonraki istisna <c>ExecuteTask</c>'ta
    /// kalıyor. Görev DI konteyneri tarafından kökte tutulduğu için asla
    /// toplanmıyor, dolayısıyla <c>TaskScheduler.UnobservedTaskException</c>
    /// de hiç tetiklenmiyor — servis ömür boyu sessizce ölü kalır. Tek çıkar
    /// yol burada gözlemek.
    /// </summary>
    private void ObserveExecuteTask(IHostedService svc, string name)
    {
        if (svc is not BackgroundService bg || bg.ExecuteTask is null) return;
        ObserveLater(name, bg.ExecuteTask, "çalışması");
    }

    private void ObserveLater(string name, Task task, string what) =>
        _ = task.ContinueWith(
            t => _log.LogError(t.Exception,
                "Hosted service {Service} {What} hata ile bitti; artık çalışmıyor", name, what),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
}
