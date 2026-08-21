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

namespace OrderDeck.Chat.Ingestors.Instagram;

/// <summary>
/// Instagram canlı yorum ingestor'ının yaşam döngüsü.
/// <see cref="OrderDeck.Chat.Ingestors.Facebook.FacebookChatHostedService"/>
/// ile aynı şekil; tek farkı <b>kalıcı hatada</b> (token/izin) yeniden
/// denemek yerine uzun idle'a düşmesi — sonsuz retry kullanıcının token'ını
/// düzeltmiyor, sadece log dolduruyor.
///
/// <para>Ayrı OAuth yok: bağlı Facebook Sayfa token'ına biniyoruz.</para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class InstagramChatHostedService : IHostedService, IDisposable
{
    private static readonly TimeSpan IdleWhenOffline = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan IdleAfterPollerExit = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan IdleAfterFatal = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);

    public const string HttpClientName = "instagram-graph";

    private readonly FacebookOAuthService _oauth;
    private readonly IChatBus _bus;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<InstagramChatHostedService> _log;
    private readonly SpamFilter? _spamFilter;
    private readonly StreamSessionService? _sessions;
    private readonly IHttpClientFactory? _httpFactory;

    private CancellationTokenSource? _cts;
    private Task? _runner;
    private CancellationTokenSource? _pollerCts;

    public InstagramChatHostedService(
        FacebookOAuthService oauth,
        IChatBus bus,
        ILoggerFactory loggerFactory,
        SpamFilter? spamFilter = null,
        StreamSessionService? sessions = null,
        IHttpClientFactory? httpFactory = null)
    {
        _oauth = oauth;
        _bus = bus;
        _loggerFactory = loggerFactory;
        _log = loggerFactory.CreateLogger<InstagramChatHostedService>();
        _spamFilter = spamFilter;
        _sessions = sessions;
        _httpFactory = httpFactory;

        if (_sessions is not null)
            _sessions.SessionEnded += OnSessionEnded;
    }

    private void OnSessionEnded(object? sender, SessionEndedEventArgs e)
    {
        // Operatör "Yayını Bitir" dedi → polling'i hemen kes.
        try { _pollerCts?.Cancel(); } catch { /* ignore */ }
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
                _log.LogDebug(ex, "[InstagramChatHostedService] stop wait swallowed");
            }
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var resolverHttp = _httpFactory?.CreateClient(HttpClientName)
                           ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var resolver = new InstagramAccountResolver(
            resolverHttp, _loggerFactory.CreateLogger<InstagramAccountResolver>());

        int consecutiveCrashes = 0;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Kip kapısı ve trial kapısı YOK: uzantıdan Instagram kaldırıldığı
                // için resmi API tek yol. Kapatılabilir olması sohbetin sessizce
                // susması demekti, açık kalması ise çift-post riski taşımıyor.

                // 1) Facebook Sayfa bağlantısı yoksa IG'ye de erişemeyiz.
                var creds = await _oauth.GetPageCredentialsAsync(ct).ConfigureAwait(false);
                if (creds is null)
                {
                    await Task.Delay(IdleWhenOffline, ct);
                    continue;
                }

                // 2) Operatör "Yayın Başlat"a basmadıysa Graph'a dokunma.
                if (_sessions is not null && _sessions.GetActive() is null)
                {
                    await Task.Delay(IdleWhenOffline, ct);
                    continue;
                }

                var account = await resolver.ResolveAsync(
                    creds.Value.PageId, creds.Value.PageAccessToken, ct).ConfigureAwait(false);
                if (account is null)
                {
                    _log.LogInformation(
                        "[InstagramChatHostedService] page {PageId} has no linked Instagram " +
                        "professional account; Instagram chat stays off", creds.Value.PageId);
                    await Task.Delay(IdleAfterFatal, ct);
                    continue;
                }

                _log.LogInformation(
                    "[InstagramChatHostedService] starting poller for IG @{Username}",
                    account.Value.Username ?? account.Value.IgUserId);

                var pollHttp = _httpFactory?.CreateClient(HttpClientName)
                               ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

                using var poller = new InstagramLiveCommentsPoller(
                    account.Value.IgUserId,
                    creds.Value.PageAccessToken,
                    _bus,
                    pollHttp,
                    _loggerFactory.CreateLogger<InstagramLiveCommentsPoller>(),
                    _spamFilter);

                using var pollerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                _pollerCts = pollerCts;

                bool crashed = false;
                try
                {
                    await poller.StartAsync(pollerCts.Token);
                    consecutiveCrashes = 0;
                    // Hatayı yüzeye çıkarır (iptalde OperationCanceledException).
                    await poller.Completion.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (OperationCanceledException) { /* SessionEnded — devam */ }
                catch (Exception ex)
                {
                    crashed = true;
                    consecutiveCrashes++;
                    _log.LogWarning(ex,
                        "[InstagramChatHostedService] poller crashed (#{Count} consecutive)",
                        consecutiveCrashes);
                }
                finally
                {
                    _pollerCts = null;
                    try { await poller.StopAsync(CancellationToken.None); } catch { /* ignore */ }
                }

                if (ct.IsCancellationRequested) break;

                // Kalıcı hata (token/izin) → hemen tekrar denemek anlamsız.
                if (poller.FatalReason is not null)
                {
                    _log.LogWarning(
                        "[InstagramChatHostedService] {Reason} — retrying in {Idle} min",
                        poller.FatalReason, IdleAfterFatal.TotalMinutes);
                    await Task.Delay(IdleAfterFatal, ct);
                    continue;
                }

                await Task.Delay(
                    crashed ? ComputeBackoff(consecutiveCrashes) : IdleAfterPollerExit, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log.LogError(ex,
                    "[InstagramChatHostedService] outer loop error; sleeping before retry");
                try { await Task.Delay(IdleAfterPollerExit, ct); } catch { break; }
            }
        }
    }

    /// <summary>30s × 2^(n-1), 5dk tavan. Facebook/YouTube ile aynı eğri.</summary>
    internal static TimeSpan ComputeBackoff(int consecutiveCrashes)
    {
        if (consecutiveCrashes <= 1) return IdleAfterPollerExit;
        var exp = Math.Min(consecutiveCrashes - 1, 10);
        var seconds = IdleAfterPollerExit.TotalSeconds * Math.Pow(2, exp);
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
