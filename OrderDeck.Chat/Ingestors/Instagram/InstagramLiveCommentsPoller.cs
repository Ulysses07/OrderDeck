using System;
using System.Threading;
using System.Threading.Tasks;
using System.Net.Http;
using Microsoft.Extensions.Logging;
using OrderDeck.Chat.Facebook;
using OrderDeck.Core.Chat;

namespace OrderDeck.Chat.Ingestors.Instagram;

/// <summary>
/// Bir IG business hesabının aktif canlı yayınındaki yorumları polling ile
/// çekip <see cref="IChatBus"/>'a basar. Read-only — Meta canlı yayın
/// yorumlarında hide/delete desteklemiyor.
///
/// <para><b>Tek çağrı:</b> <c>live_media</c> alan genişletmesiyle hem aktif
/// yayını hem yorumlarını getirir. Ayrı bir "yayın var mı" sorgusu yok.</para>
///
/// <para><b>Sayfalama yok.</b> Comments ucunun sayfalama şeması dokümante
/// edilmemiş ve Meta cursor'lar için <i>"Don't store cursors"</i> diyor.
/// Yerine uyarlanabilir aralık: sayfa taşarsa polling sıklaşır
/// (1s → 0.5s → 0.3s), akış sakinleşince gevşer. Kota darboğaz olmadığı için
/// bu bedava bir emniyet supabı.</para>
///
/// <para><b>Yayın sonu:</b> Meta canlı yorumları yayın bittikten sonra
/// okutmuyor (<i>"can only be read while ... being broadcast"</i>), yani son
/// polling aralığındaki yorumlar kaybolur. Bilinçli ödün.</para>
/// </summary>
public sealed class InstagramLiveCommentsPoller : IChatIngestor, IDisposable
{
    /// <summary>Normal polling aralığı.</summary>
    internal static readonly TimeSpan BaseInterval = TimeSpan.FromSeconds(1);

    /// <summary>Taşma hâlinde inilebilecek en kısa aralık.</summary>
    internal static readonly TimeSpan MinInterval = TimeSpan.FromMilliseconds(300);

    /// <summary>Sakinleşince her turda bu kadar gevşer.</summary>
    internal static readonly TimeSpan RelaxStep = TimeSpan.FromMilliseconds(100);

    /// <summary>Aktif yayın yokken bekleme. Yayın açılınca <see cref="BaseInterval"/>'e döner.</summary>
    private static readonly TimeSpan NoBroadcastIdle = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Bu kadar art arda geçici hatadan sonra döngüyü bırak; hosted
    /// service yeniden kurar (token yenilenmiş olabilir).</summary>
    private const int MaxConsecutiveErrors = 5;

    /// <summary>
    /// Yalnızca kullandığımız alanlar isteniyor. Kullanılmayan alan istemek
    /// App Review'da <i>"selecting unneeded permissions"</i> ile aynı kategoriye
    /// düşer. <c>limit(50)</c> <b>açıkça</b> yazılıyor: 50 sınırı sadece
    /// doğrudan uçta belgeli, iç içe genişletmede Graph'ın genel varsayılanı
    /// 25 gelebilir. Gerçekte kaç döndüğü pilot yayında ölçülecek.
    /// </summary>
    private const string Fields = "id,comments.limit(50){id,text,timestamp,username}";

    private readonly string _igUserId;
    private readonly string _pageAccessToken;
    private readonly IChatBus _bus;
    private readonly HttpClient _http;
    private readonly ILogger<InstagramLiveCommentsPoller> _log;
    private readonly SpamFilter? _spamFilter;
    private readonly InstagramCommentWatermark _watermark = new();

    private CancellationTokenSource? _cts;
    private Task? _runner;
    private readonly TaskCompletionSource _completionTcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public string Platform => "instagram";

    /// <summary>Döngü bittiğinde tamamlanır.</summary>
    public Task Completion => _completionTcs.Task;

    /// <summary>
    /// Döngü kalıcı bir sebeple durduysa operatöre gösterilecek Türkçe mesaj;
    /// aksi hâlde null. Hosted service bunu görünce yeniden denemez.
    /// </summary>
    public string? FatalReason { get; private set; }

    public InstagramLiveCommentsPoller(
        string igUserId,
        string pageAccessToken,
        IChatBus bus,
        HttpClient http,
        ILogger<InstagramLiveCommentsPoller> log,
        SpamFilter? spamFilter = null)
    {
        _igUserId = igUserId;
        _pageAccessToken = pageAccessToken;
        _bus = bus;
        _http = http;
        _log = log;
        _spamFilter = spamFilter;
    }

    public Task StartAsync(CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _runner = Task.Run(() => RunAsync(_cts.Token));
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        try { _cts?.Cancel(); } catch { /* already disposed */ }
        if (_runner is not null)
        {
            try { await _runner.WaitAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { /* expected */ }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "[InstagramLiveCommentsPoller] stop wait swallowed");
            }
        }
    }

    /// <summary>
    /// Bir sonraki polling aralığı. Taşmada yarıya iner (tabana kadar),
    /// aksi hâlde kademeli olarak tavana gevşer. Saf — test edilebilir.
    /// </summary>
    internal static TimeSpan NextInterval(TimeSpan current, bool overflowed)
    {
        if (overflowed)
        {
            var halved = TimeSpan.FromMilliseconds(current.TotalMilliseconds / 2);
            return halved < MinInterval ? MinInterval : halved;
        }

        var relaxed = current + RelaxStep;
        return relaxed > BaseInterval ? BaseInterval : relaxed;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var url = $"https://graph.facebook.com/{FacebookOAuthDefaults.GraphApiVersion}" +
                  $"/{Uri.EscapeDataString(_igUserId)}/live_media" +
                  $"?fields={Uri.EscapeDataString(Fields)}" +
                  $"&access_token={Uri.EscapeDataString(_pageAccessToken)}";

        _log.LogInformation(
            "[InstagramLiveCommentsPoller] polling live_media for IG user {IgUserId}", _igUserId);

        var interval = BaseInterval;
        int consecutiveErrors = 0;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var outcome = await PollOnceAsync(url, ct).ConfigureAwait(false);

                switch (outcome.Kind)
                {
                    case PollKind.Fatal:
                        FatalReason = outcome.Message;
                        _log.LogWarning(
                            "[InstagramLiveCommentsPoller] stopping: {Reason}", outcome.Message);
                        return;

                    case PollKind.RateLimited:
                        _log.LogWarning(
                            "[InstagramLiveCommentsPoller] rate limited; waiting {Wait}",
                            outcome.RetryAfter);
                        await Task.Delay(outcome.RetryAfter, ct).ConfigureAwait(false);
                        continue;

                    case PollKind.Transient:
                        if (++consecutiveErrors >= MaxConsecutiveErrors)
                        {
                            _log.LogWarning(
                                "[InstagramLiveCommentsPoller] giving up after {Count} errors",
                                consecutiveErrors);
                            return;
                        }
                        await Task.Delay(interval, ct).ConfigureAwait(false);
                        continue;

                    case PollKind.NoBroadcast:
                        consecutiveErrors = 0;
                        interval = BaseInterval; // yayın açılınca hemen 1sn'den başla
                        await Task.Delay(NoBroadcastIdle, ct).ConfigureAwait(false);
                        continue;

                    case PollKind.Ok:
                        consecutiveErrors = 0;
                        var result = _watermark.Advance(outcome.MediaId!, outcome.Comments!);

                        if (result.Overflowed)
                        {
                            _log.LogWarning(
                                "[InstagramLiveCommentsPoller] comment page overflowed for media " +
                                "{MediaId} — tightening poll interval (messages may have been lost)",
                                outcome.MediaId);
                        }

                        foreach (var c in result.NewComments)
                            Publish(c);

                        interval = NextInterval(interval, result.Overflowed);
                        await Task.Delay(interval, ct).ConfigureAwait(false);
                        continue;
                }
            }
        }
        catch (OperationCanceledException) { /* expected on stop / shutdown */ }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "[InstagramLiveCommentsPoller] poll loop failed for IG user {IgUserId}", _igUserId);
        }
        finally
        {
            _completionTcs.TrySetResult();
        }
    }

    private enum PollKind { Ok, NoBroadcast, Transient, RateLimited, Fatal }

    private readonly record struct PollOutcome(
        PollKind Kind,
        string? MediaId = null,
        System.Collections.Generic.IReadOnlyList<InstagramComment>? Comments = null,
        string? Message = null,
        TimeSpan RetryAfter = default);

    private async Task<PollOutcome> PollOnceAsync(string url, CancellationToken ct)
    {
        using var reqCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        reqCts.CancelAfter(RequestTimeout);

        try
        {
            using var resp = await _http.GetAsync(url, reqCts.Token).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(reqCts.Token).ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
            {
                var kind = InstagramGraphError.Classify((int)resp.StatusCode, body);
                switch (kind)
                {
                    case InstagramErrorKind.TokenExpired:
                        return new PollOutcome(PollKind.Fatal,
                            Message: "Instagram bağlantını yenilemen gerekiyor.");
                    case InstagramErrorKind.PermissionDenied:
                        return new PollOutcome(PollKind.Fatal,
                            Message: "Instagram yorum izni verilmemiş. Facebook bağlantısını yenile.");
                    case InstagramErrorKind.RateLimited:
                        var wait = TimeSpan.FromMinutes(1);
                        if (resp.Headers.TryGetValues("X-Business-Use-Case-Usage", out var vals))
                        {
                            foreach (var v in vals)
                                if (InstagramGraphError.TryGetRetryAfter(v, out var parsed))
                                {
                                    wait = parsed;
                                    break;
                                }
                        }
                        return new PollOutcome(PollKind.RateLimited, RetryAfter: wait);
                    case InstagramErrorKind.BroadcastEnded:
                        return new PollOutcome(PollKind.NoBroadcast);
                    default:
                        _log.LogDebug(
                            "[InstagramLiveCommentsPoller] transient {Status}: {Body}",
                            (int)resp.StatusCode, Truncate(body));
                        return new PollOutcome(PollKind.Transient);
                }
            }

            var page = InstagramLiveMediaParser.Parse(body);
            if (page is null) return new PollOutcome(PollKind.NoBroadcast);

            if (page.Comments is null)
            {
                // Yayın var ama comments alanı hiç gelmedi → izin arızası.
                // "Yorum yok" sanıp sessizce dönmek arızayı görünmez yapardı.
                return new PollOutcome(PollKind.Fatal,
                    Message: "Instagram yorumları okunamıyor — yorum izni eksik görünüyor.");
            }

            return new PollOutcome(PollKind.Ok, page.MediaId, page.Comments);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "[InstagramLiveCommentsPoller] request failed");
            return new PollOutcome(PollKind.Transient);
        }
    }

    private void Publish(InstagramComment c)
    {
        // Extension "@ayse_y" gönderiyordu ve müşteri eşleştirmesi bu anahtara
        // dayanıyor; Graph username'i @'sız veriyor. Başına @ koymazsak aynı
        // müşteri iki ayrı kayda bölünür.
        var handle = string.IsNullOrEmpty(c.Username) ? "bilinmiyor" : "@" + c.Username;

        if (_spamFilter is not null)
        {
            var reason = _spamFilter.ShouldDrop(c.Text, handle, c.TimestampUnix);
            if (reason is not null)
            {
                _log.LogDebug("[InstagramLiveCommentsPoller] dropped {Id} ({Reason})", c.Id, reason);
                return;
            }
        }

        _bus.Publish(new ChatMessage(
            Id: Guid.NewGuid().ToString("N"),
            Platform: Platform,
            ExternalId: c.Id,   // gerçek comment id → mevcut dedupe olduğu gibi çalışır
            Username: handle,
            DisplayName: c.Username,
            AvatarUrl: null,    // live_media yorumlarında profil fotoğrafı yok
            Text: c.Text,
            ReceivedAt: c.TimestampUnix,
            Badges: Array.Empty<string>()));
    }

    private static string Truncate(string s) => s.Length <= 200 ? s : s.Substring(0, 200);

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { /* ignore */ }
        _cts?.Dispose();
    }
}
