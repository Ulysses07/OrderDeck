using System.Text.RegularExpressions;

namespace OrderDeck.Chat.Ingestors.YouTube;

/// <summary>
/// Pulls a YouTube video ID out of whatever the user pasted into Settings.
/// Pattern set ported from UniCast's YouTubeChatScraper.ExtractVideoId — same
/// inputs work today on YouTube and the regex landscape hasn't moved in years.
/// </summary>
public static class YouTubeVideoIdExtractor
{
    private static readonly Regex BareId = new(@"^[a-zA-Z0-9_-]{11}$", RegexOptions.Compiled);
    private static readonly Regex WatchParam = new(@"[?&]v=([a-zA-Z0-9_-]{11})", RegexOptions.Compiled);
    private static readonly Regex YoutuBeShort = new(@"youtu\.be/([a-zA-Z0-9_-]{11})", RegexOptions.Compiled);
    private static readonly Regex LivePath = new(@"youtube\.com/live/([a-zA-Z0-9_-]{11})", RegexOptions.Compiled);

    /// <summary>Returns the 11-char video ID or null if nothing matched.</summary>
    public static string? TryExtract(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        input = input.Trim();

        if (BareId.IsMatch(input)) return input;

        var m = WatchParam.Match(input);
        if (m.Success) return m.Groups[1].Value;

        m = YoutuBeShort.Match(input);
        if (m.Success) return m.Groups[1].Value;

        m = LivePath.Match(input);
        if (m.Success) return m.Groups[1].Value;

        return null;
    }
}
