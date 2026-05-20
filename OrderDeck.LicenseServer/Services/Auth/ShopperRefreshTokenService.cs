using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;

namespace OrderDeck.LicenseServer.Services.Auth;

/// <summary>
/// Shopper refresh token issue + rotate. Raw token sadece dönüş değerinde
/// görünür; DB'de yalnız SHA-256 hash saklanır. Rotation single-use:
/// eski token kullanılınca revoked, yenisi ReplacedByTokenHash ile zincir
/// halinde tutulur.
/// </summary>
public sealed class ShopperRefreshTokenService
{
    private const int LifetimeDays = 90;
    private readonly LicenseDbContext _db;

    public ShopperRefreshTokenService(LicenseDbContext db) => _db = db;

    public async Task<(string Raw, DateTimeOffset ExpiresAt)> IssueAsync(
        Guid shopperId, string? createdByIp, CancellationToken ct)
    {
        var raw = GenerateRaw();
        var now = DateTimeOffset.UtcNow;
        var expiresAt = now.AddDays(LifetimeDays);
        _db.ShopperRefreshTokens.Add(new ShopperRefreshToken
        {
            Id = Guid.NewGuid(),
            ShopperId = shopperId,
            TokenHash = Hash(raw),
            CreatedAt = now,
            ExpiresAt = expiresAt,
            CreatedByIp = createdByIp,
        });
        await _db.SaveChangesAsync(ct);
        return (raw, expiresAt);
    }

    public async Task<(Guid ShopperId, string NewRaw, DateTimeOffset NewExpiresAt)?> RotateAsync(
        string oldRaw, string? createdByIp, CancellationToken ct)
    {
        var oldHash = Hash(oldRaw);
        var old = await _db.ShopperRefreshTokens
            .FirstOrDefaultAsync(t => t.TokenHash == oldHash, ct);

        if (old is null) return null;
        if (old.RevokedAt is not null) return null;
        if (old.ExpiresAt < DateTimeOffset.UtcNow) return null;

        var newRaw = GenerateRaw();
        var newHash = Hash(newRaw);
        var now = DateTimeOffset.UtcNow;
        var newExpiresAt = now.AddDays(LifetimeDays);

        old.RevokedAt = now;
        old.ReplacedByTokenHash = newHash;

        _db.ShopperRefreshTokens.Add(new ShopperRefreshToken
        {
            Id = Guid.NewGuid(),
            ShopperId = old.ShopperId,
            TokenHash = newHash,
            CreatedAt = now,
            ExpiresAt = newExpiresAt,
            CreatedByIp = createdByIp,
        });
        await _db.SaveChangesAsync(ct);
        return (old.ShopperId, newRaw, newExpiresAt);
    }

    private static string GenerateRaw()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string Hash(string raw)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static string HashForTest(string raw) => Hash(raw);
}
