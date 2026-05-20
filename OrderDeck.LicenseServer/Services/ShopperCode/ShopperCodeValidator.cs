using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;

namespace OrderDeck.LicenseServer.Services.ShopperCode;

public sealed record ShopperCodeValidationResult(bool IsValid, string? ErrorCode);

public interface IShopperCodeValidator
{
    /// <summary>
    /// Full validation pipeline: format → reserved → profanity → cooldown → uniqueness.
    /// Fast-exit: returns the first failure encountered.
    /// </summary>
    Task<ShopperCodeValidationResult> ValidateAsync(
        string? rawCode,
        Guid licenseId,
        DateTimeOffset? currentCodeUpdatedAt,
        CancellationToken ct);

    /// <summary>
    /// Pure function — only format / reserved / profanity checks. No DB access.
    /// </summary>
    ShopperCodeValidationResult ValidateFormat(string? rawCode);
}

public sealed class ShopperCodeValidator : IShopperCodeValidator
{
    private static readonly Regex AlphaNumLower =
        new(@"^[a-z0-9]+$", RegexOptions.Compiled);

    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "admin", "support", "login", "app", "panel", "orderdeck",
        "dashboard", "api", "customer", "shopper", "auth", "system",
        "root", "test",
    };

    // Intentionally minimal — small static list of obvious slurs in TR + EN.
    // Substring match (case-insensitive, applied post-normalization to lowercase).
    // For an expansive profanity service, integrate a third-party API later.
    private static readonly string[] ProfanityNeedles =
    [
        "kufur", "aptal", "salak",          // TR
        "fuck", "shit", "damn", "ass",      // EN
    ];

    private static readonly TimeSpan CooldownPeriod = TimeSpan.FromDays(7);

    private readonly LicenseDbContext _db;

    public ShopperCodeValidator(LicenseDbContext db) => _db = db;

    // -------------------------------------------------------------------------
    // Pure format validation (no DB)
    // -------------------------------------------------------------------------

    public ShopperCodeValidationResult ValidateFormat(string? rawCode)
    {
        if (string.IsNullOrWhiteSpace(rawCode))
            return Fail("empty");

        var normalized = rawCode.Trim().ToLowerInvariant();

        if (normalized.Length < 3 || normalized.Length > 20)
            return Fail("length");

        if (!AlphaNumLower.IsMatch(normalized))
            return Fail("format");

        if (Reserved.Contains(normalized))
            return Fail("reserved");

        foreach (var needle in ProfanityNeedles)
        {
            if (normalized.Contains(needle, StringComparison.Ordinal))
                return Fail("profanity");
        }

        return Ok();
    }

    // -------------------------------------------------------------------------
    // Full async validation (includes DB checks)
    // -------------------------------------------------------------------------

    public async Task<ShopperCodeValidationResult> ValidateAsync(
        string? rawCode,
        Guid licenseId,
        DateTimeOffset? currentCodeUpdatedAt,
        CancellationToken ct)
    {
        // 1. Format / reserved / profanity (no DB)
        var formatResult = ValidateFormat(rawCode);
        if (!formatResult.IsValid)
            return formatResult;

        // 2. Cooldown — only enforced when the license already has a code set
        if (currentCodeUpdatedAt.HasValue)
        {
            var elapsed = DateTimeOffset.UtcNow - currentCodeUpdatedAt.Value;
            if (elapsed < CooldownPeriod)
                return Fail("cooldown");
        }

        // 3. Global uniqueness — case-insensitive (codes stored lowercase)
        var normalized = rawCode!.Trim().ToLowerInvariant();
        var taken = await _db.Licenses
            .AnyAsync(l => l.ShopperCode == normalized && l.Id != licenseId, ct);

        if (taken)
            return Fail("taken");

        return Ok();
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static ShopperCodeValidationResult Ok() => new(true, null);
    private static ShopperCodeValidationResult Fail(string errorCode) => new(false, errorCode);
}
