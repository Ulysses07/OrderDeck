using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OrderDeck.App.Startup;

namespace OrderDeck.Tests.App;

/// <summary>
/// Arka plan servisi ömür defterinin kuralları (denetim maddesi O-01).
///
/// NEDEN BU TESTLER: yerine geçtiği kod <c>_ = svc.StartAsync(...)</c> idi —
/// yani hiçbir hatası, hiçbir sırası, hiçbir zaman aşımı gözlemlenebilir
/// değildi. Yerine yazılan mantık test edilmezse ondan daha güvenilir olduğunu
/// iddia edemeyiz; üstelik burada yanlışın bedeli tam olarak sessizlik
/// (ölü servis, yarım kapanış) yani sahada da fark edilmez.
///
/// STA GEREKMİYOR: defter ne pencere ne MessageBox tanıyor.
/// </summary>
public class HostedServiceLifecycleTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(200);

    private static HostedServiceLifecycle Build(RecordingLogger log) =>
        new(log, startBudget: Budget, stopBudget: Budget);

    [Fact]
    public async Task Stops_everything_in_reverse_start_order()
    {
        // Sıra sözleşmenin kendisi: köprü overlay'den sonra kalkıyor, demek ki
        // ondan ÖNCE inmeli. Eski kod bunu dört sabit alanla elle kuruyordu;
        // beşinci servis eklendiğinde kimse listeyi güncellemedi.
        var order = new List<string>();
        var log = new RecordingLogger();
        var lifecycle = Build(log);

        lifecycle.Track("overlay", _ => { order.Add("stop:overlay"); return Task.CompletedTask; });
        lifecycle.Track("bridge", _ => { order.Add("stop:bridge"); return Task.CompletedTask; });

        var a = new RecordingService("a", order);
        var b = new RecordingService("b", order);
        await lifecycle.StartAllAsync(new IHostedService[] { a, b });

        await lifecycle.StopAllAsync();

        Assert.Equal(
            new[] { "start:a", "start:b", "stop:b", "stop:a", "stop:bridge", "stop:overlay" },
            order);
    }

    [Fact]
    public async Task A_service_that_throws_at_start_does_not_take_the_others_down()
    {
        var order = new List<string>();
        var log = new RecordingLogger();
        var lifecycle = Build(log);

        var after = new RecordingService("after", order);
        await lifecycle.StartAllAsync(new IHostedService[] { new ThrowingStartService(), after });

        Assert.Contains("start:after", order);
        Assert.Contains(log.Entries, e =>
            e.Level == LogLevel.Error && e.Message.Contains("ThrowingStartService"));

        // Patlayan servis de deftere yazılı: yarım kalmış bir başlatmanın
        // arkasında durdurulacak durum kalmış olabilir.
        await lifecycle.StopAllAsync();
        Assert.Contains("stop:after", order);
    }

    [Fact]
    public async Task A_background_service_that_dies_after_the_first_await_is_logged()
    {
        // O-01'in asıl etkisi. StartAsync'i beklemek BUNU GÖRMEZ:
        // BackgroundService.StartAsync ExecuteAsync ilk await'e geldiğinde
        // Task.CompletedTask döner, istisna ExecuteTask'ta kalır. Servis
        // ömür boyu ölü kalır ve hiçbir yere tek satır düşmez.
        var log = new RecordingLogger();
        var lifecycle = Build(log);

        await lifecycle.StartAllAsync(new IHostedService[] { new DyingService() });

        Assert.True(
            await log.WaitFor(e => e.Level == LogLevel.Error && e.Message.Contains("DyingService")),
            "Ölen hosted service için hata kaydı düşmedi.");
    }

    [Fact]
    public async Task A_slow_start_does_not_cancel_the_services_that_already_started()
    {
        // REGRESYON TUZAĞI: başlatma bütçesini "zaman aşımlı token" olarak
        // StartAsync'e geçirmek doğal görünüyor ve DERLENİYOR — ama
        // BackgroundService o token'dan LINKED bir kaynak türetip
        // ExecuteAsync'e veriyor. Yani bütçe dolduğunda çalışan servislerin
        // hepsi kapanırdı: açılışta sessizce ölen 13 servis. Bekleme
        // Task.WhenAny ile sınırlanmak zorunda.
        var log = new RecordingLogger();
        var lifecycle = Build(log);
        var alive = new TokenCapturingService();
        var slow = new SlowStartService();

        try
        {
            await lifecycle.StartAllAsync(new IHostedService[] { alive, slow });

            Assert.True(await alive.Running.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            // Bütçenin fazlasıyla ötesine geçiliyor: sızmış bir zamanlayıcı
            // token'ı ancak bu noktada tetiklenmiş olurdu.
            await Task.Delay(Budget * 4);

            Assert.False(alive.StoppingToken.IsCancellationRequested);
            Assert.Contains(log.Entries, e =>
                e.Level == LogLevel.Warning && e.Message.Contains("SlowStartService"));
        }
        finally
        {
            slow.Release();
        }
    }

    [Fact]
    public async Task The_stop_budget_is_shared_and_the_remaining_services_still_hear_stop()
    {
        // Bütçe servis başına olsaydı 15 servis × bütçe kadar donardık.
        // Ortak bütçede ilk servis hepsini yese bile kalanlara "dur" ULAŞMALI:
        // BackgroundService durdurma sinyalini token'a bakmadan önce veriyor.
        var order = new List<string>();
        var log = new RecordingLogger();
        var lifecycle = Build(log);

        var first = new RecordingService("first", order);
        await lifecycle.StartAllAsync(new IHostedService[] { first, new HangingStopService() });

        var clock = System.Diagnostics.Stopwatch.StartNew();
        await lifecycle.StopAllAsync();
        clock.Stop();

        Assert.Contains("stop:first", order);
        Assert.True(clock.Elapsed < Budget * 5, $"Kapanış bütçeyi aştı: {clock.Elapsed}");
        Assert.Contains(log.Entries, e =>
            e.Level == LogLevel.Warning && e.Message.Contains("HangingStopService"));
    }

    [Fact]
    public async Task Each_stop_failure_is_logged_separately_and_does_not_skip_the_rest()
    {
        var order = new List<string>();
        var log = new RecordingLogger();
        var lifecycle = Build(log);

        lifecycle.Track("overlay", _ => { order.Add("stop:overlay"); return Task.CompletedTask; });
        lifecycle.Track("bridge", _ => throw new InvalidOperationException("köprü kapanmadı"));

        await lifecycle.StopAllAsync();

        Assert.Equal(new[] { "stop:overlay" }, order);
        Assert.Contains(log.Entries, e =>
            e.Level == LogLevel.Warning && e.Message.Contains("bridge"));
    }

    // ── test ikizleri ────────────────────────────────────────────────────

    private sealed class RecordingService : IHostedService
    {
        private readonly string _name;
        private readonly List<string> _order;
        public RecordingService(string name, List<string> order) { _name = name; _order = order; }

        public Task StartAsync(CancellationToken ct)
        {
            _order.Add($"start:{_name}");
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken ct)
        {
            _order.Add($"stop:{_name}");
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingStartService : IHostedService
    {
        public Task StartAsync(CancellationToken ct) => throw new InvalidOperationException("açılmadı");
        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class DyingService : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await Task.Yield();
            throw new InvalidOperationException("ilk await'ten sonra düştü");
        }
    }

    private sealed class TokenCapturingService : BackgroundService
    {
        public CancellationToken StoppingToken { get; private set; }
        public TaskCompletionSource<bool> Running { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            StoppingToken = stoppingToken;
            Running.TrySetResult(true);
            try { await Task.Delay(Timeout.Infinite, stoppingToken); }
            catch (OperationCanceledException) { /* beklenen */ }
        }
    }

    private sealed class SlowStartService : IHostedService
    {
        private readonly TaskCompletionSource<bool> _gate =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task StartAsync(CancellationToken ct) => _gate.Task;
        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
        public void Release() => _gate.TrySetResult(true);
    }

    private sealed class HangingStopService : IHostedService
    {
        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct) => Task.Delay(Timeout.Infinite, ct);
    }

    private sealed class RecordingLogger : ILogger
    {
        private readonly List<(LogLevel Level, string Message)> _entries = new();

        public IReadOnlyList<(LogLevel Level, string Message)> Entries
        {
            get { lock (_entries) return _entries.ToList(); }
        }

        /// <summary>
        /// Gözleme devamları (<c>ContinueWith</c>) thread havuzunda koşuyor;
        /// kayıt anı deterministik değil. Sabit <c>Task.Delay</c> yerine
        /// yoklama: yavaş CI makinesinde bayrak yanıp sönmesin.
        /// </summary>
        public async Task<bool> WaitFor(
            Func<(LogLevel Level, string Message), bool> match, int timeoutMs = 5000)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < timeoutMs)
            {
                if (Entries.Any(match)) return true;
                await Task.Delay(25);
            }
            return Entries.Any(match);
        }

        IDisposable? ILogger.BeginScope<TState>(TState state) => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            // Servis adı yapılandırılmış parametrede; formatter onu metne
            // gömdüğü için iddialar düz metin üstünden kurulabiliyor.
            lock (_entries) _entries.Add((logLevel, formatter(state, exception)));
        }
    }
}
