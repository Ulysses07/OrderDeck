namespace OrderDeck.Licensing.Api.Models;

public sealed record RegisterRequest(string Email, string Name, string Password);
public sealed record ResendRequest(string Email);
public sealed record LoginRequest(string Email, string Password);
public sealed record LoginResponse(string Token, DateTimeOffset ExpiresAt);
public sealed record MeResponse(Guid Id, string Email, string Name, DateTimeOffset? EmailConfirmedAt, DateTimeOffset CreatedAt);
public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);
public sealed record LicenseSummary(string LicenseKey, string SkuCode, DateTimeOffset ExpiresAt, DateTimeOffset? RevokedAt);
