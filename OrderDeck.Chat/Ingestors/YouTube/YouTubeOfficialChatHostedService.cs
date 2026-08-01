using System.Net.Http;
using OrderDeck.Chat.YouTube;
using OrderDeck.Core.Chat;
using OrderDeck.Core.Sessions;
using OrderDeck.Core.Settings;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace OrderDeck.Chat.Ingestors.YouTube;

/// <summary>
/// YouTube canlı sohbetinin TEK yolu: resmi gRPC <c>liveChatMessages.streamList</c>
/// ingestor'ının lifecycle'ı. (2026-08-01'de kota 760k/gün onaylanınca ToS'a aykırı
/// InnerTube scraper'ı ve /live sayfasını kazıyan resolver kaldırıldı.)
///
/// Dış döngü: trial gate → handle → session gate → yayın çözümle → ingestor başlat
/// → Completion'ı bekle → idle. Reconnect'i ingestor kendi içinde yönetir
/// (streamList page_token).
///
/// Çözümleme sırası: ayardaki metin doğrudan video URL'si/id'si ise
/// <see cref="YouTubeVideoIdExtractor"/> (OAuth gerekmez), değilse
/// <see cref="_activeBroadcastResolver"/> → <c>liveBroadcasts.list?broadcastStatus=active</c>
/// (OAuth ŞART; tek çağrıda hem videoId hem liveChatId döner).
/// </summary>
public sealed class YouTubeOfficialChatHostedService : IHostedService, IDisposable
{
    /// <summary>Named HttpClient: gRPC dışı REST çağrıları (videos.list —
    /// liveChatId çözümleme + izleyici sayısı).</summary>
    public const string HttpClientName = "youtube-official";

    private static readonly TimeSpan IdleWhenOffline = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan IdleAfterExit = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);

    private readonly Func<AppSettings> _settingsProvider;
    private readonly IChatBus _bus;
    private readonly ILogger<YouTubeOfficialChatHostedService> _log;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ITrialModeProbe? _trialProbe;
    private readonly SpamFilter? _spamFilter;
    private readonly StreamSessionService? _sessions;
    private readonly IHttpClientFactory? _httpFactory;
    private readonly ViewerCountTracker? _viewers;
    // Delegate, tip değil: gerçek uygulama YouTubeModerationService (Windows-only,
    // [SupportedOSPlatform]) — doğrudan bağımlılık o kısıtı bu platform-agnostik
    // servise yayardı.
    private readonly Func<CancellationToken, Task<(string VideoId, string LiveChatId)>>? _activeBroadcastResolver;

    private CancellationTokenSource? _cts;
    private Task? _runner;
    private CancellationTokenSource? _ingestorCts;
    private bool _warnedNoKey;

    public YouTubeOfficialChatHostedService(
        Func<AppSettings> settingsProvider,
        IChatBus bus,
        ILoggerFactory loggerFactory,
        ITrialModeProbe? trialProbe = null,
        SpamFilter? spamFilter = null,
        StreamSessionService? sessions = null,
        IHttpClientFactory? httpFactory = null,
        ViewerCountTracker? viewers = null,
        Func<CancellationToken, Task<(string VideoId, string LiveChatId)>>? activeBroadcastResolver = null)
    {
        _settingsProvider = settingsProvider;
        _bus = bus;
        _loggerFactory = loggerFactory;
        _log = loggerFactory.CreateLogger<YouTubeOfficialChatHostedService>();
        _trialProbe = trialProbe;
        _spamFilter = spamFilter;
        _sessions = sessions;
        _httpFactory = httpFactory;
        _viewers = viewers;
        _activeBroadcastResolver = activeBroadcastResolver;

        if (_sessions is not null)
            _sessions.SessionEnded += OnSessionEnded;
    }

    private void OnSessionEnded(object? sender, SessionEndedEventArgs e)
    {
        try { _ingestorCts?.Cancel(); } catch { /* ignore */ }
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
            catch (Exception ex) { _log.LogDebug(ex, "[YouTube Official] stop wait swallowed"); }
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        int consecutiveCrashes = 0;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var settings = _settingsProvider();

                if (_trialProbe?.IsTrialMode == true)
                {
                    await Task.Delay(IdleWhenOffline, ct);
                    continue;
                }

                var handle = settings.YouTubeChannelHandle;
                if (string.IsNullOrWhiteSpace(handle))
                {
                    await Task.Delay(IdleWhenOffline, ct);
                    continue;
                }

                // Çözümleme sırası: AppSettings override (QA/ayrı proje) →
                // binary'ye gömülü YouTubeApiDefaults.ApiKey (normal kurulum).
                var apiKey = !string.IsNullOrWhiteSpace(settings.YouTubeApiKey)
                    ? settings.YouTubeApiKey
                    : YouTubeApiDefaults.ApiKey;
                if (string.IsNullOrWhiteSpace(apiKey))
                {
                    if (!_warnedNoKey)
                    {
                        _log.LogWarning("[YouTube Official] YouTube API key yok (ne ayarda ne gömülü default'ta) — sohbet çekilemez. Release'e key gömülmeli ya da ayarlardan girilmeli.");
                        _warnedNoKey = true;
                    }
                    await Task.Delay(IdleWhenOffline, ct);
                    continue;
                }
                _warnedNoKey = false;

                // Stream session gate: yalnız aktif yayında çek.
                if (_sessions is not null && _sessions.GetActive() is null)
                {
                    await Task.Delay(IdleWhenOffline, ct);
                    continue;
                }

                // Ayarda tam video URL'si/id'si varsa OAuth'a hiç gerek yok.
                // Handle yazılıysa resmi uçtan çözülür (OAuth şart).
                var videoId = YouTubeVideoIdExtractor.TryExtract(handle);
                string? liveChatId = null;
                if (videoId is null)
                {
                    if (_activeBroadcastResolver is null)
                    {
                        await Task.Delay(IdleWhenOffline, ct);
                        continue;
                    }
                    try
                    {
                        (videoId, liveChatId) = await _activeBroadcastResolver(ct);
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex)
                    {
                        // Yayın yok / OAuth bağlı değil → Türkçe mesajla bekle.
                        _log.LogInformation("[YouTube Official] aktif yayın çözümlenemedi: {Reason}", ex.Message);
                        await Task.Delay(IdleWhenOffline, ct);
                        continue;
                    }
                }

                if (string.IsNullOrEmpty(videoId))
                {
                    await Task.Delay(IdleWhenOffline, ct);
                    continue;
                }

                _log.LogInformation("[YouTube Official] ingestor başlatılıyor, video {VideoId}", videoId);
                var ingestHttp = _httpFactory?.CreateClient(HttpClientName)
                                 ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
                using var ingestor = new YouTubeOfficialChatIngestor(
                    videoId, apiKey, _bus,
                    _loggerFactory.CreateLogger<YouTubeOfficialChatIngestor>(),
                    ingestHttp, _spamFilter, _viewers, liveChatId);

                using var ingestorCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                _ingestorCts = ingestorCts;

                bool crashed = false;
                try
                {
                    await ingestor.StartAsync(ingestorCts.Token);
                    consecutiveCrashes = 0;
                    await ingestor.Completion.WaitAsync(ingestorCts.Token);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (OperationCanceledException) { /* SessionEnded — cleanup */ }
                catch (Exception ex)
                {
                    crashed = true;
                    consecutiveCrashes++;
                    _log.LogWarning(ex,
                        "[YouTube Official] ingestor crash (#{Count}); yeniden planlanıyor", consecutiveCrashes);
                }
                finally
                {
                    _ingestorCts = null;
                    try { await ingestor.StopAsync(CancellationToken.None); } catch { /* ignore */ }
                }

                if (!ct.IsCancellationRequested)
                {
                    var idle = crashed ? ComputeBackoff(consecutiveCrashes) : IdleAfterExit;
                    await Task.Delay(idle, ct);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log.LogError(ex, "[YouTube Official] outer loop error; sleeping before retry");
                try { await Task.Delay(IdleAfterExit, ct); } catch { break; }
            }
        }
    }

    /// <summary>Exponential backoff: 30s × 2^(n-1) capped at 5 min.</summary>
    internal static TimeSpan ComputeBackoff(int consecutiveCrashes)
    {
        if (consecutiveCrashes <= 1) return IdleAfterExit;
        var exp = Math.Min(consecutiveCrashes - 1, 10);
        var seconds = IdleAfterExit.TotalSeconds * Math.Pow(2, exp);
        return seconds >= MaxBackoff.TotalSeconds ? MaxBackoff : TimeSpan.FromSeconds(seconds);
    }

    public void Dispose()
    {
        if (_sessions is not null)
            _sessions.SessionEnded -= OnSessionEnded;
        try { _cts?.Cancel(); } catch { /* ignore */ }
        _cts?.Dispose();
    }
}
