using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Auth;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Auth;

public class PasswordResetCodeServiceTests
{
    private static readonly PasswordHasher Hasher = new();

    private static LicenseDbContext NewDb() =>
        new(new DbContextOptionsBuilder<LicenseDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static PasswordResetCodeService NewService(LicenseDbContext db, int? globalCap = null)
    {
        var dict = new Dictionary<string, string?>();
        if (globalCap is not null)
            dict["Sms:DailyGlobalCap"] = globalCap.Value.ToString();
        var config = new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
        return new PasswordResetCodeService(db, Hasher, config,
            NullLogger<PasswordResetCodeService>.Instance);
    }

    /// <summary>
    /// GÜNLÜK tavan testleri için geçmiş zaman üretir: bugünün UTC başlangıcı
    /// ile <paramref name="now"/> arasını eşit aralığa böler, <c>i</c>. en yeni.
    /// </summary>
    /// <remarks>
    /// Sabit bir geçmiş (<c>now.AddMinutes(-50)</c>) KULLANILAMAZ: servis
    /// günlük tavanı <c>CreatedAt &gt;= todayStart</c> ile sayıyor
    /// (<see cref="PasswordResetCodeService.IssueAsync"/>), yani gece yarısından
    /// hemen sonra çalışan koşuda o satırlar DÜNE düşer, tavan hiç dolmaz ve
    /// test saate bağlı olarak kırılır. 2026-08-15'te CI 00:47 UTC'de tam
    /// böyle kırıldı. Aralığı <c>now - todayStart</c> ile ölçeklemek satırları
    /// her koşuda bugünün içinde tutuyor.
    /// </remarks>
    private static DateTimeOffset EarlierToday(DateTimeOffset now, int i, int count)
    {
        var todayStart = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
        return now - (now - todayStart) * (i + 1) / (count + 1);
    }

    private static async Task<Shopper> SeedShopperAsync(LicenseDbContext db)
    {
        var now = DateTimeOffset.UtcNow;
        var shopper = new Shopper
        {
            Id = Guid.NewGuid(),
            FullName = "S",
            Phone = "+9055" + Random.Shared.Next(10_000_000, 99_999_999),
            PasswordHash = Hasher.Hash("Password1!"),
            Address = "A",
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Shoppers.Add(shopper);
        await db.SaveChangesAsync();
        return shopper;
    }

    [Fact]
    public async Task IssueAsync_returns_code_and_persists_row()
    {
        using var db = NewDb();
        var svc = NewService(db);
        var shopper = await SeedShopperAsync(db);

        var code = await svc.IssueAsync(shopper, "1.2.3.4");

        code.Should().NotBeNull();
        code!.Length.Should().Be(6);
        code.Should().MatchRegex(@"^\d{6}$");
        var row = await db.ShopperPasswordResetCodes.SingleAsync();
        row.ShopperId.Should().Be(shopper.Id);
        row.CodeHash.Should().NotBe(code, "code is hashed, not stored plaintext");
        row.RequestIp.Should().Be("1.2.3.4");
    }

    [Fact]
    public async Task IssueAsync_within_cooldown_returns_null()
    {
        using var db = NewDb();
        var svc = NewService(db);
        var shopper = await SeedShopperAsync(db);

        var first = await svc.IssueAsync(shopper, "1.2.3.4");
        first.Should().NotBeNull();
        var second = await svc.IssueAsync(shopper, "1.2.3.4");
        second.Should().BeNull("60s cooldown blocks an immediate second code");
    }

    [Fact]
    public async Task IssueAsync_phone_daily_cap_returns_null()
    {
        using var db = NewDb();
        var svc = NewService(db);
        var shopper = await SeedShopperAsync(db);

        // 5 kod BUGÜN (bkz. EarlierToday: gece yarısı tuzağı) ve geçmişe
        // yayılmış. Tavan kontrolü cooldown'dan önce çalıştığı için iddia
        // satırlar cooldown içine düşse de tavanı ölçer.
        var now = DateTimeOffset.UtcNow;
        const int count = PasswordResetCodeService.PerPhoneDailyMax;
        for (int i = 0; i < count; i++)
            db.ShopperPasswordResetCodes.Add(new ShopperPasswordResetCode
            {
                Id = Guid.NewGuid(),
                ShopperId = shopper.Id,
                CodeHash = Hasher.Hash("000000"),
                CreatedAt = EarlierToday(now, i, count),
                ExpiresAt = EarlierToday(now, i, count).AddMinutes(10),
            });
        await db.SaveChangesAsync();

        var code = await svc.IssueAsync(shopper, "1.2.3.4");
        code.Should().BeNull("daily per-phone cap reached");
    }

    [Fact]
    public async Task IssueAsync_ip_hourly_cap_returns_null()
    {
        using var db = NewDb();
        var svc = NewService(db);
        var now = DateTimeOffset.UtcNow;

        // Aynı IP, son 1 saatte 5 farklı shopper'a kod (IP saatlik tavanı).
        for (int i = 0; i < PasswordResetCodeService.PerIpHourlyMax; i++)
        {
            var s = await SeedShopperAsync(db);
            db.ShopperPasswordResetCodes.Add(new ShopperPasswordResetCode
            {
                Id = Guid.NewGuid(),
                ShopperId = s.Id,
                CodeHash = Hasher.Hash("000000"),
                CreatedAt = now.AddMinutes(-5 * (i + 1)),
                ExpiresAt = now.AddMinutes(10),
                RequestIp = "9.9.9.9",
            });
        }
        await db.SaveChangesAsync();

        var victim = await SeedShopperAsync(db);
        var code = await svc.IssueAsync(victim, "9.9.9.9");
        code.Should().BeNull("hourly per-IP cap reached");
    }

    [Fact]
    public async Task IssueAsync_global_daily_cap_returns_null()
    {
        using var db = NewDb();
        var svc = NewService(db, globalCap: 1);
        var now = DateTimeOffset.UtcNow;

        // Global cap=1 ve BUGÜN zaten 1 kod var → yeni istek reddedilir.
        // Satır bugünün içinde kalmalı (bkz. EarlierToday): sabit -30dk, gece
        // yarısından sonraki ilk yarım saatte düne düşüp tavanı boşaltırdı.
        var other = await SeedShopperAsync(db);
        db.ShopperPasswordResetCodes.Add(new ShopperPasswordResetCode
        {
            Id = Guid.NewGuid(),
            ShopperId = other.Id,
            CodeHash = Hasher.Hash("000000"),
            CreatedAt = EarlierToday(now, 0, 1),
            ExpiresAt = now,
        });
        await db.SaveChangesAsync();

        var shopper = await SeedShopperAsync(db);
        var code = await svc.IssueAsync(shopper, "1.2.3.4");
        code.Should().BeNull("global daily cap reached");
    }

    [Fact]
    public async Task VerifyAndConsume_correct_code_succeeds_and_marks_consumed()
    {
        using var db = NewDb();
        var svc = NewService(db);
        var shopper = await SeedShopperAsync(db);
        var code = await svc.IssueAsync(shopper, "1.2.3.4");

        var ok = await svc.VerifyAndConsumeAsync(shopper.Id, code!);
        ok.Should().BeTrue();

        var row = await db.ShopperPasswordResetCodes.SingleAsync();
        row.ConsumedAt.Should().NotBeNull();

        // Tekrar kullanılamaz.
        var again = await svc.VerifyAndConsumeAsync(shopper.Id, code!);
        again.Should().BeFalse("consumed code cannot be reused");
    }

    [Fact]
    public async Task VerifyAndConsume_wrong_code_fails_and_increments_attempt()
    {
        using var db = NewDb();
        var svc = NewService(db);
        var shopper = await SeedShopperAsync(db);
        await svc.IssueAsync(shopper, "1.2.3.4");

        var ok = await svc.VerifyAndConsumeAsync(shopper.Id, "000001");
        ok.Should().BeFalse();
        var row = await db.ShopperPasswordResetCodes.SingleAsync();
        row.AttemptCount.Should().Be(1);
        row.ConsumedAt.Should().BeNull();
    }

    [Fact]
    public async Task VerifyAndConsume_expired_code_fails()
    {
        using var db = NewDb();
        var svc = NewService(db);
        var shopper = await SeedShopperAsync(db);
        var now = DateTimeOffset.UtcNow;
        db.ShopperPasswordResetCodes.Add(new ShopperPasswordResetCode
        {
            Id = Guid.NewGuid(),
            ShopperId = shopper.Id,
            CodeHash = Hasher.Hash("123456"),
            CreatedAt = now.AddMinutes(-20),
            ExpiresAt = now.AddMinutes(-10),   // süresi dolmuş
        });
        await db.SaveChangesAsync();

        var ok = await svc.VerifyAndConsumeAsync(shopper.Id, "123456");
        ok.Should().BeFalse("expired code rejected");
    }

    [Fact]
    public async Task VerifyAndConsume_locks_after_max_attempts()
    {
        using var db = NewDb();
        var svc = NewService(db);
        var shopper = await SeedShopperAsync(db);
        var now = DateTimeOffset.UtcNow;
        db.ShopperPasswordResetCodes.Add(new ShopperPasswordResetCode
        {
            Id = Guid.NewGuid(),
            ShopperId = shopper.Id,
            CodeHash = Hasher.Hash("123456"),
            CreatedAt = now,
            ExpiresAt = now.AddMinutes(10),
            AttemptCount = PasswordResetCodeService.MaxVerifyAttempts,   // sınırda
        });
        await db.SaveChangesAsync();

        // Doğru kod bile olsa, bu deneme sayacı aşıyor → invalidate + false.
        var ok = await svc.VerifyAndConsumeAsync(shopper.Id, "123456");
        ok.Should().BeFalse("brute-force lock invalidates the code");
        var row = await db.ShopperPasswordResetCodes.SingleAsync();
        row.ConsumedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task VerifyAndConsume_no_code_returns_false()
    {
        using var db = NewDb();
        var svc = NewService(db);
        var shopper = await SeedShopperAsync(db);

        var ok = await svc.VerifyAndConsumeAsync(shopper.Id, "123456");
        ok.Should().BeFalse();
    }
}
