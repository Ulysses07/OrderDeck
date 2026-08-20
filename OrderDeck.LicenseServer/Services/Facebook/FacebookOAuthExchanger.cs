using System.Text.Json;
using Microsoft.Extensions.Options;
// GraphResult Meta Graph'ın geneline ait yapısal sonuç tipi; WhatsApp
// istemcisiyle birlikte doğdu. İkinci bir kopya çıkarmak yerine ödünç
// alınıyor — davranışı birebir aynı olmalı.
using OrderDeck.LicenseServer.Services.WhatsApp;

namespace OrderDeck.LicenseServer.Services.Facebook;

/// <summary>Takas sonucu: uzun ömürlü kullanıcı token'ı ve ömrü (sn).</summary>
public sealed record FacebookUserToken(string AccessToken, long ExpiresInSeconds);

/// <summary>
/// Masaüstünden gelen OAuth <c>code</c>'unu uzun ömürlü kullanıcı token'ına
/// çevirir. Yalnız HTTP yapar; DB'ye dokunmaz, yetki kararı vermez — böylece
/// uç testlerinde tek parça sahtelenebilir.
/// </summary>
public interface IFacebookOAuthExchanger
{
    /// <summary>İki adım tek çağrıda: <c>code</c> → kısa ömürlü → uzun ömürlü
    /// (~60 gün). Masaüstüne yalnız sonuncusu döner; kısa ömürlü token
    /// sunucudan hiç çıkmaz.</summary>
    Task<GraphResult<FacebookUserToken>> ExchangeCodeForLongLivedAsync(
        string code, CancellationToken ct);
}

public sealed class FacebookOAuthExchanger : IFacebookOAuthExchanger
{
    private readonly HttpClient _http;
    private readonly FacebookOptions _opt;
    private readonly ILogger<FacebookOAuthExchanger> _log;

    public FacebookOAuthExchanger(
        HttpClient http, IOptions<FacebookOptions> opt, ILogger<FacebookOAuthExchanger> log)
    {
        _http = http;
        _opt = opt.Value;
        _log = log;
    }

    private string TokenEndpoint =>
        $"{_opt.GraphBaseUrl.TrimEnd('/')}/{_opt.GraphApiVersion}/oauth/access_token";

    public async Task<GraphResult<FacebookUserToken>> ExchangeCodeForLongLivedAsync(
        string code, CancellationToken ct)
    {
        var shortLived = await PostAsync("code-exchange", new Dictionary<string, string>
        {
            ["client_id"] = _opt.AppId,
            ["client_secret"] = _opt.AppSecret,
            ["redirect_uri"] = _opt.RedirectUri,
            ["code"] = code,
        }, ct);
        if (!shortLived.Ok) return shortLived;

        // Kısa ömürlü token ~1-2 saat yaşıyor; masaüstü 60 günlük olanı
        // saklamak zorunda, yoksa operatör her yayın öncesi yeniden bağlanır.
        return await PostAsync("long-lived-exchange", new Dictionary<string, string>
        {
            ["grant_type"] = "fb_exchange_token",
            ["client_id"] = _opt.AppId,
            ["client_secret"] = _opt.AppSecret,
            ["fb_exchange_token"] = shortLived.Value!.AccessToken,
        }, ct);
    }

    /// <summary>
    /// App secret, code ve token GÖVDEDE gider, sorgu dizesinde DEĞİL: bu
    /// istemci <c>AddHttpClient</c> ile kayıtlı, yani LoggingHttpMessageHandler
    /// istek URI'sini Information seviyesinde yazıyor. Query redaction ikisinde
    /// de ayrı ayrı kapatılabilen bir çalışma zamanı varsayılanı — sırrı ona
    /// emanet etme.
    /// </summary>
    private async Task<GraphResult<FacebookUserToken>> PostAsync(
        string step, Dictionary<string, string> form, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(form),
        };

        HttpResponseMessage resp;
        try
        {
            resp = await _http.SendAsync(req, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Facebook OAuth ağ hatası ({Step})", step);
            return GraphResult<FacebookUserToken>.Failure("network", ex.Message);
        }

        var body = await resp.Content.ReadAsStringAsync(ct);
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (root.TryGetProperty("error", out var err))
            {
                var errCode = err.TryGetProperty("code", out var c) ? c.ToString() : null;
                var msg = err.TryGetProperty("message", out var m) ? m.GetString() : null;
                _log.LogWarning("Facebook OAuth hatası ({Step}, {Code}): {Msg}", step, errCode, msg);
                return GraphResult<FacebookUserToken>.Failure(errCode, msg);
            }

            if (!resp.IsSuccessStatusCode)
                return Opaque(step, resp, ((int)resp.StatusCode).ToString());

            var token = root.TryGetProperty("access_token", out var t) ? t.GetString() : null;
            if (string.IsNullOrWhiteSpace(token))
                return Opaque(step, resp, "unexpected-shape");

            // expires_in yoksa 0 dönüyoruz: masaüstü bunu "bilinmiyor" sayıp
            // son kullanma tarihi yazmıyor — uydurulmuş bir tarih, süresi
            // dolmuş token'ı geçerli göstermekten iyidir.
            var expires = root.TryGetProperty("expires_in", out var e)
                          && e.TryGetInt64(out var seconds)
                ? seconds
                : 0L;

            return GraphResult<FacebookUserToken>.Success(new FacebookUserToken(token!, expires));
        }
        catch (JsonException)
        {
            // Meta'nın eski OAuth ucu form-encoded dönüyordu ve GraphApiVersion
            // operatör ayarı — bu dal gerçekten erişilebilir ve o gövdede
            // access_token açıkta duruyor.
            return Opaque(step, resp, ((int)resp.StatusCode).ToString());
        }
    }

    /// <summary>Meta'nın kendi <c>error.message</c>'ı OLMAYAN başarısızlıklar.
    /// Gövde ne çağırana döner ne de log'a yazılır: her iki adımın yanıtı da
    /// token taşıyor.</summary>
    private GraphResult<FacebookUserToken> Opaque(
        string step, HttpResponseMessage resp, string code)
    {
        _log.LogWarning(
            "Facebook OAuth beklenmedik yanıt ({Step}, HTTP {Status}, {Code}): [gövde gizlendi]",
            step, (int)resp.StatusCode, code);
        return GraphResult<FacebookUserToken>.Failure(code, "beklenmedik yanıt");
    }
}
