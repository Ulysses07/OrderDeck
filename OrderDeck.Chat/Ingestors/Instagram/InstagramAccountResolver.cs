using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Net.Http;
using Microsoft.Extensions.Logging;
using OrderDeck.Chat.Facebook;

namespace OrderDeck.Chat.Ingestors.Instagram;

/// <summary>Bir Sayfa'ya bağlı Instagram professional hesabı.</summary>
public readonly record struct InstagramAccount(string IgUserId, string? Username);

/// <summary>
/// <c>GET /{page-id}?fields=instagram_business_account{id,username}</c> ile
/// bağlı IG hesabını çözer.
///
/// <para>Bu çağrı <c>ads_read</c> koşullu maddesinden <b>etkilenmiyor</b>
/// (yalnızca <c>live_media</c> ve <c>comments</c> uçları etkileniyor), yani
/// hesap çözümlemesi çalışıp yorum okuma patlıyorsa sorun izinlerdedir.</para>
///
/// <para>Başarılı sonuç Sayfa başına cache'lenir — yayın boyunca saniyede bir
/// aynı çağrıyı yapmanın anlamı yok. Başarısızlık <b>cache'lenmez</b>: geçici
/// bir 5xx kalıcı "bağlı hesap yok" hâline dönüşmemeli.</para>
/// </summary>
public sealed class InstagramAccountResolver
{
    private static readonly string GraphBase =
        $"https://graph.facebook.com/{FacebookOAuthDefaults.GraphApiVersion}";

    private readonly HttpClient _http;
    private readonly ILogger<InstagramAccountResolver> _log;

    private string? _cachedPageId;
    private InstagramAccount? _cached;

    public InstagramAccountResolver(HttpClient http, ILogger<InstagramAccountResolver> log)
    {
        _http = http;
        _log = log;
    }

    /// <summary>Bağlı IG hesabını döner; yoksa veya çağrı başarısızsa null.</summary>
    public async Task<InstagramAccount?> ResolveAsync(
        string pageId, string pageAccessToken, CancellationToken ct)
    {
        if (_cached is not null && string.Equals(_cachedPageId, pageId, StringComparison.Ordinal))
            return _cached;

        var url = $"{GraphBase}/{Uri.EscapeDataString(pageId)}" +
                  $"?fields={Uri.EscapeDataString("instagram_business_account{id,username}")}" +
                  $"&access_token={Uri.EscapeDataString(pageAccessToken)}";

        try
        {
            using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
            {
                _log.LogDebug(
                    "[InstagramAccountResolver] {Status} for page {PageId}: {Body}",
                    (int)resp.StatusCode, pageId, Truncate(body));
                return null;
            }

            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("instagram_business_account", out var acc) ||
                acc.ValueKind != JsonValueKind.Object ||
                !acc.TryGetProperty("id", out var idEl))
            {
                _log.LogInformation(
                    "[InstagramAccountResolver] page {PageId} has no linked Instagram professional account",
                    pageId);
                return null;
            }

            var igUserId = idEl.GetString();
            if (string.IsNullOrEmpty(igUserId)) return null;

            var username = acc.TryGetProperty("username", out var u) ? u.GetString() : null;
            var account = new InstagramAccount(igUserId, username);

            _cachedPageId = pageId;
            _cached = account;

            _log.LogInformation(
                "[InstagramAccountResolver] page {PageId} → IG @{Username} ({IgUserId})",
                pageId, username ?? "?", igUserId);
            return account;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "[InstagramAccountResolver] resolve failed for page {PageId}", pageId);
            return null;
        }
    }

    /// <summary>Facebook bağlantısı değişince (disconnect/yeniden bağlan) çağrılır.</summary>
    public void Invalidate()
    {
        _cachedPageId = null;
        _cached = null;
    }

    private static string Truncate(string s) => s.Length <= 200 ? s : s.Substring(0, 200);
}
