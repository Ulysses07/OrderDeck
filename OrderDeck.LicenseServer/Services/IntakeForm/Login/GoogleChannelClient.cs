using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace OrderDeck.LicenseServer.Services.IntakeForm.Login;

/// <summary>Bağlama akışının sonucu. <c>ErrorCode</c> dönüş URL'ine query
/// olarak yazılır — SABİT kod kümesi: "kanalyok" | "saglayici". Serbest metin
/// buraya asla girmez; ekrandaki karşılığını IntakeForm.cshtml.cs çevirir.</summary>
public sealed record IntakeLoginResult(bool Ok, string? ErrorCode, IntakeLinkedIdentity? Identity);

public interface IGoogleChannelClient
{
    Task<IntakeLoginResult> FetchChannelAsync(string code, CancellationToken ct);
}

/// <summary>
/// Authorization code'u access token'a çevirir, <c>channels?mine=true</c> ile
/// GİRİŞ YAPAN hesabın kanalını okur. Token değişkende yaşar ve metot bitince
/// atılır — hiçbir yere yazılmaz: ihtiyaç tek seferlik, saklamak yalnız risk.
///
/// Kapsam <c>youtube.readonly</c> — kanalı OKUMAYA yeter, yönetmeye yetmez.
/// Sırlar (client_secret, access token) gövde/başlıkta taşınır, URI'de değil:
/// AddHttpClient'ın günlükleyicisi URI'yi Information'da yazıyor.
/// </summary>
public sealed class GoogleChannelClient : IGoogleChannelClient
{
    private const string TokenUrl = "https://oauth2.googleapis.com/token";
    private const string ChannelUrl =
        "https://www.googleapis.com/youtube/v3/channels?part=id,snippet&mine=true";

    private readonly HttpClient _http;
    private readonly IntakeLoginOptions _options;
    private readonly ILogger<GoogleChannelClient> _log;

    public GoogleChannelClient(
        HttpClient http, IOptions<IntakeLoginOptions> options, ILogger<GoogleChannelClient> log)
    {
        _http = http;
        _options = options.Value;
        _log = log;
    }

    public async Task<IntakeLoginResult> FetchChannelAsync(string code, CancellationToken ct)
    {
        try
        {
            using var tokenReq = new HttpRequestMessage(HttpMethod.Post, TokenUrl)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["code"] = code,
                    ["client_id"] = _options.GoogleClientId ?? "",
                    ["client_secret"] = _options.GoogleClientSecret ?? "",
                    ["redirect_uri"] = _options.RedirectUri,
                    ["grant_type"] = "authorization_code"
                })
            };
            using var tokenResp = await _http.SendAsync(tokenReq, ct).ConfigureAwait(false);
            if (!tokenResp.IsSuccessStatusCode)
            {
                // Gövde loglanmaz: hata gövdesi bizim koddan değil Google'dan
                // gelir ve code parametresini yankılayabilir.
                _log.LogWarning("Google token takası başarısız — HTTP {Status}", (int)tokenResp.StatusCode);
                return new(false, "saglayici", null);
            }

            string? accessToken;
            await using (var s = await tokenResp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            using (var doc = await JsonDocument.ParseAsync(s, cancellationToken: ct).ConfigureAwait(false))
                accessToken = doc.RootElement.TryGetProperty("access_token", out var at)
                    ? at.GetString() : null;

            if (string.IsNullOrEmpty(accessToken))
            {
                _log.LogWarning("Google token yanıtında access_token yok");
                return new(false, "saglayici", null);
            }

            using var chReq = new HttpRequestMessage(HttpMethod.Get, ChannelUrl);
            chReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var chResp = await _http.SendAsync(chReq, ct).ConfigureAwait(false);
            if (!chResp.IsSuccessStatusCode)
            {
                _log.LogWarning("YouTube mine=true çağrısı başarısız — HTTP {Status}", (int)chResp.StatusCode);
                return new(false, "saglayici", null);
            }

            await using var cs = await chResp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var cdoc = await JsonDocument.ParseAsync(cs, cancellationToken: ct).ConfigureAwait(false);

            if (!cdoc.RootElement.TryGetProperty("items", out var items) ||
                items.ValueKind != JsonValueKind.Array || items.GetArrayLength() == 0)
            {
                // Hesap gerçek ama kanalı yok (yalnız izleyici hesabı).
                // "saglayici" DEĞİL: müşteriye "başka hesapla dene" denmeli,
                // "sorun oldu tekrar dene" değil — ikisi farklı eylem ister.
                return new(false, "kanalyok", null);
            }

            var first = items[0];
            var channelId = first.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
            string? title = null, handle = null;
            if (first.TryGetProperty("snippet", out var sn))
            {
                title = sn.TryGetProperty("title", out var t) ? t.GetString() : null;
                handle = sn.TryGetProperty("customUrl", out var cu) ? cu.GetString() : null;
            }

            if (string.IsNullOrEmpty(channelId))
            {
                _log.LogWarning("YouTube mine=true yanıtında kanal kimliği yok");
                return new(false, "saglayici", null);
            }

            return new(true, null, new IntakeLinkedIdentity(title ?? "", handle, channelId));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Google kanal bağlama başarısız");
            return new(false, "saglayici", null);
        }
    }
}
