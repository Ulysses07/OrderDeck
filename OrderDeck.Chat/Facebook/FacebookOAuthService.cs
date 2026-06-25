using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OrderDeck.Core.Settings;

namespace OrderDeck.Chat.Facebook;

/// <summary>
/// Owns the Facebook OAuth 2.0 lifecycle for the desktop app. Mirrors
/// <c>YouTubeOAuthService</c> in shape (Connect / Disconnect / IsConnected /
/// GetConnectedPageName), but the implementation is hand-rolled because
/// Meta doesn't ship a .NET equivalent of <c>GoogleWebAuthorizationBroker</c>
/// — there is no library that spins up the loopback listener for us.
///
/// <para>Flow on Connect:</para>
/// <list type="number">
///   <item>Build the authorise URL with <c>client_id</c>, <c>redirect_uri</c>,
///     <c>state</c>, and <c>config_id</c> (Login for Business config).</item>
///   <item>Open the default browser.</item>
///   <item>Listen on <see cref="LoopbackPrefix"/> for the redirect.</item>
///   <item>Exchange the <c>?code=...</c> for a short-lived user token.</item>
///   <item>Exchange the short-lived user token for a long-lived one (~60d).</item>
///   <item>Hit <c>/me/accounts</c>, ask the caller to pick a Page (or auto-pick
///     if there's only one).</item>
///   <item>Derive the Page access token from <c>/{page-id}?fields=access_token,name</c>.</item>
///   <item>Persist the <see cref="FacebookTokenBundle"/> via DPAPI.</item>
/// </list>
///
/// Single-operator install (same assumption as YouTube).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class FacebookOAuthService
{
    /// <summary>
    /// Loopback callback prefix. Hard-coded because Meta enforces an exact-match
    /// redirect URI in live mode; development mode accepts any
    /// <c>http://localhost*</c> URI so we don't need to register it during
    /// dev. If this ever changes, update the App Settings → Facebook Login for
    /// Business → "Valid OAuth Redirect URIs" entry on the Meta dashboard.
    /// Port 4849 is intentionally distinct from 4747 (overlay) and 4748
    /// (extension bridge) to avoid clashing with running hosted services.
    /// </summary>
    public const string LoopbackPrefix = "http://localhost:4849/facebook/callback/";

    private const string RedirectUri = "http://localhost:4849/facebook/callback";

    private static readonly string GraphBase =
        $"https://graph.facebook.com/{FacebookOAuthDefaults.GraphApiVersion}";
    private static readonly string DialogBase =
        $"https://www.facebook.com/{FacebookOAuthDefaults.GraphApiVersion}/dialog/oauth";

    private readonly Func<AppSettings> _settings;
    private readonly EncryptedFacebookTokenStore _tokenStore;
    private readonly HttpClient _http;
    private readonly ILogger<FacebookOAuthService> _log;

    // Cached after a successful Connect / IsConnected probe; cleared on
    // Disconnect. Mirrors the YouTube service so the settings dialog can
    // render "Bağlı: <Page Name>" without any Graph API hit.
    private FacebookTokenBundle? _bundle;

    public FacebookOAuthService(
        Func<AppSettings> settings,
        EncryptedFacebookTokenStore tokenStore,
        HttpClient http,
        ILogger<FacebookOAuthService> log)
    {
        _settings = settings;
        _tokenStore = tokenStore;
        _http = http;
        _log = log;
    }

    /// <summary>True if a Page access token is on disk.</summary>
    public async Task<bool> IsConnectedAsync(CancellationToken ct = default)
    {
        if (_bundle is not null) return true;
        _bundle = await _tokenStore.LoadAsync(ct).ConfigureAwait(false);
        return _bundle is not null;
    }

    /// <summary>
    /// Returns the cached Page display name, or null if not connected.
    /// </summary>
    public async Task<string?> GetConnectedPageNameAsync(CancellationToken ct = default)
    {
        if (await IsConnectedAsync(ct).ConfigureAwait(false))
            return _bundle!.PageName;
        return null;
    }

    /// <summary>
    /// Returns the current page access token (and page id), or null if not
    /// connected. Used by <c>FacebookModerationService</c> /
    /// <c>FacebookLiveCommentsPoller</c> as the bearer for Graph API calls.
    /// </summary>
    public async Task<(string PageId, string PageAccessToken)?> GetPageCredentialsAsync(
        CancellationToken ct = default)
    {
        if (!await IsConnectedAsync(ct).ConfigureAwait(false)) return null;
        return (_bundle!.PageId, _bundle.PageAccessToken);
    }

    /// <summary>
    /// Starts the OAuth dance. <paramref name="pagePicker"/> is invoked when
    /// the operator has more than one Page; it gets the candidate list and
    /// should return the user's choice (or null to abort). If they have
    /// exactly one Page we auto-pick — keeps the common case one-click.
    /// </summary>
    public async Task ConnectAsync(
        Func<IReadOnlyList<FacebookPageCandidate>, CancellationToken, Task<FacebookPageCandidate?>> pagePicker,
        CancellationToken ct = default)
    {
        var s = _settings();
        if (!HasCredentials(s, out var appId, out var appSecret, out var configId))
            throw new InvalidOperationException(
                "Facebook App ID / Secret missing. Either build a release with " +
                "FacebookOAuthDefaults populated, or drop the values into " +
                "AppSettings (settings.json) for development.");

        // 1. Authorise URL — state binds the callback to this attempt so a
        //    stale browser tab can't smuggle a code from a different session.
        var state = GenerateState();
        var url = BuildAuthorizeUrl(appId, configId, state);

        // 2. Spin up the loopback listener BEFORE opening the browser so we
        //    don't lose the redirect if Facebook is uncharacteristically fast.
        using var listener = new HttpListener();
        listener.Prefixes.Add(LoopbackPrefix);
        try
        {
            listener.Start();
        }
        catch (HttpListenerException ex)
        {
            throw new InvalidOperationException(
                $"Failed to bind {LoopbackPrefix} — another process is using " +
                "the port, or you need to run as administrator once to grant " +
                "URL ACL.", ex);
        }

        OpenBrowser(url);

        // 3. Block until either the user completes the flow or the cancel
        //    token fires (e.g. operator closed the settings dialog). The
        //    listener stops the moment we return.
        var (code, returnedState) = await WaitForCallbackAsync(listener, ct).ConfigureAwait(false);
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(state),
                Encoding.UTF8.GetBytes(returnedState ?? string.Empty)))
        {
            throw new InvalidOperationException("OAuth state mismatch — possible CSRF.");
        }

        // 4. Code → short-lived user token.
        var shortLived = await ExchangeCodeAsync(appId, appSecret, code, ct).ConfigureAwait(false);

        // 5. Short-lived → long-lived (~60d).
        var longLived = await ExchangeForLongLivedAsync(appId, appSecret, shortLived.AccessToken, ct)
            .ConfigureAwait(false);

        // 6. List the operator's Pages → ask the picker.
        var pages = await ListPagesAsync(longLived.AccessToken, ct).ConfigureAwait(false);
        if (pages.Count == 0)
            throw new InvalidOperationException(
                "Bu hesabın yönetebileceği Facebook Page bulunamadı.");

        var chosen = pages.Count == 1
            ? pages[0]
            : await pagePicker(pages, ct).ConfigureAwait(false);
        if (chosen is null)
            throw new OperationCanceledException("Operator aborted Page selection.");

        // 7. Page-scoped access token (separate from user token — what we
        //    bearer for every comment + moderation call).
        var pageToken = await GetPageAccessTokenAsync(chosen.Id, longLived.AccessToken, ct)
            .ConfigureAwait(false);

        // 8. Persist + cache.
        var expires = longLived.ExpiresIn > 0
            ? DateTimeOffset.UtcNow.AddSeconds(longLived.ExpiresIn)
            : (DateTimeOffset?)null;

        var bundle = new FacebookTokenBundle(
            UserAccessToken: longLived.AccessToken,
            PageId: chosen.Id,
            PageAccessToken: pageToken,
            PageName: chosen.Name,
            ExpiresAt: expires);

        await _tokenStore.SaveAsync(bundle, ct).ConfigureAwait(false);
        _bundle = bundle;

        // Mirror the Page id back into AppSettings so the rest of the app
        // can branch on "is Facebook configured?" without holding a reference
        // to this service.
        s.FacebookPageId = chosen.Id;

        _log.LogInformation("Facebook OAuth connected for Page {PageId} ({PageName})",
            chosen.Id, chosen.Name);
    }

    /// <summary>Clears the local DPAPI blob. The upstream grant remains —
    /// operator can revoke it from facebook.com/settings &gt; Business
    /// Integrations if they want a hard reset.</summary>
    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        await _tokenStore.ClearAsync(ct).ConfigureAwait(false);
        _bundle = null;
        _settings().FacebookPageId = null;
        _log.LogInformation("Facebook OAuth disconnected (local tokens cleared)");
    }

    // ---- helpers --------------------------------------------------------

    private static string BuildAuthorizeUrl(string appId, string configId, string state)
    {
        // config_id encodes the Login for Business permission set so we don't
        // pass scope=... at all (Meta picks scopes from the configuration).
        var sb = new StringBuilder(DialogBase);
        sb.Append("?client_id=").Append(Uri.EscapeDataString(appId));
        sb.Append("&redirect_uri=").Append(Uri.EscapeDataString(RedirectUri));
        sb.Append("&state=").Append(Uri.EscapeDataString(state));
        sb.Append("&response_type=code");
        if (!string.IsNullOrWhiteSpace(configId))
            sb.Append("&config_id=").Append(Uri.EscapeDataString(configId));
        return sb.ToString();
    }

    private static void OpenBrowser(string url)
    {
        // Process.Start with UseShellExecute=true lets Windows resolve the
        // operator's default browser — same trick as the YouTube broker.
        Process.Start(new ProcessStartInfo
        {
            FileName = url,
            UseShellExecute = true,
        });
    }

    private static async Task<(string Code, string? State)> WaitForCallbackAsync(
        HttpListener listener, CancellationToken ct)
    {
        // Hook ct so closing the settings dialog actually aborts the listener.
        using var reg = ct.Register(() =>
        {
            try { listener.Stop(); } catch { /* already disposed */ }
        });

        HttpListenerContext ctx;
        try
        {
            ctx = await listener.GetContextAsync().ConfigureAwait(false);
        }
        catch (HttpListenerException) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }
        catch (ObjectDisposedException) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }

        var query = ctx.Request.QueryString;
        var code = query["code"];
        var state = query["state"];
        var error = query["error"];
        var errorDesc = query["error_description"];

        // Always send a friendly HTML response so the user knows they can
        // close the tab — otherwise they're staring at a blank page.
        string html = error is null
            ? "<html><body style='font-family:sans-serif;text-align:center;padding-top:80px'>" +
              "<h2>Facebook bağlantısı tamamlandı.</h2>" +
              "<p>Bu sekmeyi kapatabilirsiniz.</p></body></html>"
            : $"<html><body style='font-family:sans-serif;text-align:center;padding-top:80px'>" +
              $"<h2>Bağlantı iptal edildi.</h2>" +
              $"<p>{WebUtility.HtmlEncode(errorDesc ?? error)}</p></body></html>";
        var bytes = Encoding.UTF8.GetBytes(html);
        ctx.Response.ContentType = "text/html; charset=utf-8";
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes, ct).ConfigureAwait(false);
        ctx.Response.OutputStream.Close();

        if (error is not null)
            throw new InvalidOperationException(
                $"Facebook OAuth error: {error} — {errorDesc ?? "(no description)"}");
        if (string.IsNullOrEmpty(code))
            throw new InvalidOperationException("Facebook OAuth callback missing ?code");

        return (code!, state);
    }

    private async Task<TokenResponse> ExchangeCodeAsync(
        string appId, string appSecret, string code, CancellationToken ct)
    {
        var url = $"{GraphBase}/oauth/access_token" +
                  $"?client_id={Uri.EscapeDataString(appId)}" +
                  $"&client_secret={Uri.EscapeDataString(appSecret)}" +
                  $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}" +
                  $"&code={Uri.EscapeDataString(code)}";
        return await GetJsonAsync<TokenResponse>(url, ct).ConfigureAwait(false);
    }

    private async Task<TokenResponse> ExchangeForLongLivedAsync(
        string appId, string appSecret, string shortLivedToken, CancellationToken ct)
    {
        var url = $"{GraphBase}/oauth/access_token" +
                  $"?grant_type=fb_exchange_token" +
                  $"&client_id={Uri.EscapeDataString(appId)}" +
                  $"&client_secret={Uri.EscapeDataString(appSecret)}" +
                  $"&fb_exchange_token={Uri.EscapeDataString(shortLivedToken)}";
        return await GetJsonAsync<TokenResponse>(url, ct).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<FacebookPageCandidate>> ListPagesAsync(
        string userAccessToken, CancellationToken ct)
    {
        // /me/accounts returns Pages the operator can manage. We only need
        // id + name for the picker; access_token is fetched per-Page in step 7
        // so we don't accidentally cache stale tokens for unselected Pages.
        var url = $"{GraphBase}/me/accounts?fields=id,name&access_token={Uri.EscapeDataString(userAccessToken)}";
        var resp = await GetJsonAsync<MeAccountsResponse>(url, ct).ConfigureAwait(false);
        var list = new List<FacebookPageCandidate>(resp.Data?.Count ?? 0);
        if (resp.Data is not null)
        {
            foreach (var item in resp.Data)
            {
                if (!string.IsNullOrEmpty(item.Id) && !string.IsNullOrEmpty(item.Name))
                    list.Add(new FacebookPageCandidate(item.Id!, item.Name!));
            }
        }
        return list;
    }

    private async Task<string> GetPageAccessTokenAsync(
        string pageId, string userAccessToken, CancellationToken ct)
    {
        var url = $"{GraphBase}/{Uri.EscapeDataString(pageId)}" +
                  $"?fields=access_token" +
                  $"&access_token={Uri.EscapeDataString(userAccessToken)}";
        var resp = await GetJsonAsync<PageTokenResponse>(url, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(resp.AccessToken))
            throw new InvalidOperationException(
                "Graph API returned no access_token for the selected Page. " +
                "Operator may lack admin role on that Page.");
        return resp.AccessToken!;
    }

    private async Task<T> GetJsonAsync<T>(string url, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            // Surface the Graph error body — it's small JSON, very useful in logs.
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new HttpRequestException(
                $"Graph API {(int)resp.StatusCode} {resp.ReasonPhrase}: {body}");
        }
        var parsed = await resp.Content.ReadFromJsonAsync<T>(cancellationToken: ct).ConfigureAwait(false);
        if (parsed is null)
            throw new InvalidOperationException($"Graph API returned empty body for {url}");
        return parsed;
    }

    private static string GenerateState()
    {
        Span<byte> buf = stackalloc byte[24];
        RandomNumberGenerator.Fill(buf);
        return Convert.ToHexString(buf);
    }

    /// <summary>
    /// AppSettings overrides win first (QA/dev against a separate Meta App),
    /// then the compiled-in defaults baked by the build pipeline. Returns
    /// false if either App ID or Secret is missing — config_id is optional
    /// (skip when empty → standard scope=... flow).
    /// </summary>
    private static bool HasCredentials(AppSettings settings,
        out string appId, out string appSecret, out string configId)
    {
        appId = !string.IsNullOrWhiteSpace(settings.FacebookAppId)
            ? settings.FacebookAppId!
            : FacebookOAuthDefaults.AppId;
        appSecret = !string.IsNullOrWhiteSpace(settings.FacebookAppSecret)
            ? settings.FacebookAppSecret!
            : FacebookOAuthDefaults.AppSecret;
        configId = !string.IsNullOrWhiteSpace(settings.FacebookLoginConfigId)
            ? settings.FacebookLoginConfigId!
            : FacebookOAuthDefaults.LoginConfigId;

        return !string.IsNullOrWhiteSpace(appId)
            && !string.IsNullOrWhiteSpace(appSecret);
    }

    // ---- wire shapes ----------------------------------------------------

    private sealed class TokenResponse
    {
        [JsonPropertyName("access_token")] public string AccessToken { get; set; } = "";
        [JsonPropertyName("token_type")] public string? TokenType { get; set; }
        [JsonPropertyName("expires_in")] public long ExpiresIn { get; set; }
    }

    private sealed class MeAccountsResponse
    {
        [JsonPropertyName("data")] public List<MeAccount>? Data { get; set; }
    }

    private sealed class MeAccount
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
    }

    private sealed class PageTokenResponse
    {
        [JsonPropertyName("access_token")] public string? AccessToken { get; set; }
    }
}

/// <summary>Lightweight projection of a Page row from <c>/me/accounts</c>,
/// used by the picker UI during the OAuth flow.</summary>
public sealed record FacebookPageCandidate(string Id, string Name);
