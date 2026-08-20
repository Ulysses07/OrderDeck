using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Audit;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Audit;

/// <summary>
/// Gizlilik politikası "Güvenlik kayıtları: 90 gün" diyor. Bu testler o
/// taahhüdün kodda fiilen uygulandığını, üstelik satırları silmeden
/// uygulandığını (audit izi 24 ay yaşamaya devam etmeli) kanıtlıyor.
/// </summary>
public class SecurityDataRetentionJobTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public SecurityDataRetentionJobTests(ApiFactory factory) => _factory = factory;

    private static readonly DateTimeOffset Old   = DateTimeOffset.UtcNow.AddDays(-120);
    private static readonly DateTimeOffset Fresh = DateTimeOffset.UtcNow.AddDays(-10);

    /// <summary>Her tabloya biri eski biri yeni iki satır atar. Dönen Guid'ler
    /// paylaşılan InMemory DB'de bu testin satırlarını izole etmeye yarıyor.</summary>
    private sealed record Seed(
        Guid OldAudit, Guid FreshAudit,
        Guid OldRefresh, Guid FreshRefresh,
        Guid OldShopperRefresh, Guid FreshShopperRefresh,
        Guid OldIntake, Guid FreshIntake,
        Guid OldPaymentAudit, Guid FreshPaymentAudit);

    private async Task<Seed> SeedAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();

        var s = new Seed(
            Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid());

        db.AuditLogs.AddRange(
            NewAudit(s.OldAudit, Old),
            NewAudit(s.FreshAudit, Fresh));

        db.RefreshTokens.AddRange(
            NewRefresh(s.OldRefresh, Old),
            NewRefresh(s.FreshRefresh, Fresh));

        db.ShopperRefreshTokens.AddRange(
            NewShopperRefresh(s.OldShopperRefresh, Old),
            NewShopperRefresh(s.FreshShopperRefresh, Fresh));

        db.IntakeFormSubmissions.AddRange(
            NewIntake(s.OldIntake, Old),
            NewIntake(s.FreshIntake, Fresh));

        db.PaymentSubmissionAudits.AddRange(
            NewPaymentAudit(s.OldPaymentAudit, Old),
            NewPaymentAudit(s.FreshPaymentAudit, Fresh));

        await db.SaveChangesAsync();
        return s;
    }

    private static AuditLogEntry NewAudit(Guid id, DateTimeOffset at) => new()
    {
        Id = id,
        OccurredAt = at,
        AdminId = Guid.Empty,
        AdminUsername = "test",
        EventType = "test.event",
        TargetType = "test",
        IpAddress = "203.0.113.7",
    };

    private static RefreshToken NewRefresh(Guid id, DateTimeOffset at) => new()
    {
        Id = id,
        CustomerId = Guid.NewGuid(),
        TokenHash = id.ToString("N"),
        CreatedAt = at,
        ExpiresAt = at.AddDays(30),
        CreatedByIp = "203.0.113.7",
    };

    private static ShopperRefreshToken NewShopperRefresh(Guid id, DateTimeOffset at) => new()
    {
        Id = id,
        ShopperId = Guid.NewGuid(),
        TokenHash = id.ToString("N"),
        CreatedAt = at,
        ExpiresAt = at.AddDays(30),
        CreatedByIp = "203.0.113.7",
    };

    private static IntakeFormSubmission NewIntake(Guid id, DateTimeOffset at) => new()
    {
        Id = id,
        IntakeFormConfigId = Guid.NewGuid(),
        Username = "viewer",
        FullName = "Test Kullanıcı",
        Address = "Test adres",
        SubmittedAt = at,
        IpAddress = "203.0.113.7",
        UserAgent = "Mozilla/5.0",
    };

    private static PaymentSubmissionAudit NewPaymentAudit(Guid id, DateTimeOffset at) => new()
    {
        Id = id,
        PaymentId = Guid.NewGuid(),
        ShopperId = Guid.NewGuid(),
        LicenseId = Guid.NewGuid(),
        CreatedAt = at,
        IpAddress = "203.0.113.7",
        UserAgent = "Mozilla/5.0",
        FraudFlags = "",
        ParserConfidence = "high",
    };

    private static SecurityDataRetentionJob Make(
        LicenseDbContext db, int retentionDays = 90, int batchSize = 1000)
        => new(db, Options.Create(new SecurityDataRetentionOptions
        {
            RetentionDays = retentionDays,
            BatchSize = batchSize,
        }), NullLogger<SecurityDataRetentionJob>.Instance);

    private async Task RunAsync(int retentionDays = 90, int batchSize = 1000)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        await Make(db, retentionDays, batchSize).AnonymiseAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Anonymise_clears_ip_on_rows_older_than_retention()
    {
        var s = await SeedAsync();
        await RunAsync();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();

        (await db.AuditLogs.SingleAsync(x => x.Id == s.OldAudit))
            .IpAddress.Should().BeNull();
        (await db.RefreshTokens.SingleAsync(x => x.Id == s.OldRefresh))
            .CreatedByIp.Should().BeNull();
        (await db.ShopperRefreshTokens.SingleAsync(x => x.Id == s.OldShopperRefresh))
            .CreatedByIp.Should().BeNull();

        var intake = await db.IntakeFormSubmissions.SingleAsync(x => x.Id == s.OldIntake);
        intake.IpAddress.Should().BeNull();
        intake.UserAgent.Should().BeNull();

        // Non-nullable sütunlar: null yerine boş string bekliyoruz.
        var pay = await db.PaymentSubmissionAudits.SingleAsync(x => x.Id == s.OldPaymentAudit);
        pay.IpAddress.Should().BeEmpty();
        pay.UserAgent.Should().BeEmpty();
    }

    [Fact]
    public async Task Anonymise_leaves_rows_inside_retention_window_untouched()
    {
        var s = await SeedAsync();
        await RunAsync();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();

        (await db.AuditLogs.SingleAsync(x => x.Id == s.FreshAudit))
            .IpAddress.Should().Be("203.0.113.7");
        (await db.RefreshTokens.SingleAsync(x => x.Id == s.FreshRefresh))
            .CreatedByIp.Should().Be("203.0.113.7");
        (await db.ShopperRefreshTokens.SingleAsync(x => x.Id == s.FreshShopperRefresh))
            .CreatedByIp.Should().Be("203.0.113.7");
        (await db.IntakeFormSubmissions.SingleAsync(x => x.Id == s.FreshIntake))
            .IpAddress.Should().Be("203.0.113.7");
        (await db.PaymentSubmissionAudits.SingleAsync(x => x.Id == s.FreshPaymentAudit))
            .IpAddress.Should().Be("203.0.113.7");
    }

    [Fact]
    public async Task Anonymise_keeps_the_rows_themselves()
    {
        var s = await SeedAsync();
        await RunAsync();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();

        // Taahhüt IP'yi silmek; satırı değil. Audit izi 24 ay yaşamalı,
        // mali kayıt hiç silinmemeli.
        (await db.AuditLogs.AnyAsync(x => x.Id == s.OldAudit)).Should().BeTrue();
        (await db.PaymentSubmissionAudits.AnyAsync(x => x.Id == s.OldPaymentAudit)).Should().BeTrue();
        (await db.IntakeFormSubmissions.AnyAsync(x => x.Id == s.OldIntake)).Should().BeTrue();
    }

    [Fact]
    public async Task Anonymise_is_disabled_when_retention_is_zero()
    {
        var s = await SeedAsync();
        await RunAsync(retentionDays: 0);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();

        (await db.AuditLogs.SingleAsync(x => x.Id == s.OldAudit))
            .IpAddress.Should().Be("203.0.113.7");
    }

    [Fact]
    public async Task Anonymise_is_idempotent_across_runs()
    {
        var s = await SeedAsync();
        await RunAsync();
        // İkinci tur: WHERE koşulu zaten boşaltılmışları dışladığı için
        // döngü hemen bitmeli, satırlar bozulmamalı.
        await RunAsync();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();

        (await db.AuditLogs.SingleAsync(x => x.Id == s.OldAudit)).IpAddress.Should().BeNull();
        (await db.AuditLogs.SingleAsync(x => x.Id == s.FreshAudit)).IpAddress.Should().Be("203.0.113.7");
    }

    [Fact]
    public async Task Anonymise_drains_everything_when_batch_is_smaller_than_the_backlog()
    {
        var s = await SeedAsync();
        await RunAsync(batchSize: 1);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();

        (await db.AuditLogs.SingleAsync(x => x.Id == s.OldAudit)).IpAddress.Should().BeNull();
        (await db.RefreshTokens.SingleAsync(x => x.Id == s.OldRefresh)).CreatedByIp.Should().BeNull();
    }
}
