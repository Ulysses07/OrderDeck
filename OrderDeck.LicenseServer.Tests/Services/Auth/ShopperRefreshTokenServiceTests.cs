using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Auth;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Auth;

public class ShopperRefreshTokenServiceTests
{
    private static LicenseDbContext NewDb() =>
        new(new DbContextOptionsBuilder<LicenseDbContext>()
            .UseInMemoryDatabase($"shoprtsvc-{Guid.NewGuid():N}")
            .Options);

    /// <summary>R10-S01: rotasyon artık sahibin güncel parola neslini
    /// sorguladığı için token'ın bir Shopper satırına bağlanması şart.</summary>
    private static async Task<Shopper> SeedShopperAsync(
        LicenseDbContext db, int authVersion = 0)
    {
        var shopper = new Shopper
        {
            Id = Guid.NewGuid(),
            FullName = "Test Shopper",
            Phone = $"+9055{Random.Shared.Next(10000000, 99999999)}",
            PasswordHash = $"hash-{Guid.NewGuid():N}",
            AuthVersion = authVersion,
        };
        db.Shoppers.Add(shopper);
        await db.SaveChangesAsync();
        return shopper;
    }

    [Fact]
    public async Task Issue_creates_db_row_and_returns_raw_token()
    {
        await using var db = NewDb();
        var svc = new ShopperRefreshTokenService(db);
        var shopperId = Guid.NewGuid();

        var (raw, expiresAt) = await svc.IssueAsync(shopperId, authVersion: 3, "1.2.3.4", default);

        raw.Should().NotBeNullOrWhiteSpace().And.HaveLength(64);
        expiresAt.Should().BeAfter(DateTimeOffset.UtcNow.AddDays(89));

        var row = await db.ShopperRefreshTokens.SingleAsync();
        row.ShopperId.Should().Be(shopperId);
        row.TokenHash.Should().HaveLength(64);
        row.TokenHash.Should().NotBe(raw);
        row.RevokedAt.Should().BeNull();
        row.AuthVersion.Should().Be(3, "token üretildiği andaki parola neslini taşır");
    }

    [Fact]
    public async Task Rotate_revokes_old_and_returns_new_chain()
    {
        await using var db = NewDb();
        var svc = new ShopperRefreshTokenService(db);
        var shopper = await SeedShopperAsync(db, authVersion: 2);
        var (oldRaw, _) = await svc.IssueAsync(shopper.Id, shopper.AuthVersion, "1.2.3.4", default);

        var rotateResult = await svc.RotateAsync(oldRaw, "5.6.7.8", default);
        rotateResult.Should().NotBeNull();
        rotateResult!.Value.AuthVersion.Should().Be(2,
            "çağıran access token'ı bu nesille üretecek; shopper satırını " +
            "yeniden okumak, arada değişen bir parolanın neslini kapmak olurdu");

        var rows = await db.ShopperRefreshTokens.OrderBy(t => t.CreatedAt).ToListAsync();
        rows.Should().HaveCount(2);
        rows[0].RevokedAt.Should().NotBeNull();
        rows[0].ReplacedByTokenHash.Should().Be(rows[1].TokenHash);
        rows[1].ShopperId.Should().Be(shopper.Id);
        rows[1].AuthVersion.Should().Be(2, "yeni token güncel nesli devralır");
    }

    [Fact]
    public async Task Rotate_returns_null_for_unknown_token()
    {
        await using var db = NewDb();
        var svc = new ShopperRefreshTokenService(db);
        var result = await svc.RotateAsync("notarealtoken", "1.2.3.4", default);
        result.Should().BeNull();
    }

    [Fact]
    public async Task Rotate_returns_null_for_already_revoked_token()
    {
        await using var db = NewDb();
        var svc = new ShopperRefreshTokenService(db);
        var shopper = await SeedShopperAsync(db);
        var (raw, _) = await svc.IssueAsync(shopper.Id, shopper.AuthVersion, "1.2.3.4", default);
        await svc.RotateAsync(raw, "1.2.3.4", default);
        var second = await svc.RotateAsync(raw, "1.2.3.4", default);
        second.Should().BeNull();
    }

    // ── R10-S01: parola değişikliğine yarışan login'in oturumu ──────────────
    //
    // Senaryo: saldırgan ESKİ parolayla login'de — Verify eski hash'le geçti,
    // tam o anda mağdur parolayı değiştirdi (AuthVersion++ ve iptal süpürmesi
    // commit oldu). Login'in refresh insert'i süpürmenin SELECT'inden SONRA
    // koştuğu için süpürme bu token'ı hiç görmedi: parola değişmiş ama
    // saldırganın oturumu 90 gün yenilenebilir kalıyordu. Nesil damgası
    // sıralamadan bağımsız yakalar: süpürme kaçırdıysa damga bayat kalır ve
    // ilk yenileme reddedilir.

    [Fact]
    public async Task Rotate_returns_null_when_token_stamped_with_stale_auth_version()
    {
        await using var db = NewDb();
        var svc = new ShopperRefreshTokenService(db);
        var shopper = await SeedShopperAsync(db, authVersion: 0);

        // Parola değişikliği önce commit oldu (süpürme o anki aktifleri
        // iptal etti — henüz token yok, liste boş).
        shopper.AuthVersion = 1;
        await db.SaveChangesAsync();

        // Login'in insert'i süpürmeden SONRA koştu; damgası ise Verify
        // anında okunan ESKİ nesil.
        var (staleRaw, _) = await svc.IssueAsync(shopper.Id, authVersion: 0, "6.6.6.6", default);

        var result = await svc.RotateAsync(staleRaw, "6.6.6.6", default);

        result.Should().BeNull("bayat nesille üretilen token ilk yenilemede düşmeli");
        var rows = await db.ShopperRefreshTokens.ToListAsync();
        rows.Should().ContainSingle("reddedilen yenileme YENİ token üretmemeli");
    }

    [Fact]
    public async Task Rotate_returns_null_when_shopper_row_is_gone()
    {
        await using var db = NewDb();
        var svc = new ShopperRefreshTokenService(db);
        // Shopper satırı hiç yok (ör. hesap tamamen silinmiş): oturum
        // yenilenemez — "bilinmiyor"u "geçerli" saymıyoruz.
        var (raw, _) = await svc.IssueAsync(Guid.NewGuid(), authVersion: 0, "1.2.3.4", default);
        var result = await svc.RotateAsync(raw, "1.2.3.4", default);
        result.Should().BeNull();
    }

    [Fact]
    public async Task Rotate_returns_null_for_expired_token()
    {
        await using var db = NewDb();
        var svc = new ShopperRefreshTokenService(db);
        var rawSeed = new string('x', 64);
        var hash = ShopperRefreshTokenService.HashForTest(rawSeed);
        db.ShopperRefreshTokens.Add(new ShopperRefreshToken
        {
            Id = Guid.NewGuid(),
            ShopperId = Guid.NewGuid(),
            TokenHash = hash,
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-100),
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(-10),
        });
        await db.SaveChangesAsync();

        var result = await svc.RotateAsync(rawSeed, "1.2.3.4", default);
        result.Should().BeNull();
    }
}
