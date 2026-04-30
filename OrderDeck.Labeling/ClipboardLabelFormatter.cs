using System.Text.RegularExpressions;

namespace OrderDeck.Labeling;

/// <summary>
/// Builds the clipboard payload that the user's existing label app (etiket.exe) consumes
/// via its clipboard polling. The expected shape is `@username YORUM`, all single-spaced.
/// </summary>
public sealed class ClipboardLabelFormatter
{
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    public string Format(string username, string originalMessage)
    {
        var u = (username ?? "").Trim();
        if (!u.StartsWith('@')) u = "@" + u;

        var msg = Whitespace.Replace(originalMessage ?? "", " ").Trim();

        return $"{u} {msg}".Trim();
    }
}
