using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Caching.Memory;

namespace OrderDeck.LicenseServer.Controllers;

/// <summary>
/// Public (anonim) YouTube handle doğrulama — intake formu, girilen YouTube
/// kullanıcı adının gerçekten bir kanala karşılık gelip gelmediğini gösterir.
/// <c>channels.list?forHandle</c> kullanır (1 kota birimi/çağrı; <c>search.list</c>
/// 100 birim olduğu için KULLANILMAZ). Sonuçlar handle bazında 1 saat cache'lenir
/// ve IP başına rate-limit uygulanır → kota tüketimi ihmal edilebilir.
///
/// API key sunucu config'inden (<c>YouTube:ApiKey</c>, VPS .env
/// <c>YouTube__ApiKey</c>) okunur. Key yoksa <c>available:false</c> döner ve form
/// doğrulamayı sessizce atlar (yumuşak degrade).
/// </summary>
[ApiController]
[AllowAnonymous]
public sealed class YouTubeVerifyController : ControllerBase
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(1);

    private readonly IHttpClientFactory _httpFactory;
    private readonly IMemoryCache _cache;
    private readonly string? _apiKey;

    public YouTubeVerifyController(IHttpClientFactory httpFactory, IMemoryCache cache, IConfiguration config)
    {
        _httpFactory = httpFactory;
        _cache = cache;
        _apiKey = config["YouTube:ApiKey"];
    }

    public sealed record VerifyResult(bool Available, bool Exists, string? Title, string? Thumbnail, string? ChannelId);

    [HttpGet("api/public/verify/youtube")]
    [EnableRateLimiting("youtube-verify")]
    public async Task<IActionResult> Verify([FromQuery] string? handle, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
            return Ok(new VerifyResult(Available: false, Exists: false, null, null, null));

        var h = (handle ?? "").Trim().TrimStart('@').Trim().ToLowerInvariant();
        if (h.Length == 0 || h.Length > 64)
            return Ok(new VerifyResult(Available: true, Exists: false, null, null, null));

        if (_cache.TryGetValue("ytv:" + h, out VerifyResult? cached) && cached is not null)
            return Ok(cached);

        VerifyResult result;
        try
        {
            var client = _httpFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(8);
            var url = "https://www.googleapis.com/youtube/v3/channels" +
                      $"?part=id,snippet&forHandle={Uri.EscapeDataString(h)}&key={_apiKey}";
            using var resp = await client.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                // API hatası/kota → doğrulamayı atlat (form yine gönderilebilsin).
                return Ok(new VerifyResult(Available: false, Exists: false, null, null, null));
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

            if (doc.RootElement.TryGetProperty("items", out var items) &&
                items.ValueKind == JsonValueKind.Array && items.GetArrayLength() > 0)
            {
                // channels.list?forHandle → items[0].id = kanalın channelId'si (UCxxx).
                // Bunu forma saklayıp WPF'te chat kaydıyla BİREBİR eşleştirmek için döneriz.
                var channelId = items[0].TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                var snippet = items[0].GetProperty("snippet");
                var title = snippet.TryGetProperty("title", out var t) ? t.GetString() : null;
                string? thumb = null;
                if (snippet.TryGetProperty("thumbnails", out var th) &&
                    th.TryGetProperty("default", out var def) &&
                    def.TryGetProperty("url", out var u))
                    thumb = u.GetString();
                result = new VerifyResult(Available: true, Exists: true, title, thumb, channelId);
            }
            else
            {
                result = new VerifyResult(Available: true, Exists: false, null, null, null);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            // Ağ/parse hatası → doğrulamayı atlat.
            return Ok(new VerifyResult(Available: false, Exists: false, null, null, null));
        }

        _cache.Set("ytv:" + h, result, CacheTtl);
        return Ok(result);
    }
}
