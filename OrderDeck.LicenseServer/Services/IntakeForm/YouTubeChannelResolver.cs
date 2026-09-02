using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace OrderDeck.LicenseServer.Services.IntakeForm;

/// <summary>
/// <c>channels.list?forHandle</c> ile handle → kanal çözümü (1 kota birimi/çağrı;
/// <c>search.list</c> 100 birim olduğu için KULLANILMAZ). Sonuçlar handle bazında
/// 1 saat cache'lenir, böylece istemcinin canlı doğrulaması ile gönderim anındaki
/// sunucu doğrulaması tek çağrıya iner.
///
/// API key <c>YouTube:ApiKey</c> (VPS .env: <c>YouTube__ApiKey</c>). Key yoksa ya da
/// çağrı düşerse <c>Available:false</c> döner — yumuşak degrade.
/// </summary>
public sealed class YouTubeChannelResolver : IYouTubeChannelResolver
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(1);
    private static readonly YouTubeChannel Unavailable = new(Available: false, Exists: false, Title: null, Thumbnail: null, ChannelId: null);

    private readonly IHttpClientFactory _httpFactory;
    private readonly IMemoryCache _cache;
    private readonly string? _apiKey;
    private readonly ILogger<YouTubeChannelResolver> _log;

    public YouTubeChannelResolver(
        IHttpClientFactory httpFactory,
        IMemoryCache cache,
        IConfiguration config,
        ILogger<YouTubeChannelResolver> log)
    {
        _httpFactory = httpFactory;
        _cache = cache;
        _apiKey = config["YouTube:ApiKey"];
        _log = log;
    }

    public async Task<YouTubeChannel> ResolveHandleAsync(string? handle, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
            return Unavailable;

        var h = (handle ?? "").Trim().TrimStart('@').Trim().ToLowerInvariant();
        if (h.Length == 0 || h.Length > 64)
            return new YouTubeChannel(Available: true, Exists: false, Title: null, Thumbnail: null, ChannelId: null);

        var cacheKey = "ytv:" + h;
        if (_cache.TryGetValue(cacheKey, out YouTubeChannel? cached) && cached is not null)
            return cached;

        YouTubeChannel result;
        try
        {
            var client = _httpFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(8);
            var url = "https://www.googleapis.com/youtube/v3/channels" +
                      $"?part=id,snippet&forHandle={Uri.EscapeDataString(h)}&key={_apiKey}";
            using var resp = await client.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                _log.LogWarning("YouTube kanalı çözümlenemedi — HTTP {StatusCode}, handle={Handle}",
                    (int)resp.StatusCode, h);
                return Unavailable;
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

            if (doc.RootElement.TryGetProperty("items", out var items) &&
                items.ValueKind == JsonValueKind.Array && items.GetArrayLength() > 0)
            {
                // items[0].id = kanalın channelId'si (UCxxx). WPF'teki chat kaydıyla
                // BİREBİR eşleşen değer bu.
                var channelId = items[0].TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                string? title = null;
                string? thumb = null;
                if (items[0].TryGetProperty("snippet", out var snippet))
                {
                    title = snippet.TryGetProperty("title", out var t) ? t.GetString() : null;
                    if (snippet.TryGetProperty("thumbnails", out var th) &&
                        th.TryGetProperty("default", out var def) &&
                        def.TryGetProperty("url", out var u))
                        thumb = u.GetString();
                }
                result = new YouTubeChannel(Available: true, Exists: true, Title: title, Thumbnail: thumb, ChannelId: channelId);
            }
            else
            {
                result = new YouTubeChannel(Available: true, Exists: false, Title: null, Thumbnail: null, ChannelId: null);
            }
        }
        // Yalnız GERÇEK istek iptali (tarayıcı bağlantısı kesildi vb.) yeniden
        // fırlatılır. "when" koşulu olmasaydı HttpClient.Timeout süresi dolduğunda
        // atılan TaskCanceledException (ct iptal edilmemişken) da buraya düşer ve
        // müşterinin kaydı kaybolurdu — oysa bu bizim ağ sorunumuz, müşteriyi
        // kilitlememeli.
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "YouTube kanal çözümü başarısız, yumuşak degrade — handle={Handle}", h);
            return Unavailable;
        }

        _cache.Set(cacheKey, result, CacheTtl);
        return result;
    }
}
