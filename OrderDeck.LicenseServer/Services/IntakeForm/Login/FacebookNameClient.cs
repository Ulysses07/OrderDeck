using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Services.Facebook;

namespace OrderDeck.LicenseServer.Services.IntakeForm.Login;

public interface IFacebookNameClient
{
    Task<IntakeLoginResult> FetchNameAsync(string code, CancellationToken ct);
}

/// <summary>
/// Kayıt formu için Facebook görünen adı: code → kısa ömürlü token →
/// <c>/me?fields=id,name</c>. Masaüstü akışındaki FacebookOAuthExchanger'dan
/// AYRI — o long-lived token üretir ve kendi RedirectUri'sine bağlıdır; burada
/// token metot bitince atılır. App aynı (<c>OrderDeck:Facebook</c>).
/// GraphBaseUrl testlerde override edilebilsin diye FacebookOptions'tan gelir.
/// </summary>
public sealed class FacebookNameClient : IFacebookNameClient
{
    private readonly HttpClient _http;
    private readonly FacebookOptions _fb;
    private readonly IntakeLoginOptions _login;
    private readonly ILogger<FacebookNameClient> _log;

    public FacebookNameClient(
        HttpClient http,
        IOptions<FacebookOptions> fb,
        IOptions<IntakeLoginOptions> login,
        ILogger<FacebookNameClient> log)
    {
        _http = http;
        _fb = fb.Value;
        _login = login.Value;
        _log = log;
    }

    public async Task<IntakeLoginResult> FetchNameAsync(string code, CancellationToken ct)
    {
        try
        {
            // Sırlar GÖVDEDE — Graph, oauth/access_token için POST form kabul
            // ediyor ve FacebookOAuthExchanger da aynı yolu kullanıyor.
            using var tokenReq = new HttpRequestMessage(HttpMethod.Post,
                $"{_fb.GraphBaseUrl}/{_fb.GraphApiVersion}/oauth/access_token")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = _fb.AppId,
                    ["client_secret"] = _fb.AppSecret,
                    ["redirect_uri"] = _login.RedirectUri,
                    ["code"] = code
                })
            };
            using var tokenResp = await _http.SendAsync(tokenReq, ct).ConfigureAwait(false);
            if (!tokenResp.IsSuccessStatusCode)
            {
                _log.LogWarning("Facebook token takası başarısız — HTTP {Status}", (int)tokenResp.StatusCode);
                return new(false, "saglayici", null);
            }

            string? accessToken;
            await using (var s = await tokenResp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            using (var doc = await JsonDocument.ParseAsync(s, cancellationToken: ct).ConfigureAwait(false))
                accessToken = doc.RootElement.TryGetProperty("access_token", out var at)
                    ? at.GetString() : null;

            if (string.IsNullOrEmpty(accessToken))
            {
                _log.LogWarning("Facebook token yanıtında access_token yok");
                return new(false, "saglayici", null);
            }

            using var meReq = new HttpRequestMessage(HttpMethod.Get,
                $"{_fb.GraphBaseUrl}/{_fb.GraphApiVersion}/me?fields=id%2Cname");
            meReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var meResp = await _http.SendAsync(meReq, ct).ConfigureAwait(false);
            if (!meResp.IsSuccessStatusCode)
            {
                _log.LogWarning("Facebook /me çağrısı başarısız — HTTP {Status}", (int)meResp.StatusCode);
                return new(false, "saglayici", null);
            }

            await using var ms = await meResp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var mdoc = await JsonDocument.ParseAsync(ms, cancellationToken: ct).ConfigureAwait(false);
            var name = mdoc.RootElement.TryGetProperty("name", out var n) ? n.GetString() : null;

            if (string.IsNullOrWhiteSpace(name))
            {
                _log.LogWarning("Facebook /me yanıtında ad yok");
                return new(false, "saglayici", null);
            }

            // Handle/ChannelId yok: FB eşleştirmesi görünen adla yürüyor
            // (HandleValidator'da FB kuralı olmamasıyla aynı gerekçe).
            return new(true, null, new IntakeLinkedIdentity(name.Trim(), null, null));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Facebook ad bağlama başarısız");
            return new(false, "saglayici", null);
        }
    }
}
