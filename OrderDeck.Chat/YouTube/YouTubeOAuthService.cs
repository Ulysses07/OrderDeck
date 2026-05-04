using System;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Services;
using Google.Apis.Util.Store;
using Google.Apis.YouTube.v3;
using Microsoft.Extensions.Logging;
using OrderDeck.Core.Settings;

namespace OrderDeck.Chat.YouTube;

/// <summary>
/// Owns the YouTube OAuth 2.0 lifecycle for the desktop app: starting the
/// browser-based consent flow, surfacing connection state to the UI, and
/// handing out authorised <see cref="YouTubeService"/> instances to callers
/// (today, <see cref="YouTubeModerationService"/>).
///
/// Thin wrapper over Google.Apis.Auth's <c>GoogleWebAuthorizationBroker</c>:
/// the heavy lifting (loopback HTTP listener, PKCE, refresh-token storage)
/// is done by the library — we only injects the encrypted <see cref="IDataStore"/>
/// + the Client ID/Secret pair from <see cref="AppSettings"/>.
///
/// Single-user assumption: <c>USER_KEY</c> is hard-coded to "operator". If
/// we ever support multi-account on a single install (V2), this becomes the
/// caller's chosen account id.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class YouTubeOAuthService
{
    private const string UserKey = "operator";

    /// <summary>
    /// Scopes that match what's declared in the Privacy Policy on
    /// orderdeckapp.com — keep this list aligned with the audit submission.
    /// <list type="bullet">
    ///   <item><c>youtube</c> — read live chat + channel info.</item>
    ///   <item><c>youtube.force-ssl</c> — required for delete + ban moderation.</item>
    /// </list>
    /// </summary>
    public static readonly string[] Scopes =
    {
        YouTubeService.Scope.Youtube,
        YouTubeService.Scope.YoutubeForceSsl,
    };

    private readonly Func<AppSettings> _settings;
    private readonly IDataStore _tokenStore;
    private readonly ILogger<YouTubeOAuthService> _log;

    // Cached after a successful Connect or a successful "is connected" probe;
    // cleared on Disconnect. Lets the UI render the channel title without
    // hitting the API on every settings dialog open.
    private UserCredential? _credential;
    private string? _channelTitle;

    public YouTubeOAuthService(
        Func<AppSettings> settings,
        IDataStore tokenStore,
        ILogger<YouTubeOAuthService> log)
    {
        _settings = settings;
        _tokenStore = tokenStore;
        _log = log;
    }

    /// <summary>True if a non-revoked refresh token is on disk for the operator.</summary>
    public async Task<bool> IsConnectedAsync(CancellationToken ct = default)
    {
        if (_credential is not null) return true;

        // Probing the data store directly (without invoking the broker) avoids
        // popping a browser tab when the caller is just rendering UI state.
        // The exact token shape is owned by Google.Apis.Auth, so we go via
        // the flow instead of typing the JSON ourselves.
        var settings = _settings();
        if (string.IsNullOrWhiteSpace(settings.YouTubeOAuthClientId) ||
            string.IsNullOrWhiteSpace(settings.YouTubeOAuthClientSecret))
            return false;

        try
        {
            var flow = BuildFlow(settings);
            var token = await flow.LoadTokenAsync(UserKey, ct).ConfigureAwait(false);
            return token is { RefreshToken.Length: > 0 };
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Token store probe failed; treating as disconnected");
            return false;
        }
    }

    /// <summary>
    /// Starts (or reuses) the OAuth flow. Pops the operator's default browser
    /// to Google's consent page; blocks until they grant or cancel. On
    /// success the refresh token is in the encrypted store and a
    /// <see cref="UserCredential"/> is cached for the rest of the process.
    /// </summary>
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        var settings = _settings();
        if (string.IsNullOrWhiteSpace(settings.YouTubeOAuthClientId) ||
            string.IsNullOrWhiteSpace(settings.YouTubeOAuthClientSecret))
            throw new InvalidOperationException(
                "YouTube OAuth Client ID/Secret missing in AppSettings. " +
                "Drop them into settings.json before connecting.");

        var flow = BuildFlow(settings);
        // GoogleWebAuthorizationBroker wraps an installed-app flow with a
        // local HTTP listener for the redirect; first call without a stored
        // token opens the browser, subsequent calls just return the cached
        // credential.
        var clientSecrets = new ClientSecrets
        {
            ClientId = settings.YouTubeOAuthClientId,
            ClientSecret = settings.YouTubeOAuthClientSecret,
        };
        _credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
            clientSecrets,
            Scopes,
            UserKey,
            ct,
            _tokenStore).ConfigureAwait(false);

        _channelTitle = null; // force a fresh lookup on next call
        _log.LogInformation("YouTube OAuth connected for operator");
    }

    /// <summary>Revokes the local refresh token (token store cleared). The
    /// upstream Google grant is left in place — operator can revoke from
    /// myaccount.google.com if they want a hard reset.</summary>
    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        await _tokenStore.ClearAsync().ConfigureAwait(false);
        _credential = null;
        _channelTitle = null;
        _log.LogInformation("YouTube OAuth disconnected (local tokens cleared)");
    }

    /// <summary>
    /// Returns the connected channel's display title (e.g. "Mezat Dünyası"),
    /// or null if not connected. Cached after first lookup so the settings
    /// dialog can render it without burning quota on every open.
    /// </summary>
    public async Task<string?> GetConnectedChannelTitleAsync(CancellationToken ct = default)
    {
        if (_channelTitle is not null) return _channelTitle;
        if (!await IsConnectedAsync(ct).ConfigureAwait(false)) return null;

        try
        {
            using var youtube = await CreateAuthorizedServiceAsync(ct).ConfigureAwait(false);
            var req = youtube.Channels.List("snippet");
            req.Mine = true;
            var resp = await req.ExecuteAsync(ct).ConfigureAwait(false);
            _channelTitle = resp?.Items?.FirstOrDefault()?.Snippet?.Title;
            return _channelTitle;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to fetch connected channel title");
            return null;
        }
    }

    /// <summary>
    /// Hands out an authorised <see cref="YouTubeService"/> for callers that
    /// need to make API calls (read live chat, delete a message, ban a user).
    /// Caller disposes. The credential auto-refreshes the access token from
    /// the encrypted refresh token when needed.
    /// </summary>
    public async Task<YouTubeService> CreateAuthorizedServiceAsync(CancellationToken ct = default)
    {
        if (_credential is null)
        {
            // Re-hydrate from disk without showing the browser. If there's
            // no token on disk we fall through to the broker which will
            // open the browser — matching the "user clicked something that
            // needed YouTube" UX.
            await ConnectAsync(ct).ConfigureAwait(false);
        }

        return new YouTubeService(new BaseClientService.Initializer
        {
            HttpClientInitializer = _credential,
            ApplicationName = "OrderDeck",
        });
    }

    private GoogleAuthorizationCodeFlow BuildFlow(AppSettings settings)
    {
        return new GoogleAuthorizationCodeFlow(new GoogleAuthorizationCodeFlow.Initializer
        {
            ClientSecrets = new ClientSecrets
            {
                ClientId = settings.YouTubeOAuthClientId,
                ClientSecret = settings.YouTubeOAuthClientSecret,
            },
            Scopes = Scopes,
            DataStore = _tokenStore,
        });
    }
}
