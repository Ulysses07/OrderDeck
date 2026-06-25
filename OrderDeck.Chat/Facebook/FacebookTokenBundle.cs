using System;

namespace OrderDeck.Chat.Facebook;

/// <summary>
/// Everything we need to call the Graph API on the operator's behalf,
/// captured in a single record so the encrypted token store has one blob
/// to persist. Both tokens are long-lived (~60 days for the user token,
/// effectively non-expiring for the page token derived from a long-lived
/// user token — Meta's quirk).
/// </summary>
/// <param name="UserAccessToken">Long-lived user access token. Used to
/// re-derive the page token if it ever gets invalidated, and to list
/// pages on initial Connect.</param>
/// <param name="PageId">The Facebook Page id the operator chose during
/// the OAuth flow.</param>
/// <param name="PageAccessToken">Long-lived page access token. Bearer for
/// every comments/moderation Graph API call.</param>
/// <param name="PageName">Display name of the connected Page, cached so
/// the settings UI can render "Bağlı: Mezat Dünyası" without an API hit.</param>
/// <param name="ExpiresAt">When the user token expires (UTC). The page
/// token effectively doesn't expire, but if the user token dies we lose
/// the ability to re-derive it, so we treat this as the overall lifetime.
/// Null = "never expires" (older Meta accounts).</param>
public sealed record FacebookTokenBundle(
    string UserAccessToken,
    string PageId,
    string PageAccessToken,
    string PageName,
    DateTimeOffset? ExpiresAt);
