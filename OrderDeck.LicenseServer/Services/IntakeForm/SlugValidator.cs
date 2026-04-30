using System.Text.RegularExpressions;

namespace OrderDeck.LicenseServer.Services.IntakeForm;

public enum SlugValidationResult
{
    Valid,
    Empty,
    InvalidLength,
    InvalidFormat,
    Reserved
}

public static class SlugValidator
{
    private static readonly Regex Pattern =
        new(@"^[a-z0-9](?:[a-z0-9-]{1,30}[a-z0-9])?$", RegexOptions.Compiled);

    private static readonly HashSet<string> ReservedWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "admin", "api", "hangfire", "me", "r", "unsubscribe",
        "password-reset", "auth", "login", "logout", "null",
        "undefined", "app", "assets", "static", "orderdeck"
    };

    public static SlugValidationResult Validate(string? slug)
    {
        if (string.IsNullOrWhiteSpace(slug))
            return SlugValidationResult.Empty;

        if (slug.Length < 3 || slug.Length > 32)
            return SlugValidationResult.InvalidLength;

        if (!Pattern.IsMatch(slug))
            return SlugValidationResult.InvalidFormat;

        if (ReservedWords.Contains(slug))
            return SlugValidationResult.Reserved;

        return SlugValidationResult.Valid;
    }
}
