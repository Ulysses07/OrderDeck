using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace OrderDeck.LicenseServer.Services.IntakeForm;

/// <summary>
/// <c>channels.list</c> ile kanal çözümü — handle için <c>forHandle</c>, kanal
/// adresinden çıkan kimlik için <c>id</c> (ikisi de 1 kota birimi/çağrı;
/// <c>search.list</c> 100 birim olduğu için KULLANILMAZ). Sonuçlar girdi bazında
/// 1 saat cache'lenir, böylece istemcinin canlı doğrulaması ile gönderim anındaki
/// sunucu doğrulaması tek çağrıya iner. İki yolun önbellek anahtarları AYRI
/// ("ytv:" / "ytid:") — çakışsalardı biri diğerinin cevabı yerine geçerdi.
///
/// API key <c>YouTube:ApiKey</c> (VPS .env: <c>YouTube__ApiKey</c>). Key yoksa ya da
/// çağrı düşerse <c>Available:false</c> döner — yumuşak degrade.
/// </summary>
public sealed class YouTubeChannelResolver : IYouTubeChannelResolver
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(1);
    private static readonly YouTubeChannel Unavailable = new(Available: false, Exists: false, Title: null, Thumbnail: null, ChannelId: null);
    // "Baktık, yok" — Unavailable ile ANLAMCA ZIT: bu değer çağıranı bloke etmeye yetkilendirir.
    private static readonly YouTubeChannel NotFound = new(Available: true, Exists: false, Title: null, Thumbnail: null, ChannelId: null);

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

    public Task<YouTubeChannel> ResolveHandleAsync(string? handle, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
            return Task.FromResult(Unavailable);

        var h = (handle ?? "").Trim().TrimStart('@').Trim().ToLowerInvariant();
        if (h.Length == 0 || h.Length > 64)
            return Task.FromResult(NotFound);

        return QueryAsync(
            cacheKey: "ytv:" + h,
            url: "https://www.googleapis.com/youtube/v3/channels" +
                 $"?part=id,snippet&forHandle={Uri.EscapeDataString(h)}",
            logInput: h,
            ct);
    }

    /// <summary>
    /// Kanal kimliği yolu. Handle yolundan iki farkı var, ikisi de kritik:
    /// sorgu <c>forHandle</c> değil <c>id</c> (forHandle'a UC… vermek hiçbir
    /// kanala denk gelmez) ve normalizasyon yalnız <c>Trim()</c> —
    /// <c>TrimStart('@')</c>/<c>ToLowerInvariant()</c> YOK, çünkü kanal
    /// kimlikleri büyük/küçük harf duyarlı ve küçültülmüş kimlik bulunamaz.
    /// </summary>
    public Task<YouTubeChannel> ResolveChannelIdAsync(string? channelId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
            return Task.FromResult(Unavailable);

        var id = (channelId ?? "").Trim();
        if (id.Length == 0 || id.Length > 64)
            return Task.FromResult(NotFound);

        // Önek "ytid:" — handle önbelleği "ytv:" kullanıyor. Tek anahtar uzayı
        // olsaydı aynı string'in handle sonucu kimlik sorgusunun cevabı yerine
        // geçer ve müşteriye yanlış kanal onaylatılırdı.
        return QueryAsync(
            cacheKey: "ytid:" + id,
            url: "https://www.googleapis.com/youtube/v3/channels" +
                 $"?part=id,snippet&id={Uri.EscapeDataString(id)}",
            logInput: id,
            ct);
    }

    /// <summary>
    /// İki yolun ortak gövdesi: önbellek, HTTP, JSON ayrıştırma, yumuşak degrade.
    /// Tek yerde durması şart — iki kopyanın biri düzeltilip diğeri unutulursa
    /// aradaki fark sessizce müşteriye yansır.
    /// </summary>
    private async Task<YouTubeChannel> QueryAsync(string cacheKey, string url, string logInput, CancellationToken ct)
    {
        if (_cache.TryGetValue(cacheKey, out YouTubeChannel? cached) && cached is not null)
            return cached;

        YouTubeChannel result;
        try
        {
            var client = _httpFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(8);
            // Anahtar sorgu dizesinde DEĞİL başlıkta: AddHttpClient()'ın varsayılan
            // günlükleyicisi giden isteğin tam URI'sini Information seviyesinde
            // yazıyor, yani `&key=…` doğrudan konteyner günlüğüne düşerdi.
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("X-goog-api-key", _apiKey);
            using var resp = await client.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                _log.LogWarning("YouTube kanalı çözümlenemedi — HTTP {StatusCode}, girdi={Girdi}",
                    (int)resp.StatusCode, logInput);
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
                string? handle = null;
                if (items[0].TryGetProperty("snippet", out var snippet))
                {
                    title = snippet.TryGetProperty("title", out var t) ? t.GetString() : null;
                    // customUrl = kanalın "@handle"ı. Kanal adresi yolunda müşteri
                    // kullanıcı adı yazmıyor; bunu almazsak kayıt çıplak UC… ile
                    // açılır. Aynı yanıtta geliyor, ek kota harcanmıyor.
                    handle = snippet.TryGetProperty("customUrl", out var cu) ? cu.GetString() : null;
                    if (snippet.TryGetProperty("thumbnails", out var th) &&
                        th.TryGetProperty("default", out var def) &&
                        def.TryGetProperty("url", out var u))
                        thumb = u.GetString();
                }
                result = new YouTubeChannel(Available: true, Exists: true, Title: title, Thumbnail: thumb,
                    ChannelId: channelId, Handle: handle);
            }
            else
            {
                result = NotFound;
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
            _log.LogWarning(ex, "YouTube kanal çözümü başarısız, yumuşak degrade — girdi={Girdi}", logInput);
            return Unavailable;
        }

        _cache.Set(cacheKey, result, CacheTtl);
        return result;
    }
}
