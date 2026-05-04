using System;
using System.Linq;
using System.Net;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Google;
using Google.Apis.YouTube.v3;
using Google.Apis.YouTube.v3.Data;
using Microsoft.Extensions.Logging;

namespace OrderDeck.Chat.YouTube;

/// <summary>
/// Two-call moderation API on top of YouTube Data API v3:
/// <list type="bullet">
///   <item><see cref="DeleteMessageAsync"/> — wraps <c>liveChatMessages.delete</c>.</item>
///   <item><see cref="BanUserAsync"/> — wraps <c>liveChatBans.insert</c>.</item>
/// </list>
///
/// Both endpoints need the operator's own active broadcast's
/// <c>liveChatId</c>; we resolve that via <c>liveBroadcasts.list?mine=true</c>
/// and cache for 60 seconds (a single broadcast lives for hours, no point
/// burning quota on every action).
///
/// Errors are mapped to user-readable Turkish strings so the
/// <c>MainShellViewModel</c> can pop a <c>MessageBox</c> directly without
/// branching on HTTP codes.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class YouTubeModerationService
{
    /// <summary>Cache TTL for the active broadcast's liveChatId. A live
    /// broadcast doesn't change its chat id mid-stream, so 60 s of staleness
    /// is fine and saves us 11 unit/poll on hot moderation paths.</summary>
    private static readonly TimeSpan LiveChatIdTtl = TimeSpan.FromSeconds(60);

    private readonly YouTubeOAuthService _oauth;
    private readonly ILogger<YouTubeModerationService> _log;

    private string? _cachedLiveChatId;
    private DateTimeOffset _cachedLiveChatIdExpiresAt;
    private readonly SemaphoreSlim _liveChatIdLock = new(1, 1);

    public YouTubeModerationService(
        YouTubeOAuthService oauth,
        ILogger<YouTubeModerationService> log)
    {
        _oauth = oauth;
        _log = log;
    }

    /// <summary>
    /// Resolves (and caches) the liveChatId of the operator's currently-active
    /// broadcast. Throws <see cref="ModerationException"/> with a friendly
    /// message if no broadcast is live or the user isn't authorised.
    /// </summary>
    public async Task<string> GetActiveLiveChatIdAsync(CancellationToken ct = default)
    {
        if (_cachedLiveChatId is not null && DateTimeOffset.UtcNow < _cachedLiveChatIdExpiresAt)
            return _cachedLiveChatId;

        await _liveChatIdLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Double-check under the lock — an earlier concurrent call may
            // have populated the cache while we were waiting.
            if (_cachedLiveChatId is not null && DateTimeOffset.UtcNow < _cachedLiveChatIdExpiresAt)
                return _cachedLiveChatId;

            using var youtube = await _oauth.CreateAuthorizedServiceAsync(ct).ConfigureAwait(false);
            var req = youtube.LiveBroadcasts.List("snippet");
            req.BroadcastStatus = LiveBroadcastsResource.ListRequest.BroadcastStatusEnum.Active;
            req.Mine = true;

            LiveBroadcastListResponse resp;
            try
            {
                resp = await req.ExecuteAsync(ct).ConfigureAwait(false);
            }
            catch (GoogleApiException ex)
            {
                throw MapException(ex);
            }

            var liveChatId = resp?.Items?
                .Select(i => i.Snippet?.LiveChatId)
                .FirstOrDefault(s => !string.IsNullOrEmpty(s));

            if (string.IsNullOrEmpty(liveChatId))
                throw new ModerationException(
                    "Aktif YouTube yayını bulunamadı. Önce YouTube Studio'dan canlı yayını başlat.");

            _cachedLiveChatId = liveChatId;
            _cachedLiveChatIdExpiresAt = DateTimeOffset.UtcNow + LiveChatIdTtl;
            return liveChatId;
        }
        finally
        {
            _liveChatIdLock.Release();
        }
    }

    /// <summary>
    /// Deletes a single chat message by its YouTube message id. The message
    /// id is exactly what the InnerTube scraper stores in
    /// <c>ChatMessage.ExternalId</c>, so callers pass that through unchanged.
    /// </summary>
    public async Task DeleteMessageAsync(string messageId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(messageId))
            throw new ArgumentException("Mesaj kimliği boş olamaz.", nameof(messageId));

        try
        {
            using var youtube = await _oauth.CreateAuthorizedServiceAsync(ct).ConfigureAwait(false);
            await youtube.LiveChatMessages.Delete(messageId).ExecuteAsync(ct).ConfigureAwait(false);
            _log.LogInformation("YouTube live chat message deleted: {MessageId}", messageId);
        }
        catch (GoogleApiException ex)
        {
            throw MapException(ex);
        }
    }

    /// <summary>
    /// Bans (permanently) a user from the operator's active live chat. The
    /// liveChatId is fetched on demand from the cached resolver; the channel
    /// id is what the InnerTube scraper stores in <c>ChatMessage.Username</c>.
    /// </summary>
    public async Task BanUserAsync(string channelId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(channelId))
            throw new ArgumentException("Kullanıcı kanal kimliği boş olamaz.", nameof(channelId));

        var liveChatId = await GetActiveLiveChatIdAsync(ct).ConfigureAwait(false);

        var ban = new LiveChatBan
        {
            Snippet = new LiveChatBanSnippet
            {
                LiveChatId = liveChatId,
                Type = "permanent",
                BannedUserDetails = new ChannelProfileDetails
                {
                    ChannelId = channelId,
                },
            },
        };

        try
        {
            using var youtube = await _oauth.CreateAuthorizedServiceAsync(ct).ConfigureAwait(false);
            await youtube.LiveChatBans.Insert(ban, "snippet").ExecuteAsync(ct).ConfigureAwait(false);
            _log.LogInformation("YouTube live chat user banned: {ChannelId}", channelId);
        }
        catch (GoogleApiException ex)
        {
            throw MapException(ex);
        }
    }

    /// <summary>Maps Google API errors to user-readable Turkish messages.</summary>
    private static ModerationException MapException(GoogleApiException ex)
    {
        return ex.HttpStatusCode switch
        {
            HttpStatusCode.Unauthorized =>
                new ModerationException("YouTube oturumun süresi doldu. Settings → YouTube'dan tekrar bağlan.", ex),
            HttpStatusCode.Forbidden =>
                new ModerationException("Bu işlem için YouTube'da yeterli yetkin yok (kanal sahibi veya moderatör olmalısın).", ex),
            HttpStatusCode.NotFound =>
                new ModerationException("Mesaj veya kullanıcı bulunamadı (zaten silinmiş ya da yayın bitmiş olabilir).", ex),
            (HttpStatusCode)429 =>
                new ModerationException("Günlük YouTube API kotası doldu. Yarın tekrar dene.", ex),
            _ => new ModerationException($"YouTube API hatası: {ex.Message}", ex),
        };
    }
}

/// <summary>UI-friendly moderation error — its message is suitable for direct
/// display in a MessageBox.</summary>
public sealed class ModerationException : Exception
{
    public ModerationException(string message) : base(message) { }
    public ModerationException(string message, Exception inner) : base(message, inner) { }
}
