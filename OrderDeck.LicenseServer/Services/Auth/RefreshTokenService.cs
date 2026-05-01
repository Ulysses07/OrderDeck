using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;

namespace OrderDeck.LicenseServer.Services.Auth;

public sealed class RefreshTokenInvalidException : Exception
{
    public RefreshTokenInvalidException(string message) : base(message) { }
}

/// <summary>
/// Issues, rotates and revokes customer refresh tokens. Raw token values are never
/// persisted — only SHA-256(raw) is stored. On every successful rotation the old
/// row is revoked and points at the new row's hash for forensic traceability.
/// </summary>
public sealed class RefreshTokenService
{
    private readonly LicenseDbContext _db;
    private readonly JwtTokenService _jwt;
    private readonly JwtOptions _options;

    public RefreshTokenService(
        LicenseDbContext db,
        JwtTokenService jwt,
        IOptions<JwtOptions> options)
    {
        _db = db;
        _jwt = jwt;
        _options = options.Value;
    }

    /// <summary>
    /// Issues a new refresh token for the given customer. Returns the raw token
    /// (shown to the caller exactly once) and its expiry. The DB Id of the new
    /// row is returned for audit logging.
    /// </summary>
    public async Task<(string RawToken, DateTimeOffset ExpiresAt, Guid TokenId)> IssueAsync(
        Guid customerId, string? ip, CancellationToken ct)
    {
        var raw = GenerateRawToken();
        var hash = HashToken(raw);
        var now = DateTimeOffset.UtcNow;
        var lifetimeDays = _options.RefreshTokenLifetimeDays > 0 ? _options.RefreshTokenLifetimeDays : 30;
        var entity = new RefreshToken
        {
            Id = Guid.NewGuid(),
            CustomerId = customerId,
            TokenHash = hash,
            CreatedAt = now,
            ExpiresAt = now.AddDays(lifetimeDays),
            CreatedByIp = ip
        };
        _db.RefreshTokens.Add(entity);
        await _db.SaveChangesAsync(ct);
        return (raw, entity.ExpiresAt, entity.Id);
    }

    /// <summary>
    /// Validates the supplied raw refresh token. If valid (not revoked, not
    /// expired, exists), revokes it, issues a fresh access+refresh pair and
    /// returns both tokens. Throws <see cref="RefreshTokenInvalidException"/>
    /// otherwise.
    /// </summary>
    public async Task<RotateResult> RotateAsync(
        string rawRefreshToken, string? ip, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(rawRefreshToken))
            throw new RefreshTokenInvalidException("missing-token");

        var hash = HashToken(rawRefreshToken);
        var existing = await _db.RefreshTokens
            .Include(t => t.Customer)
            .FirstOrDefaultAsync(t => t.TokenHash == hash, ct);

        if (existing is null)
            throw new RefreshTokenInvalidException("not-found");
        if (existing.RevokedAt is not null)
            throw new RefreshTokenInvalidException("revoked");
        if (existing.ExpiresAt <= DateTimeOffset.UtcNow)
            throw new RefreshTokenInvalidException("expired");

        // Issue fresh pair
        var newRaw = GenerateRawToken();
        var newHash = HashToken(newRaw);
        var now = DateTimeOffset.UtcNow;
        var lifetimeDays = _options.RefreshTokenLifetimeDays > 0 ? _options.RefreshTokenLifetimeDays : 30;

        var newEntity = new RefreshToken
        {
            Id = Guid.NewGuid(),
            CustomerId = existing.CustomerId,
            TokenHash = newHash,
            CreatedAt = now,
            ExpiresAt = now.AddDays(lifetimeDays),
            CreatedByIp = ip
        };
        existing.RevokedAt = now;
        existing.ReplacedByTokenHash = newHash;

        _db.RefreshTokens.Add(newEntity);
        await _db.SaveChangesAsync(ct);

        var (access, accessExpires) = _jwt.IssueCustomerToken(existing.CustomerId, existing.Customer.Email);

        return new RotateResult(
            access, accessExpires,
            newRaw, newEntity.ExpiresAt,
            existing.CustomerId, existing.Customer.Email,
            newEntity.Id);
    }

    /// <summary>
    /// Best-effort revoke. If the token is unknown or already revoked, this is a no-op.
    /// </summary>
    public async Task<RevokeResult> RevokeAsync(string rawRefreshToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(rawRefreshToken))
            return RevokeResult.NotFound;

        var hash = HashToken(rawRefreshToken);
        var existing = await _db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (existing is null) return RevokeResult.NotFound;
        if (existing.RevokedAt is not null) return RevokeResult.AlreadyRevoked;

        existing.RevokedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return new RevokeResult(true, existing.Id, existing.CustomerId);
    }

    private static string GenerateRawToken()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string HashToken(string raw)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(raw);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

public sealed record RotateResult(
    string AccessToken,
    DateTimeOffset AccessExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshExpiresAt,
    Guid CustomerId,
    string Email,
    Guid NewRefreshTokenId);

public readonly record struct RevokeResult(bool Revoked, Guid TokenId, Guid CustomerId)
{
    public static RevokeResult NotFound => new(false, Guid.Empty, Guid.Empty);
    public static RevokeResult AlreadyRevoked => new(false, Guid.Empty, Guid.Empty);
}
