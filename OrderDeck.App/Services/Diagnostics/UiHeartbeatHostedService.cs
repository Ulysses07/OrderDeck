using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace OrderDeck.App.Services.Diagnostics;

/// <summary>
/// UI freeze diagnostic (2026-05-13): her 5 dakikada bir UI thread'in
/// responsive olduğunu doğrular ve memory snapshot bırakır. UI donarsa
/// heartbeat log atılamaz → sonraki açılışta log analizi pencereyi tespit
/// eder.
///
/// Çalışma: background thread'den Dispatcher.InvokeAsync ile UI thread'e
/// ping atar. Yanıt süresi log'a düşer. Yanıt timeout'a (10 sn) takılırsa
/// "UI hung" WARN log atar.
/// </summary>
public sealed class UiHeartbeatHostedService : BackgroundService
{
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan UiPingTimeout = TimeSpan.FromSeconds(10);

    private readonly Dispatcher _dispatcher;
    private readonly ILogger<UiHeartbeatHostedService> _log;

    public UiHeartbeatHostedService(
        Dispatcher dispatcher,
        ILogger<UiHeartbeatHostedService> log)
    {
        _dispatcher = dispatcher;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // İlk ping app açılışını fazla bombardıman etmemek için 1 dk bekle.
        try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PingAndLog(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Heartbeat tick failed");
            }

            try { await Task.Delay(HeartbeatInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task PingAndLog(CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var pingTask = _dispatcher.InvokeAsync(() => { /* no-op */ }).Task;

        var completed = await Task.WhenAny(pingTask, Task.Delay(UiPingTimeout, ct));
        sw.Stop();

        var memMb = GC.GetTotalMemory(forceFullCollection: false) / (1024.0 * 1024.0);

        if (completed != pingTask)
        {
            _log.LogWarning(
                "UI HEARTBEAT: thread unresponsive after {Timeout}s; mem={Mem:0.0}MB " +
                "(önceki donma şüphesi — bir sonraki tick canlı dönerse log incele)",
                UiPingTimeout.TotalSeconds, memMb);
            // pingTask'i bırak — bir sonraki tick'te tamamlanmışsa zaten OK.
            return;
        }

        _log.LogInformation(
            "UI HEARTBEAT: alive (ping={Ping:0}ms, mem={Mem:0.0}MB)",
            sw.Elapsed.TotalMilliseconds, memMb);
    }
}
