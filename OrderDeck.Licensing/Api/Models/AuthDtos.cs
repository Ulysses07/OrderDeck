namespace OrderDeck.Licensing.Api.Models;

public sealed record RegisterRequest(string Email, string Name, string Password);
public sealed record ResendRequest(string Email);
public sealed record LoginRequest(string Email, string Password);
public sealed record LoginResponse(
    string Token,
    DateTimeOffset ExpiresAt,
    string? RefreshToken = null,
    DateTimeOffset? RefreshExpiresAt = null,
    Guid? CustomerId = null,
    string? Email = null);
public sealed record RefreshRequest(string RefreshToken);
public sealed record LogoutRequest(string RefreshToken);
public sealed record MeResponse(Guid Id, string Email, string Name, DateTimeOffset? EmailConfirmedAt, DateTimeOffset CreatedAt);
public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);
// Id nullable + sona eklendi → eski LicenseServer build'leri (PR #20 öncesi
// /me/licenses response'unda Id alanı yoktu) ile geriye uyumlu deserialize olur.
public sealed record LicenseSummary(string LicenseKey, string SkuCode, DateTimeOffset ExpiresAt, DateTimeOffset? RevokedAt, Guid? Id = null);
