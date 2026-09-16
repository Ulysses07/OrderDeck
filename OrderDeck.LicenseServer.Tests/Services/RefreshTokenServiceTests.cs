using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Auth;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services;

public sealed class RefreshTokenServiceTests
{
    private static (LicenseDbContext db, RefreshTokenService svc) Build(int refreshDays = 30)
    {
        var opts = new DbContextOptionsBuilder<LicenseDbContext>()
            .UseInMemoryDatabase($"refresh-{Guid.NewGuid():N}")
            .Options;
        var db = new LicenseDbContext(opts);
        var jwtOpts = Options.Create(new JwtOptions
        {
            SecretKey = "test-secret-key-must-be-at-least-32-bytes-long-for-hs256",
            Issuer = "orderdeck-license-server",
            AccessTokenLifetimeMinutes = 15,
            RefreshTokenLifetimeDays = refreshDays
        });
        var jwt = new JwtTokenService(jwtOpts);
        var svc = new RefreshTokenService(db, jwt, jwtOpts);
        return (db, svc);
    }

    private static async Task<Customer> SeedCustomerAsync(LicenseDbContext db, int authVersion = 0)
    {
        var c = new Customer
        {
            Id = Guid.NewGuid(),
            Email = $"rt-{Guid.NewGuid():N}@example.com",
            Name = "RT Test",
            PasswordHash = "x",
            AuthVersion = authVersion,
            EmailConfirmedAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.Customers.Add(c);
        await db.SaveChangesAsync();
        return c;
    }

    [Fact]
    public async Task IssueAsync_creates_persisted_row_with_hash_only()
    {
        var (db, svc) = Build();
        var customer = await SeedCustomerAsync(db);

        var (raw, expiresAt, id) = await svc.IssueAsync(
            customer.Id, customer.AuthVersion, "10.0.0.1", CancellationToken.None);

        raw.Should().HaveLength(64);
        expiresAt.Should().BeAfter(DateTimeOffset.UtcNow);
        var stored = await db.RefreshTokens.FirstAsync(t => t.Id == id);
        stored.TokenHash.Should().NotBe(raw, "raw token must never be persisted");
        stored.TokenHash.Should().HaveLength(64);
        stored.RevokedAt.Should().BeNull();
        stored.CreatedByIp.Should().Be("10.0.0.1");
    }

    [Fact]
    public async Task RotateAsync_revokes_old_and_issues_new_pair()
    {
        var (db, svc) = Build();
        var customer = await SeedCustomerAsync(db);
        var (raw1, _, id1) = await svc.IssueAsync(customer.Id, customer.AuthVersion, null, CancellationToken.None);

        var result = await svc.RotateAsync(raw1, "10.0.0.2", CancellationToken.None);

        result.AccessToken.Should().NotBeNullOrEmpty();
        result.RefreshToken.Should().NotBeNullOrEmpty().And.NotBe(raw1);
        result.CustomerId.Should().Be(customer.Id);

        var oldRow = await db.RefreshTokens.FirstAsync(t => t.Id == id1);
        oldRow.RevokedAt.Should().NotBeNull();
        oldRow.ReplacedByTokenHash.Should().NotBeNull();

        var newRow = await db.RefreshTokens.FirstAsync(t => t.Id == result.NewRefreshTokenId);
        oldRow.ReplacedByTokenHash.Should().Be(newRow.TokenHash);
        newRow.RevokedAt.Should().BeNull();
    }

    [Fact]
    public async Task RotateAsync_with_already_rotated_token_throws()
    {
        var (db, svc) = Build();
        var customer = await SeedCustomerAsync(db);
        var (raw1, _, _) = await svc.IssueAsync(customer.Id, customer.AuthVersion, null, CancellationToken.None);
        await svc.RotateAsync(raw1, null, CancellationToken.None);

        var act = () => svc.RotateAsync(raw1, null, CancellationToken.None);

        await act.Should().ThrowAsync<RefreshTokenInvalidException>();
    }

    [Fact]
    public async Task RotateAsync_chain_pointer_walks_through_two_rotations()
    {
        var (db, svc) = Build();
        var customer = await SeedCustomerAsync(db);
        var (raw1, _, id1) = await svc.IssueAsync(customer.Id, customer.AuthVersion, null, CancellationToken.None);
        var r1 = await svc.RotateAsync(raw1, null, CancellationToken.None);
        var r2 = await svc.RotateAsync(r1.RefreshToken, null, CancellationToken.None);

        var token1 = await db.RefreshTokens.FirstAsync(t => t.Id == id1);
        var token2 = await db.RefreshTokens.FirstAsync(t => t.Id == r1.NewRefreshTokenId);
        var token3 = await db.RefreshTokens.FirstAsync(t => t.Id == r2.NewRefreshTokenId);

        token1.ReplacedByTokenHash.Should().Be(token2.TokenHash);
        token2.ReplacedByTokenHash.Should().Be(token3.TokenHash);
        token3.RevokedAt.Should().BeNull();
    }

    [Fact]
    public async Task RotateAsync_garbage_token_throws()
    {
        var (_, svc) = Build();

        var act = () => svc.RotateAsync("not-a-real-token", null, CancellationToken.None);

        await act.Should().ThrowAsync<RefreshTokenInvalidException>();
    }

    [Fact]
    public async Task RotateAsync_expired_token_throws()
    {
        var (db, svc) = Build();
        var customer = await SeedCustomerAsync(db);
        var (raw, _, id) = await svc.IssueAsync(customer.Id, customer.AuthVersion, null, CancellationToken.None);

        // Force expiry via direct DB tweak.
        var row = await db.RefreshTokens.FirstAsync(t => t.Id == id);
        row.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-5);
        await db.SaveChangesAsync();

        var act = () => svc.RotateAsync(raw, null, CancellationToken.None);

        await act.Should().ThrowAsync<RefreshTokenInvalidException>();
    }

    [Fact]
    public async Task RevokeAsync_marks_token_revoked_and_blocks_future_rotate()
    {
        var (db, svc) = Build();
        var customer = await SeedCustomerAsync(db);
        var (raw, _, id) = await svc.IssueAsync(customer.Id, customer.AuthVersion, null, CancellationToken.None);

        var revoke = await svc.RevokeAsync(raw, CancellationToken.None);
        revoke.Revoked.Should().BeTrue();

        var row = await db.RefreshTokens.FirstAsync(t => t.Id == id);
        row.RevokedAt.Should().NotBeNull();

        var act = () => svc.RotateAsync(raw, null, CancellationToken.None);
        await act.Should().ThrowAsync<RefreshTokenInvalidException>();
    }

    [Fact]
    public async Task RevokeAsync_unknown_token_is_noop()
    {
        var (_, svc) = Build();
        var result = await svc.RevokeAsync("not-real", CancellationToken.None);
        result.Revoked.Should().BeFalse();
    }

    // ── R11-S01: parola değişikliğine yarışan login'in oturumu ──────────────
    //
    // #445 bu yarışı Shopper kolunda kapattı; yayıncı/Customer kolu aynı
    // açıkla kaldı. Senaryo: saldırgan ESKİ parolayla login'de — Verify eski
    // hash'le geçti, tam o anda mağdur parolayı değiştirdi (MarkAllRevoked
    // commit oldu). Login'in refresh insert'i süpürmenin SELECT'inden SONRA
    // koştuğu için süpürme bu token'ı hiç görmedi: parola değişmiş ama
    // saldırganın oturumu 30 gün yenilenebilir kaldı. Nesil damgası
    // sıralamadan bağımsız yakalar — süpürme listeyle, damga nesille çalışır.

    [Fact]
    public async Task IssueAsync_stamps_token_with_current_auth_version()
    {
        var (db, svc) = Build();
        var customer = await SeedCustomerAsync(db, authVersion: 3);

        var (_, _, id) = await svc.IssueAsync(
            customer.Id, customer.AuthVersion, null, CancellationToken.None);

        var row = await db.RefreshTokens.FirstAsync(t => t.Id == id);
        row.AuthVersion.Should().Be(3, "token üretildiği andaki parola neslini taşır");
    }

    [Fact]
    public async Task RotateAsync_throws_when_token_stamped_with_stale_auth_version()
    {
        var (db, svc) = Build();
        var customer = await SeedCustomerAsync(db, authVersion: 0);

        // Parola değişikliği önce commit oldu; süpürme o anki aktifleri
        // iptal etti — henüz token yok, liste boş.
        customer.AuthVersion = 1;
        await db.SaveChangesAsync();

        // Login'in insert'i süpürmeden SONRA koştu; damgası Verify anında
        // okunan ESKİ nesil.
        var (staleRaw, _, _) = await svc.IssueAsync(
            customer.Id, authVersion: 0, null, CancellationToken.None);

        var act = () => svc.RotateAsync(staleRaw, null, CancellationToken.None);

        await act.Should().ThrowAsync<RefreshTokenInvalidException>(
            "bayat nesille üretilen token ilk yenilemede düşmeli");
        var rows = await db.RefreshTokens.ToListAsync();
        rows.Should().ContainSingle("reddedilen yenileme YENİ token üretmemeli");
    }

    [Fact]
    public async Task RotateAsync_new_token_inherits_current_auth_version()
    {
        var (db, svc) = Build();
        var customer = await SeedCustomerAsync(db, authVersion: 2);
        var (raw, _, _) = await svc.IssueAsync(
            customer.Id, customer.AuthVersion, null, CancellationToken.None);

        var result = await svc.RotateAsync(raw, null, CancellationToken.None);

        var newRow = await db.RefreshTokens.FirstAsync(t => t.Id == result.NewRefreshTokenId);
        newRow.AuthVersion.Should().Be(2, "yeni token güncel nesli devralır");
    }

    [Fact]
    public async Task RotateAsync_throws_when_customer_row_is_gone()
    {
        var (db, svc) = Build();
        var customer = await SeedCustomerAsync(db);
        var (raw, _, _) = await svc.IssueAsync(
            customer.Id, customer.AuthVersion, null, CancellationToken.None);

        // Hesap tamamen silindi: "bilinmiyor"u "geçerli" saymıyoruz.
        db.Customers.Remove(customer);
        await db.SaveChangesAsync();

        var act = () => svc.RotateAsync(raw, null, CancellationToken.None);

        await act.Should().ThrowAsync<RefreshTokenInvalidException>();
    }
}
