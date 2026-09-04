using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Services.Facebook;

namespace OrderDeck.LicenseServer.Services.Instagram;

/// <summary>
/// Canlı yayın yorumuna private reply DM'i. Meta kuralları: pencere = yayın
/// süresi, yorum başına 1 reply — düşerse (pencere kapandı vb.) sessizce false,
/// Hangfire retry ANLAMSIZ: aynı yoruma ikinci deneme zaten reddedilir.
/// </summary>
public sealed class InstagramPrivateReplyClient
{
    private readonly HttpClient _http;
    private readonly FacebookOptions _fb;
    private readonly ILogger<InstagramPrivateReplyClient> _log;

    public InstagramPrivateReplyClient(
        HttpClient http, IOptions<FacebookOptions> fb, ILogger<InstagramPrivateReplyClient> log)
    {
        _http = http;
        _fb = fb.Value;
        _log = log;
    }

    public async Task<bool> SendAsync(
        string pageId, string commentId, string text, string pageToken, CancellationToken ct)
    {
        var url = $"{_fb.GraphBaseUrl.TrimEnd('/')}/{_fb.GraphApiVersion}/{pageId}/messages";
        var payload = JsonSerializer.Serialize(new
        {
            recipient = new { comment_id = commentId },
            message = new { text }
        });

        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        // Token başlıkta — URI'de olsaydı log'lara sızardı (FacebookNameClient kuralı).
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", pageToken);

        using var res = await _http.SendAsync(req, ct);
        if (res.IsSuccessStatusCode) return true;

        var body = await res.Content.ReadAsStringAsync(ct);
        _log.LogWarning("Instagram private reply düştü — comment={CommentId}, status={Status}, body={Body}",
            commentId, (int)res.StatusCode, body.Length > 512 ? body[..512] : body);
        return false;
    }
}
