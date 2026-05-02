using OrderDeck.Core.Chat;
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

    private CancellationTokenSource? _cts;
    private Task? _runner;

    public YouTubeChatHostedService(
        Func<AppSettings> settingsProvider,
        IChatBus bus,
        ILoggerFactory loggerFactory,
        ITrialModeProbe? trialProbe = null,
        SpamFilter? spamFilter = null)
    {
        _settingsProvider = settingsProvider;
        _bus = bus;
        _loggerFactory = loggerFactory;
        _log = loggerFactory.CreateLogger<YouTubeChatHostedService>();
        _trialProbe = trialProbe;
        _spamFilter = spamFilter;
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

                try
                {
                    await scraper.StartAsync(ct);
                    // Wait for the runner to exit on its own (stream ended +
                    // 10 min silence heuristic) OR for cancellation. Previously
                    // this pinned Task.Delay(InfiniteTimeSpan), which kept
                    // useless 5-second polling alive indefinitely after the
                    // broadcaster ended their live.
                    await scraper.Completion.WaitAsync(ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "[YouTubeChatHostedService] scraper crashed; rescheduling");
                }
                finally
                {
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
        try { _cts?.Cancel(); } catch { }
        _cts?.Dispose();
    }
}
