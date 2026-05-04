using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OrderDeck.LicenseServer.Extension;

/// <summary>
/// Single source of truth for the DOM selectors the OrderDeck browser
/// extension uses to scrape live-chat messages off Instagram, TikTok and
/// Facebook. The extension fetches the JSON projection of <see cref="Current"/>
/// at <c>GET /api/v1/extension/selectors</c>, caches it locally, and uses it
/// every time DOM scanning runs.
///
/// <para><b>Why this exists:</b> Each platform rotates DOM internals every
/// few months, breaking sideloaded extensions silently. Pushing a new
/// installer to dozens of operators is impractical, so selectors live here
/// instead — change the constant, redeploy the license server, and every
/// extension picks up the fix within ~10 minutes via the <c>chrome.alarms</c>
/// refresh loop.</para>
///
/// <para><b>Hot-fix workflow:</b> edit the constants below → PR → merge →
/// the GitHub Actions deploy workflow rebuilds the license-server container
/// → restart on the VPS. Total time ~2 minutes; clients pull the new ETag
/// at the next refresh tick.</para>
///
/// <para><b>What this does NOT cover:</b> structural parser changes (a new
/// MutationObserver pattern, a brand-new platform). Those still require a
/// signed extension build — the schema here is data, not behaviour.</para>
/// </summary>
public static class SelectorRegistry
{
    /// <summary>
    /// The currently published selector bundle. <see cref="PublishedAt"/>
    /// MUST be bumped whenever any field below changes — the ETag is
    /// derived from the rendered JSON and clients short-circuit on
    /// If-None-Match when the ETag matches their cached value.
    /// </summary>
    public static readonly SelectorBundle Current = new(
        SchemaVersion: 1,
        PublishedAt: new DateTimeOffset(2026, 5, 4, 10, 0, 0, TimeSpan.Zero),
        Platforms: new Dictionary<string, PlatformSelectors>
        {
            ["instagram"] = new(
                IsLivePage: new IsLivePageSelectors(
                    UrlPatterns: new[] { "/live" },
                    DomSelectors: new[]
                    {
                        "[aria-label*=\"Live\" i]",
                        "[aria-label*=\"Canlı\" i]",
                    }),
                Comments: new CommentSelectors(
                    PrimaryContainers: "[aria-label*=\"yorum\" i], [aria-label*=\"comment\" i]",
                    PrimaryRowItems: "span[dir=\"auto\"]",
                    FallbackPattern: "div-2span"),
                ObserverTarget: new[] { "[role=\"main\"]", "section" },
                Validators: new ValidatorSettings(
                    UsernameMaxLength: 50,
                    MessageMaxLength: 1000,
                    UiTextBlocklist: new[]
                    {
                        "live", "messages", "share", "like", "comment", "send", "follow",
                        "canlı", "mesajlar", "paylaş", "beğen", "yorum", "gönder", "takip et",
                        "izliyor", "watching", "viewers", "izleyici",
                    },
                    TimeStringRegex: @"^\d+\s*(dk|sa|gün|sn|m|h|d|s|ay|yıl|min|hr|sec)$")),

            ["tiktok"] = new(
                IsLivePage: new IsLivePageSelectors(
                    UrlPatterns: new[] { "/live" },
                    DomSelectors: new[]
                    {
                        "[data-e2e=\"chat-list\"]",
                        "[class*=\"ChatList\"]",
                    }),
                Comments: new CommentSelectors(
                    PrimaryContainers: "[data-e2e=\"chat-message\"]",
                    PrimaryRowItems: "[data-e2e=\"comment-username\"], [data-e2e=\"chat-username\"]",
                    FallbackPattern: "data-e2e",
                    SecondaryContainers: new[]
                    {
                        "[class*=\"DivCommentItemContainer\"]",
                        "[class*=\"comment-item\"]",
                        "[class*=\"ChatMessage\"]",
                    },
                    MessageItem: "[data-e2e=\"comment-text\"], [data-e2e=\"chat-text\"]"),
                ObserverTarget: new[]
                {
                    "[data-e2e=\"chat-list\"]",
                    "[class*=\"ChatList\"]",
                    "[role=\"main\"]",
                },
                Validators: new ValidatorSettings(
                    UsernameMaxLength: 50,
                    MessageMaxLength: 1000,
                    UiTextBlocklist: new[]
                    {
                        "live", "follow", "share", "gift", "like", "comment", "send",
                        "rose", "viewers", "watching", "joined", "top", "gifts", "chat", "settings",
                    },
                    TimeStringRegex: null)),

            ["facebook"] = new(
                IsLivePage: new IsLivePageSelectors(
                    // FB Live runs from many paths and has no single "I am live"
                    // signal. Empty pattern list = always treat as scrapeable;
                    // scanForComments() returns empty when nothing matches.
                    UrlPatterns: Array.Empty<string>(),
                    DomSelectors: Array.Empty<string>(),
                    AlwaysTrue: true),
                Comments: new CommentSelectors(
                    PrimaryContainers: "[aria-label*=\"yorum\" i], [aria-label*=\"comment\" i]",
                    PrimaryRowItems: "span[dir=\"auto\"]",
                    FallbackPattern: "article-span-pair",
                    SecondaryContainers: new[] { "div[role=\"article\"]" }),
                ObserverTarget: new[] { "[role=\"complementary\"]", "[role=\"main\"]" },
                Validators: new ValidatorSettings(
                    UsernameMaxLength: 50,
                    MessageMaxLength: 1000,
                    UiTextBlocklist: new[]
                    {
                        "beğen", "like", "yanıtla", "reply", "paylaş", "share",
                        "gizle", "hide", "bildir", "report", "sabitle", "pin",
                        "yorum yap", "comment", "görüntüle", "view",
                        "canlı", "live", "izliyor", "watching", "izleyici", "viewers",
                        "mesaj gönder", "send message",
                    },
                    TimeStringRegex: @"^\d+\s*(dk|sa|gün|sn|m|h|d|s|ay|yıl|min|hr|sec)$",
                    UrlShapedUsernameDenied: true)),
        });

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    /// <summary>JSON projection. Cached so repeat callers (every extension
    /// refresh) don't allocate.</summary>
    public static string CurrentJson { get; } = JsonSerializer.Serialize(Current, JsonOpts);

    /// <summary>
    /// Strong ETag derived from the JSON body. Stable across instances /
    /// restarts as long as <see cref="Current"/> doesn't change, so a
    /// rolling deploy doesn't invalidate every cached client.
    /// </summary>
    public static string CurrentETag { get; } = ComputeETag(CurrentJson);

    private static string ComputeETag(string body)
    {
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes(body));
        var hex = new StringBuilder(hash.Length * 2);
        foreach (var b in hash) hex.Append(b.ToString("x2"));
        // Strong ETag — wrapped in quotes per RFC 7232.
        return "\"" + hex.ToString() + "\"";
    }
}

// ─── Schema records ──────────────────────────────────────────────────────
// These are the JSON contract the extension consumes. Don't rename fields
// without bumping schemaVersion — older extensions deserialize against the
// old shape and skip unknown fields, but renamed required fields break.

public sealed record SelectorBundle(
    int SchemaVersion,
    DateTimeOffset PublishedAt,
    IReadOnlyDictionary<string, PlatformSelectors> Platforms);

public sealed record PlatformSelectors(
    IsLivePageSelectors IsLivePage,
    CommentSelectors Comments,
    IReadOnlyList<string> ObserverTarget,
    ValidatorSettings Validators);

public sealed record IsLivePageSelectors(
    IReadOnlyList<string> UrlPatterns,
    IReadOnlyList<string> DomSelectors,
    bool AlwaysTrue = false);

public sealed record CommentSelectors(
    string PrimaryContainers,
    string PrimaryRowItems,
    string FallbackPattern,
    IReadOnlyList<string>? SecondaryContainers = null,
    string? MessageItem = null);

public sealed record ValidatorSettings(
    int UsernameMaxLength,
    int MessageMaxLength,
    IReadOnlyList<string> UiTextBlocklist,
    string? TimeStringRegex,
    bool UrlShapedUsernameDenied = false);
