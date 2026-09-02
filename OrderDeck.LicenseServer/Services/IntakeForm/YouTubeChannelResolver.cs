using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;

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
    private static readonly YouTubeChannel Unavailable = new(false, false, null, null, null);

    private readonly IHttpClientFactory _httpFactory;
    private readonly IMemoryCache _cache;
    private readonly string? _apiKey;

    public YouTubeChannelResolver(IHttpClientFactory httpFactory, IMemoryCache cache, IConfiguration config)
    {
        _httpFactory = httpFactory;
        _cache = cache;
        _apiKey = config["YouTube:ApiKey"];
    }

    public async Task<YouTubeChannel> ResolveHandleAsync(string? handle, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
            return Unavailable;

        var h = (handle ?? "").Trim().TrimStart('@').Trim().ToLowerInvariant();
        if (h.Length == 0 || h.Length > 64)
            return new YouTubeChannel(true, false, null, null, null);

        if (_cache.TryGetValue("ytv:" + h, out YouTubeChannel? cached) && cached is not null)
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
                return Unavailable;

            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

            if (doc.RootElement.TryGetProperty("items", out var items) &&
                items.ValueKind == JsonValueKind.Array && items.GetArrayLength() > 0)
            {
                // items[0].id = kanalın channelId'si (UCxxx). WPF'teki chat kaydıyla
                // BİREBİR eşleşen değer bu.
                var channelId = items[0].TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                var snippet = items[0].GetProperty("snippet");
                var title = snippet.TryGetProperty("title", out var t) ? t.GetString() : null;
                string? thumb = null;
                if (snippet.TryGetProperty("thumbnails", out var th) &&
                    th.TryGetProperty("default", out var def) &&
                    def.TryGetProperty("url", out var u))
                    thumb = u.GetString();
                result = new YouTubeChannel(true, true, title, thumb, channelId);
            }
            else
            {
                result = new YouTubeChannel(true, false, null, null, null);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            return Unavailable;
        }

        _cache.Set("ytv:" + h, result, CacheTtl);
        return result;
    }
}
