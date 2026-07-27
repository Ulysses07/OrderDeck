using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.Versioning;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OrderDeck.Chat.YouTube; // reusing ModerationException — platform-agnostic UI marker

namespace OrderDeck.Chat.Facebook;

/// <summary>
/// Two-call moderation API on top of Facebook Graph:
/// <list type="bullet">
///   <item><see cref="DeleteCommentAsync"/> — wraps <c>DELETE /{comment-id}</c>.</item>
///   <item><see cref="BanUserAsync"/> — wraps <c>POST /{page-id}/blocked?psid=...</c>,
///     which blocks the user from all of the Page's content and conversations
///     (not just the current live).</item>
/// </list>
///
/// Unlike YouTube there's no <c>liveChatId</c> equivalent to cache — every
/// comment / user id is already scoped to the connected Page, so we just
/// bearer the Page access token from <see cref="FacebookOAuthService"/>.
///
/// Errors → <see cref="ModerationException"/> with Turkish text the
/// <c>MainShellViewModel</c> can show directly.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class FacebookModerationService
{
    private static readonly string GraphBase =
        $"https://graph.facebook.com/{FacebookOAuthDefaults.GraphApiVersion}";

    private readonly FacebookOAuthService _oauth;
    private readonly HttpClient _http;
    private readonly ILogger<FacebookModerationService> _log;

    public FacebookModerationService(
        FacebookOAuthService oauth,
        HttpClient http,
        ILogger<FacebookModerationService> log)
    {
        _oauth = oauth;
        _http = http;
        _log = log;
    }

    /// <summary>
    /// Deletes a Facebook live comment. <paramref name="commentId"/> is the
    /// <c>id</c> returned by the streaming endpoint (or REST <c>/comments</c>),
    /// which the chat ingestor stores in <c>ChatMessage.ExternalId</c>.
    /// </summary>
    public async Task DeleteCommentAsync(string commentId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(commentId))
            throw new ArgumentException("Yorum kimliği boş olamaz.", nameof(commentId));

        var creds = await RequireCredsAsync(ct).ConfigureAwait(false);
        var url = $"{GraphBase}/{Uri.EscapeDataString(commentId)}" +
                  $"?access_token={Uri.EscapeDataString(creds.PageAccessToken)}";

        using var resp = await _http.DeleteAsync(url, ct).ConfigureAwait(false);
        await EnsureSuccessOrThrowAsync(resp, ct).ConfigureAwait(false);

        _log.LogInformation("Facebook comment deleted: {CommentId}", commentId);
    }

    /// <summary>
    /// Bans a user from the operator's Page. <paramref name="userId"/> is the
    /// page-scoped id (PSID) returned in <c>from.id</c> of live comments. The
    /// ban applies to ALL of the Page's content, not just the current live —
    /// that's the only ban granularity Meta exposes.
    /// </summary>
    public async Task BanUserAsync(string userId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userId))
            throw new ArgumentException("Kullanıcı kimliği boş olamaz.", nameof(userId));

        var creds = await RequireCredsAsync(ct).ConfigureAwait(false);
        var url = $"{GraphBase}/{Uri.EscapeDataString(creds.PageId)}/blocked" +
                  $"?psid={Uri.EscapeDataString(userId)}" +
                  $"&access_token={Uri.EscapeDataString(creds.PageAccessToken)}";

        // Empty POST body — Graph just needs the URL params.
        using var resp = await _http.PostAsync(url, content: null, ct).ConfigureAwait(false);
        await EnsureSuccessOrThrowAsync(resp, ct).ConfigureAwait(false);

        _log.LogInformation("Facebook user blocked from page: {UserId}", userId);
    }

    /// <summary>
    /// Lifts a ban placed by <see cref="BanUserAsync"/> —
    /// <c>DELETE /{page-id}/blocked?psid=...</c>. Unlike YouTube there's no
    /// server-side ban id to remember: the PSID alone identifies the block, so
    /// this still works after an app restart and from any message that user
    /// ever posted.
    /// </summary>
    public async Task UnbanUserAsync(string userId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userId))
            throw new ArgumentException("Kullanıcı kimliği boş olamaz.", nameof(userId));

        var creds = await RequireCredsAsync(ct).ConfigureAwait(false);
        var url = $"{GraphBase}/{Uri.EscapeDataString(creds.PageId)}/blocked" +
                  $"?psid={Uri.EscapeDataString(userId)}" +
                  $"&access_token={Uri.EscapeDataString(creds.PageAccessToken)}";

        using var resp = await _http.DeleteAsync(url, ct).ConfigureAwait(false);
        await EnsureSuccessOrThrowAsync(resp, ct).ConfigureAwait(false);

        _log.LogInformation("Facebook user unblocked from page: {UserId}", userId);
    }

    private async Task<(string PageId, string PageAccessToken)> RequireCredsAsync(CancellationToken ct)
    {
        var creds = await _oauth.GetPageCredentialsAsync(ct).ConfigureAwait(false);
        if (creds is null)
            throw new ModerationException(
                "Facebook bağlı değil. Önce Settings → Facebook'tan bir Page bağla.");
        return creds.Value;
    }

    private static async Task EnsureSuccessOrThrowAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode) return;

        // Graph errors come as {"error":{"message":..,"type":..,"code":..,"error_subcode":..}}
        GraphErrorEnvelope? env = null;
        try
        {
            env = await resp.Content.ReadFromJsonAsync<GraphErrorEnvelope>(cancellationToken: ct)
                .ConfigureAwait(false);
        }
        catch
        {
            // Body wasn't JSON; we'll fall through to the generic HTTP-status mapping.
        }

        throw MapException(resp.StatusCode, env?.Error);
    }

    /// <summary>
    /// Maps Graph API errors to user-readable Turkish messages. Internal so
    /// the test suite can verify each branch without spinning up real HTTP.
    /// See https://developers.facebook.com/docs/graph-api/guides/error-handling/
    /// for the meaning of <c>error.code</c> and <c>error_subcode</c>.
    /// </summary>
    internal static ModerationException MapException(HttpStatusCode status, GraphError? error)
    {
        // Code 190 = OAuth exception family (token expired/invalidated/wrong perms).
        // Code 200 = "Permission denied" — operator removed perm from the Page.
        // Code 100 + subcode 33 = "object does not exist" (already deleted, etc.).
        // Code 4 = app-level rate limit; code 17 = user-level rate limit;
        //   code 32 = page-level rate limit. All three == "wait and retry".
        if (error is not null)
        {
            return error.Code switch
            {
                190 => new ModerationException(
                    "Facebook oturumun süresi doldu. Settings → Facebook'tan tekrar bağlan."),
                200 => new ModerationException(
                    "Bu işlem için Facebook'ta yeterli yetkin yok (Page admin/moderator olmalısın)."),
                100 when error.ErrorSubcode is 33 or 1357040 => new ModerationException(
                    "Yorum veya kullanıcı bulunamadı (zaten silinmiş ya da yayın bitmiş olabilir)."),
                4 or 17 or 32 or 613 => new ModerationException(
                    "Facebook API limiti doldu. Birkaç dakika sonra tekrar dene."),
                _ => new ModerationException(
                    $"Facebook API hatası ({error.Code}/{error.ErrorSubcode}): {error.Message}"),
            };
        }

        // No structured error → fall back on HTTP status.
        return status switch
        {
            HttpStatusCode.Unauthorized =>
                new ModerationException("Facebook oturumun süresi doldu. Settings → Facebook'tan tekrar bağlan."),
            HttpStatusCode.Forbidden =>
                new ModerationException("Bu işlem için Facebook'ta yeterli yetkin yok (Page admin/moderator olmalısın)."),
            HttpStatusCode.NotFound =>
                new ModerationException("Yorum veya kullanıcı bulunamadı (zaten silinmiş ya da yayın bitmiş olabilir)."),
            (HttpStatusCode)429 =>
                new ModerationException("Facebook API limiti doldu. Birkaç dakika sonra tekrar dene."),
            _ => new ModerationException($"Facebook API hatası: HTTP {(int)status}"),
        };
    }

    // ---- wire shapes ----------------------------------------------------

    internal sealed class GraphErrorEnvelope
    {
        [JsonPropertyName("error")] public GraphError? Error { get; set; }
    }

    internal sealed class GraphError
    {
        [JsonPropertyName("message")] public string? Message { get; set; }
        [JsonPropertyName("type")] public string? Type { get; set; }
        [JsonPropertyName("code")] public int Code { get; set; }
        [JsonPropertyName("error_subcode")] public int? ErrorSubcode { get; set; }
        [JsonPropertyName("fbtrace_id")] public string? FbTraceId { get; set; }
    }
}
