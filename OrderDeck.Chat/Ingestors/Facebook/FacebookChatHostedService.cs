using System;
using System.Net.Http;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OrderDeck.Chat.Facebook;
using OrderDeck.Core.Chat;
using OrderDeck.Core.Sessions;
using OrderDeck.Core.Settings;

namespace OrderDeck.Chat.Ingestors.Facebook;

/// <summary>
/// Owns the lifecycle of the Facebook Live comments ingestor. Mirrors
/// <see cref="OrderDeck.Chat.Ingestors.YouTube.YouTubeChatHostedService"/>
/// in shape but the inner work is "resolve current live video id → open
/// SSE stream → wait for it to end" rather than "poll continuation tokens".
///
/// <para>Loop (only when an operator has connected a Page):</para>
/// <list type="number">
///   <item>Gate on trial mode + active session (same as YouTube).</item>
///   <item>Ask <see cref="FacebookLiveVideoResolver"/> for the current
///     LIVE_NOW video id. Null → idle 60s.</item>
///   <item>Open <see cref="FacebookLiveCommentsStream"/>; wait for it to
///     finish (broadcast ended, network drop, or operator stopped).</item>
///   <item>On crash → exponential backoff (30s → 1m → 2m → 4m → 5m cap).
///     Graceful end → 30s and re-resolve.</item>
/// </list>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class FacebookChatHostedService : IHostedService, IDisposable
{
    private static readonly TimeSpan IdleWhenOffline = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan IdleAfterStreamExit = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);

    public const string ResolverClientName = "facebook-resolver";
    public const string StreamClientName = "facebook-stream";

    private readonly Func<AppSettings> _settingsProvider;
    private readonly FacebookOAuthService _oauth;
    private readonly IChatBus _bus;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<FacebookChatHostedService> _log;
    private readonly ITrialModeProbe? _trialProbe;
    private readonly SpamFilter? _spamFilter;
    private readonly StreamSessionService? _sessions;
    private readonly IHttpClientFactory? _httpFactory;

    private CancellationTokenSource? _cts;
    private Task? _runner;

    // Per-stream cancellation source so SessionEnded can cut the SSE
    // connection immediately when the operator ends the session.
    private CancellationTokenSource? _streamCts;

    public FacebookChatHostedService(
        Func<AppSettings> settingsProvider,
        FacebookOAuthService oauth,
        IChatBus bus,
        ILoggerFactory loggerFactory,
        ITrialModeProbe? trialProbe = null,
        SpamFilter? spamFilter = null,
        StreamSessionService? sessions = null,
        IHttpClientFactory? httpFactory = null)
    {
        _settingsProvider = settingsProvider;
        _oauth = oauth;
        _bus = bus;
        _loggerFactory = loggerFactory;
        _log = loggerFactory.CreateLogger<FacebookChatHostedService>();
        _trialProbe = trialProbe;
        _spamFilter = spamFilter;
        _sessions = sessions;
        _httpFactory = httpFactory;

        if (_sessions is not null)
            _sessions.SessionEnded += OnSessionEnded;
    }

    private void OnSessionEnded(object? sender, SessionEndedEventArgs e)
    {
        // Operator pressed "Yayını Bitir" → drop the SSE so the next start
        // re-resolves immediately rather than waiting for Meta to time out.
        try { _streamCts?.Cancel(); } catch { /* ignore */ }
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
            catch (Exception ex)
            {
                _log.LogDebug(ex, "[FacebookChatHostedService] stop wait swallowed");
            }
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var resolverHttp = _httpFactory?.CreateClient(ResolverClientName)
                          ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var resolver = new FacebookLiveVideoResolver(
            resolverHttp, _loggerFactory.CreateLogger<FacebookLiveVideoResolver>());

        int consecutiveCrashes = 0;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_trialProbe?.IsTrialMode == true)
                {
                    await Task.Delay(IdleWhenOffline, ct);
                    continue;
                }

                // No Page bound yet (operator hasn't run OAuth) → idle.
                var creds = await _oauth.GetPageCredentialsAsync(ct).ConfigureAwait(false);
                if (creds is null)
                {
                    await Task.Delay(IdleWhenOffline, ct);
                    continue;
                }

                // Stream session gate — same semantics as YouTube. Operator
                // must press "Yayın Başlat" before we touch the Graph API.
                if (_sessions is not null && _sessions.GetActive() is null)
                {
                    await Task.Delay(IdleWhenOffline, ct);
                    continue;
                }

                var videoId = await resolver.ResolveAsync(
                    creds.Value.PageId, creds.Value.PageAccessToken, ct).ConfigureAwait(false);
                if (string.IsNullOrEmpty(videoId))
                {
                    _log.LogDebug(
                        "[FacebookChatHostedService] page {PageId} not currently live; retry in {Idle}s",
                        creds.Value.PageId, IdleWhenOffline.TotalSeconds);
                    await Task.Delay(IdleWhenOffline, ct);
                    continue;
                }

                _log.LogInformation(
                    "[FacebookChatHostedService] opening comment poller for video {VideoId}", videoId);

                // streaming-graph endpoint expects a long-lived connection —
                // never let the named-client default timeout (typically
                // 100s) kill it. We override to InfiniteTimeSpan locally.
                var streamHttp = _httpFactory?.CreateClient(StreamClientName)
                                 ?? new HttpClient();
                streamHttp.Timeout = Timeout.InfiniteTimeSpan;

                using var stream = new FacebookLiveCommentsStream(
                    videoId,
                    creds.Value.PageAccessToken,
                    _bus,
                    streamHttp,
                    _loggerFactory.CreateLogger<FacebookLiveCommentsStream>(),
                    _spamFilter);

                using var streamCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                _streamCts = streamCts;

                bool crashed = false;
                try
                {
                    await stream.StartAsync(streamCts.Token);
                    consecutiveCrashes = 0;
                    await stream.Completion.WaitAsync(streamCts.Token);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (OperationCanceledException) { /* SessionEnded — fall through */ }
                catch (Exception ex)
                {
                    crashed = true;
                    consecutiveCrashes++;
                    _log.LogWarning(ex,
                        "[FacebookChatHostedService] stream crashed (#{Count} consecutive); rescheduling",
                        consecutiveCrashes);
                }
                finally
                {
                    _streamCts = null;
                    try { await stream.StopAsync(CancellationToken.None); } catch { /* ignore */ }
                }

                if (!ct.IsCancellationRequested)
                {
                    var idle = crashed
                        ? ComputeBackoff(consecutiveCrashes)
                        : IdleAfterStreamExit;
                    await Task.Delay(idle, ct);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log.LogError(ex,
                    "[FacebookChatHostedService] outer loop error; sleeping before retry");
                try { await Task.Delay(IdleAfterStreamExit, ct); } catch { break; }
            }
        }
    }

    /// <summary>Exponential backoff: 30s × 2^(n-1) capped at 5min. Same
    /// shape as YouTube's so operator never sees one platform recover
    /// faster than the other after a network hiccup.</summary>
    internal static TimeSpan ComputeBackoff(int consecutiveCrashes)
    {
        if (consecutiveCrashes <= 1) return IdleAfterStreamExit;
        var exp = Math.Min(consecutiveCrashes - 1, 10);
        var seconds = IdleAfterStreamExit.TotalSeconds * Math.Pow(2, exp);
        if (seconds >= MaxBackoff.TotalSeconds) return MaxBackoff;
        return TimeSpan.FromSeconds(seconds);
    }

    public void Dispose()
    {
        if (_sessions is not null)
            _sessions.SessionEnded -= OnSessionEnded;
        try { _cts?.Cancel(); } catch { /* ignore */ }
        _cts?.Dispose();
    }
}
