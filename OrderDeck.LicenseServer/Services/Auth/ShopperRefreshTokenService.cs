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

    /// <summary>
    /// R10-S01: <paramref name="authVersion"/> token'a damgalanır — çağıranın
    /// parolayı DOĞRULADIĞI andaki nesil. Login, eski parolayla parola
    /// değişikliğine yarışırsa (Verify eski hash'le geçti, değişiklik + iptal
    /// süpürmesi commit oldu, bu insert süpürmenin SELECT'inden SONRA koştu)
    /// token ayakta kalır ama bayat nesli taşır; <see cref="RotateAsync"/>
    /// ilk yenilemede reddeder. Süpürme listeyle, damga nesille yakalar —
    /// hangi sıralama gerçekleşirse gerçekleşsin biri tutar.
    /// </summary>
    public async Task<(string Raw, DateTimeOffset ExpiresAt)> IssueAsync(
        Guid shopperId, int authVersion, string? createdByIp, CancellationToken ct)
    {
        var raw = GenerateRaw();
        var now = DateTimeOffset.UtcNow;
        var expiresAt = now.AddDays(LifetimeDays);
        _db.ShopperRefreshTokens.Add(new ShopperRefreshToken
        {
            Id = Guid.NewGuid(),
            ShopperId = shopperId,
            TokenHash = Hash(raw),
            AuthVersion = authVersion,
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

        // R10-S01: token, üretildiği andaki parola nesline bağlıdır. Nesil
        // ilerlemişse (parola değişti/sıfırlandı) bu token iptal süpürmesinden
        // kaçmış demektir — login'in insert'i süpürmenin SELECT'inden sonra
        // koşmuş olabilir. Süpürmenin göremediğini nesil damgası yakalar;
        // yenisi de ESKİ nesille üretilemez: aşağıdaki insert aynı damgayı
        // taşısaydı bile bu kontrol onu bir sonraki yenilemede keserdi, biz
        // hiç üretmeyerek oturumu burada bitiriyoruz.
        var currentAuthVersion = await _db.Shoppers
            .Where(s => s.Id == old.ShopperId)
            .Select(s => (int?)s.AuthVersion)
            .FirstOrDefaultAsync(ct);
        if (currentAuthVersion is null || currentAuthVersion != old.AuthVersion)
            return null;

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
            AuthVersion = old.AuthVersion, // == güncel nesil (yukarıda denendi)
            CreatedAt = now,
            ExpiresAt = newExpiresAt,
            CreatedByIp = createdByIp,
        });

        try
        {
            // Yukarıdaki RevokedAt kontrolü tek başına yetmez: iki istek aynı
            // satırı aynı anda okuyup ikisi de "iptal değil" görebilir. Asıl
            // koruma burada — RevokedAt eşzamanlılık belirteci olduğu için EF
            // iptali "WHERE RevokedAt IS NULL" ile yazar; yarışı kaybeden
            // istek 0 satır günceller ve aşağıdaki istisnayı alır. Yeni
            // token'ın eklenmesi aynı SaveChanges'te olduğundan kaybeden
            // hiçbir şey yazamaz.
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return null;
        }

        return (old.ShopperId, newRaw, newExpiresAt);
    }

    /// <summary>
    /// Shopper'ın tüm aktif refresh token'larını iptal işaretler; iptal edilen
    /// sayıyı döner. Parolanın değiştiği her yerde çağrılmalı: parola değişmiş
    /// ama eski cihazlar hâlâ yenileme yapabiliyorsa, parolayı değiştirmek
    /// hesabı geri almaz — saldırgan 90 gün boyunca oturumda kalır.
    ///
    /// SaveChanges BİLEREK burada çağrılmıyor. İptal, parola değişikliğiyle
    /// aynı SaveChanges'te commit olmalı; ayrı çağrı olsaydı arada düşen bir
    /// istek tam olarak kapatmaya çalıştığımız durumu bırakırdı.
    /// </summary>
    public async Task<int> MarkAllRevokedAsync(
        Guid shopperId, DateTimeOffset now, CancellationToken ct)
    {
        var active = await _db.ShopperRefreshTokens
            .Where(t => t.ShopperId == shopperId && t.RevokedAt == null)
            .ToListAsync(ct);
        foreach (var t in active)
            t.RevokedAt = now;
        return active.Count;
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
