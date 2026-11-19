using System;
using System.Globalization;
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
/// <see cref="InstagramAccountResolver.ResolveDetailedAsync"/> sonucunun durumu.
/// "Sayfada hesap yok" ile "çağrı düştü" ayrı tutulur: ilki kurulumun hâlidir
/// (yalnız Facebook kullanan Sayfa), ikincisi geçici olabilir.
/// </summary>
public enum InstagramResolveStatus
{
    /// <summary>Sayfaya bağlı hesap bulundu.</summary>
    Found,

    /// <summary>Çağrı başarılı, ama Sayfaya bağlı Instagram professional hesabı yok.</summary>
    NotLinked,

    /// <summary>Çağrı düştü: 2xx dışı yanıt, ağ hatası ya da ayrıştırılamayan gövde.</summary>
    Failed,
}

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
        string pageId, string pageAccessToken, CancellationToken ct) =>
        (await ResolveDetailedAsync(pageId, pageAccessToken, ct).ConfigureAwait(false)).Account;

    /// <summary>
    /// <see cref="ResolveAsync"/> ile aynı çağrı ve aynı önbellek; ek olarak null'ın
    /// sebebini söyler: Sayfada hesap yok (<see cref="InstagramResolveStatus.NotLinked"/>)
    /// ya da çağrı düştü (<see cref="InstagramResolveStatus.Failed"/>). <c>Account</c>
    /// yalnız <see cref="InstagramResolveStatus.Found"/>'da doludur ve yalnız o
    /// önbelleklenir; öbür ikisi her çağrıda yeniden sorulur. Çağıranın iptali
    /// <see cref="InstagramResolveStatus.Failed"/> sayılmaz, yükselir.
    /// </summary>
    public async Task<(InstagramAccount? Account, InstagramResolveStatus Status)> ResolveDetailedAsync(
        string pageId, string pageAccessToken, CancellationToken ct)
    {
        if (_cached is not null && string.Equals(_cachedPageId, pageId, StringComparison.Ordinal))
            return (_cached, InstagramResolveStatus.Found);

        var url = $"{GraphBase}/{Uri.EscapeDataString(pageId)}" +
                  $"?fields={Uri.EscapeDataString("instagram_business_account{id,username}")}" +
                  $"&access_token={Uri.EscapeDataString(pageAccessToken)}";

        try
        {
            using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
            {
                // Ham gövde yazılmaz: Graph ayrıştıramadığı token'ı iletide geri yazabilir.
                _log.LogWarning(
                    "[InstagramAccountResolver] {Status} for page {PageId}: {GraphCode}",
                    (int)resp.StatusCode, pageId, GraphCodeText(body));
                return (null, InstagramResolveStatus.Failed);
            }

            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("instagram_business_account", out var acc) ||
                acc.ValueKind != JsonValueKind.Object ||
                !acc.TryGetProperty("id", out var idEl))
            {
                _log.LogInformation(
                    "[InstagramAccountResolver] page {PageId} has no linked Instagram professional account",
                    pageId);
                return (null, InstagramResolveStatus.NotLinked);
            }

            var igUserId = idEl.GetString();
            if (string.IsNullOrEmpty(igUserId)) return (null, InstagramResolveStatus.NotLinked);

            var username = acc.TryGetProperty("username", out var u) ? u.GetString() : null;
            var account = new InstagramAccount(igUserId, username);

            _cachedPageId = pageId;
            _cached = account;

            _log.LogInformation(
                "[InstagramAccountResolver] page {PageId} → IG @{Username} ({IgUserId})",
                pageId, username ?? "?", igUserId);
            return (account, InstagramResolveStatus.Found);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Yalnız tip: istisna iletisi (ve istisnanın kendisi) yazılmaz, adres ya da token taşıyabilir.
            _log.LogWarning(
                "[InstagramAccountResolver] resolve failed for page {PageId}: {ExceptionType}",
                pageId, ex.GetType().Name);
            return (null, InstagramResolveStatus.Failed);
        }
    }

    /// <summary>
    /// Graph hata gövdesinin günlüğe yazılabilen özeti: <c>Graph code N, subcode M</c> (alt kod yoksa
    /// yalnız kod); gövde Graph hatası değilse ya da okunamıyorsa <c>no Graph error code</c>. İleti ve
    /// gövdenin başka hiçbir parçası taşınmaz. Poller'ın geçici hata satırı da bunu kullanır.
    /// </summary>
    internal static string GraphCodeText(string? body)
    {
        int? code = null, subcode = null;
        if (!string.IsNullOrWhiteSpace(body))
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("error", out var err)
                    && err.ValueKind == JsonValueKind.Object)
                {
                    if (err.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.Number
                        && c.TryGetInt32(out var ci)) code = ci;
                    if (err.TryGetProperty("error_subcode", out var s) && s.ValueKind == JsonValueKind.Number
                        && s.TryGetInt32(out var si)) subcode = si;
                }
            }
            catch (JsonException)
            {
                // HTML hata sayfası vb.: kod yok.
            }
        }

        if (code is null) return "no Graph error code";
        return subcode is null
            ? string.Create(CultureInfo.InvariantCulture, $"Graph code {code}")
            : string.Create(CultureInfo.InvariantCulture, $"Graph code {code}, subcode {subcode}");
    }

    /// <summary>Sayfanın saklı token'ı değişince hesap döngüsü çağırır
    /// (<c>InstagramAccountLoop.ForgetAccountIfTokenChanged</c>): yeni token'la hesap bir kez
    /// yeniden çözülür.</summary>
    public void Invalidate()
    {
        _cachedPageId = null;
        _cached = null;
    }
}
