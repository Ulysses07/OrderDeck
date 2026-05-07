using OrderDeck.Core.Chat;
using OrderDeck.Core.Sessions;
using OrderDeck.Core.Settings;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace OrderDeck.Chat.Ingestors.YouTube;

/// <summary>
/// Owns the lifecycle of the YouTube live chat scraper.
///
/// Loop (only when AppSettings.YouTubeChannelHandle is set):
///   1. Read settings; if handle missing, idle for 60s and re-check.
///   2. Resolve handle → live video ID (or direct video ID/URL if pasted).
///   3. If not currently live, idle 60s and re-check.
///   4. Start a YouTubeLiveChatScraper for that video ID. Wait for it to fail
///      naturally (stream ended, chat disabled, network).
///   5. After scraper exits, idle 30s, then loop back to step 2.
///
/// Trial-mode probe is honored at the start of each loop iteration so trial
/// users transparently lose YouTube access until they buy a license.
/// </summary>
public sealed class YouTubeChatHostedService : IHostedService, IDisposable
{
    private static readonly TimeSpan IdleWhenOffline = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan IdleAfterScraperExit = TimeSpan.FromSeconds(30);

    private readonly Func<AppSettings> _settingsProvider;
    private readonly IChatBus _bus;
    private readonly ILogger<YouTubeChatHostedService> _log;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ITrialModeProbe? _trialProbe;
    private readonly SpamFilter? _spamFilter;
    private readonly StreamSessionService? _sessions;

    private CancellationTokenSource? _cts;
    private Task? _runner;

    // Per-iteration cancellation source so SessionEnded can stop the
    // currently-running scraper *now* instead of waiting for the 3-min
    // silence heuristic. Scoped to one scraper run; recreated each loop.
    private CancellationTokenSource? _scraperCts;

    public YouTubeChatHostedService(
        Func<AppSettings> settingsProvider,
        IChatBus bus,
        ILoggerFactory loggerFactory,
        ITrialModeProbe? trialProbe = null,
        SpamFilter? spamFilter = null,
        StreamSessionService? sessions = null)
    {
        _settingsProvider = settingsProvider;
        _bus = bus;
        _loggerFactory = loggerFactory;
        _log = loggerFactory.CreateLogger<YouTubeChatHostedService>();
        _trialProbe = trialProbe;
        _spamFilter = spamFilter;
        _sessions = sessions;

        if (_sessions is not null)
            _sessions.SessionEnded += OnSessionEnded;
    }

    private void OnSessionEnded(object? sender, SessionEndedEventArgs e)
    {
        // "Yayını Bitir" pressed in the WPF shell → cancel the active
        // scraper immediately so a subsequent "Yayın Başlat" gets a
        // fresh resolve pass and isn't held up by the silence heuristic.
        try { _scraperCts?.Cancel(); } catch { /* ignore */ }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var ct = _cts.Token;
        _runner = Task.Run(() => RunAsync(ct), ct);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try { _cts?.Cancel(); } catch { /* ignore */ }
        if (_runner is not null)
        {
            try { await _runner.WaitAsync(cancellationToken); }
            catch (OperationCanceledException) { /* expected */ }
            catch (Exception ex) { _log.LogDebug(ex, "[YouTubeChatHostedService] stop wait swallowed"); }
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        using var resolver = new YouTubeLiveResolver(_loggerFactory.CreateLogger<YouTubeLiveResolver>());

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_trialProbe?.IsTrialMode == true)
                {
                    await Task.Delay(IdleWhenOffline, ct);
                    continue;
                }

                var handle = _settingsProvider().YouTubeChannelHandle;
                if (string.IsNullOrWhiteSpace(handle))
                {
                    await Task.Delay(IdleWhenOffline, ct);
                    continue;
                }

                // Stream session gate: only scrape while the operator has an
                // active session ("Yayın Başlat" pressed). When they press
                // "Yayını Bitir" SessionEnded fires + cancels _scraperCts so
                // we exit fast; on next loop iteration GetActive() returns
                // null until the next start. This makes start/stop feel
                // immediate instead of being held up by the silence
                // heuristic from the previous broadcast.
                if (_sessions is not null && _sessions.GetActive() is null)
                {
                    await Task.Delay(IdleWhenOffline, ct);
                    continue;
                }

                // Direct video URL or bare ID? Use it as-is; resolver only fires for handles.
                var videoId = YouTubeVideoIdExtractor.TryExtract(handle)
                              ?? await resolver.ResolveAsync(handle, ct);

                if (string.IsNullOrEmpty(videoId))
                {
                    _log.LogDebug("[YouTubeChatHostedService] {Handle} appears offline; retrying in {Idle}s",
                        handle, IdleWhenOffline.TotalSeconds);
                    await Task.Delay(IdleWhenOffline, ct);
                    continue;
                }

                _log.LogInformation("[YouTubeChatHostedService] starting scraper for video {VideoId}", videoId);
                using var scraper = new YouTubeLiveChatScraper(
                    videoId, _bus, _loggerFactory.CreateLogger<YouTubeLiveChatScraper>(),
                    _spamFilter);

                // Per-scraper cancellation: linked to the outer ct (app
                // shutdown / hosted-service stop) AND cancellable directly
                // from OnSessionEnded so "Yayını Bitir" stops chat without
                // waiting for the 3-min silence timer.
                using var scraperCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                _scraperCts = scraperCts;

                try
                {
                    await scraper.StartAsync(scraperCts.Token);
                    // Wait for the runner to exit on its own (stream ended +
                    // silence heuristic) OR for cancellation (operator
                    // pressed Yayını Bitir / app shutdown).
                    await scraper.Completion.WaitAsync(scraperCts.Token);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (OperationCanceledException) { /* SessionEnded — fall through to cleanup */ }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "[YouTubeChatHostedService] scraper crashed; rescheduling");
                }
                finally
                {
                    _scraperCts = null;
                    try { await scraper.StopAsync(CancellationToken.None); } catch { /* ignore */ }
                }

                if (!ct.IsCancellationRequested)
                    await Task.Delay(IdleAfterScraperExit, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log.LogError(ex, "[YouTubeChatHostedService] outer loop error; sleeping before retry");
                try { await Task.Delay(IdleAfterScraperExit, ct); } catch { break; }
            }
        }
    }

    public void Dispose()
    {
        if (_sessions is not null)
            _sessions.SessionEnded -= OnSessionEnded;
        try { _cts?.Cancel(); } catch { }
        _cts?.Dispose();
    }
}
