using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Domain.Bank;
using OrderDeck.LicenseServer.Services.Audit;
using OrderDeck.LicenseServer.Services.Observability;
using OrderDeck.LicenseServer.Services.Privacy;
using OrderDeck.LicenseServer.Services.Shoppers;
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
        ParserRawText = $"dekont-metni-{Guid.NewGuid():N}",
    };

    private static SecurityDataRetentionJob Make(
        LicenseDbContext db, int retentionDays = 90, int batchSize = 1000, int deletionRequestPhoneDays = 30,
        TestEmailSender? email = null, int submissionRawTextDays = 30)
        => new(db, Options.Create(new SecurityDataRetentionOptions
        {
            RetentionDays = retentionDays,
            BatchSize = batchSize,
            DeletionRequestPhoneDays = deletionRequestPhoneDays,
            SubmissionRawTextDays = submissionRawTextDays,
        }), new AdminJobAlerts(email ?? new TestEmailSender(), new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [AdminJobAlerts.AlertEmailKey] = $"ops-{Guid.NewGuid():N}@example.test",
            }).Build(), NullLogger<AdminJobAlerts>.Instance), NullLogger<SecurityDataRetentionJob>.Instance);

    private async Task RunAsync(int retentionDays = 90, int batchSize = 1000, int deletionRequestPhoneDays = 30)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        await Make(db, retentionDays, batchSize, deletionRequestPhoneDays).AnonymiseAsync(CancellationToken.None);
    }

    /// <summary>Kurulan her kaydı düşürür (yalnız <see cref="Armed"/> iken): DB kesintisi benzetimi.</summary>
    private sealed class FailingSaveInterceptor : SaveChangesInterceptor
    {
        public bool Armed { get; set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
            => Armed ? throw new DbUpdateException("DB kesintisi benzetimi") : ValueTask.FromResult(result);
    }

    /// <summary>Paylaşılan fabrika veritabanı değil, kendi InMemory veritabanı: imha kayıtları tam sayılır.</summary>
    private static LicenseDbContext OwnDb(IInterceptor? interceptor = null)
    {
        var options = new DbContextOptionsBuilder<LicenseDbContext>().UseInMemoryDatabase($"security-ret-{Guid.NewGuid():N}");
        if (interceptor is not null) options.AddInterceptors(interceptor);
        return new LicenseDbContext(options.Options);
    }

    private static ShopperDeletionRequest DeletionRequest(DateTimeOffset? handledAt) => new()
    {
        Id = Guid.NewGuid(), ShopperId = Guid.NewGuid(), PhoneAtRequest = TestPhone.NewE164(),
        RequestedAt = DateTimeOffset.UtcNow.AddDays(-60), HandledAt = handledAt, HandledBy = handledAt is null ? null : "admin",
    };

    private static BankSubjectRequest BankRequest(DateTimeOffset? closedAt) => new()
    {
        Id = Guid.NewGuid(), LicenseId = Guid.NewGuid(), Kind = BankSubjectRequestKind.Access,
        IbanHash = closedAt is null ? $"{Guid.NewGuid():N}{Guid.NewGuid():N}" : null, IbanMasked = "TR12…345",
        ReceivedAt = DateTimeOffset.UtcNow.AddDays(-60), DueAt = DateTimeOffset.UtcNow.AddDays(-30),
        CreatedAt = DateTimeOffset.UtcNow.AddDays(-60), CreatedBy = "admin", ClosedAt = closedAt,
        ClosedBy = closedAt is null ? null : "admin",
        Resolution = closedAt is null ? BankSubjectResolution.None : BankSubjectResolution.Answered,
    };

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

    [Fact]
    public async Task Islenmis_silme_talebindeki_telefon_30_gun_sonra_bosaltilir_talep_kalir()
    {
        // KVKK D-5: talebin kaydı kanıt olarak kalır; işlendikten sonra telefona ihtiyaç yok.
        var old = DeletionRequest(DateTimeOffset.UtcNow.AddDays(-31));
        var recent = DeletionRequest(DateTimeOffset.UtcNow.AddDays(-29));
        var pending = DeletionRequest(null);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            db.ShopperDeletionRequests.AddRange(old, recent, pending);
            await db.SaveChangesAsync();
        }

        await RunAsync();

        using var verify = _factory.Services.CreateScope();
        var vdb = verify.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var rows = await vdb.ShopperDeletionRequests.AsNoTracking()
            .Where(r => r.Id == old.Id || r.Id == recent.Id || r.Id == pending.Id).ToDictionaryAsync(r => r.Id);
        rows[old.Id].PhoneAtRequest.Should().BeEmpty();
        rows[old.Id].HandledAt.Should().NotBeNull("talebin kaydı kanıt olarak kalır");
        rows[old.Id].HandledBy.Should().Be("admin");
        rows[recent.Id].PhoneAtRequest.Should().Be(recent.PhoneAtRequest);
        rows[pending.Id].PhoneAtRequest.Should().Be(pending.PhoneAtRequest, "bekleyen talepte yönetici kimin talebine baktığını görür");
    }

    [Fact]
    public async Task Kapanmis_banka_veri_talebinin_kisaltilmis_IBANi_30_gun_sonra_bosaltilir_acik_talebe_dokunulmaz()
    {
        // KVKK Ö-11: özet kapanışta boşaldı; kısaltılmış IBAN da talebin belgesi için gereğinden uzun kalmasın.
        var old = BankRequest(DateTimeOffset.UtcNow.AddDays(-31));
        var recent = BankRequest(DateTimeOffset.UtcNow.AddDays(-29));
        var open = BankRequest(null);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            db.BankSubjectRequests.AddRange(old, recent, open);
            await db.SaveChangesAsync();
        }

        await RunAsync();

        using var verify = _factory.Services.CreateScope();
        var vdb = verify.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var rows = await vdb.BankSubjectRequests.AsNoTracking()
            .Where(r => r.Id == old.Id || r.Id == recent.Id || r.Id == open.Id).ToDictionaryAsync(r => r.Id);
        rows[old.Id].IbanMasked.Should().BeEmpty();
        rows[old.Id].ClosedAt.Should().Be(old.ClosedAt, "talebin kaydı kalır; kapanış anı eşzamanlılık jetonudur, değişmez");
        rows[old.Id].Resolution.Should().Be(BankSubjectResolution.Answered);
        rows[recent.Id].IbanMasked.Should().Be("TR12…345");
        rows[open.Id].IbanMasked.Should().Be("TR12…345");
        rows[open.Id].IbanHash.Should().Be(open.IbanHash);
    }

    [Fact]
    public async Task Her_bosaltma_kisisel_veri_icermeyen_imha_kaydi_yazar_basarili_kosu_kosu_kaydi_birakir()
    {
        // KVKK Ö-13 (Görev 9 kuralı): her imha kayıt bırakır; haftalık "son başarılı çalışma" kontrolü koşu kaydından okunur.
        // Hedefler çoğul DbSet adıyla: aynı sütunun imhası (ör. purge'ün ödeme denetimi parmak izi) tek hedef adıyla gruplanır.
        using var db = OwnDb();
        db.ShopperDeletionRequests.Add(DeletionRequest(DateTimeOffset.UtcNow.AddDays(-31)));
        db.BankSubjectRequests.Add(BankRequest(DateTimeOffset.UtcNow.AddDays(-31)));
        db.AuditLogs.Add(NewAudit(Guid.NewGuid(), Old));
        db.RefreshTokens.Add(NewRefresh(Guid.NewGuid(), Old));
        db.ShopperRefreshTokens.Add(NewShopperRefresh(Guid.NewGuid(), Old));
        db.IntakeFormSubmissions.Add(NewIntake(Guid.NewGuid(), Old));
        db.PaymentSubmissionAudits.Add(NewPaymentAudit(Guid.NewGuid(), Old));
        await db.SaveChangesAsync();

        await Make(db).AnonymiseAsync(CancellationToken.None);
        // İkinci koşu: boşaltılmış satır koşula girmez, yeniden sayılmaz; yalnız koşu kaydı eklenir.
        await Make(db).AnonymiseAsync(CancellationToken.None);

        var records = await db.DataDestructionRecords.AsNoTracking().ToListAsync();
        records.Where(r => r.Action == DataDestruction.Actions.Nulled).Select(r => r.Target).Should().BeEquivalentTo(
            "ShopperDeletionRequests.PhoneAtRequest", "BankSubjectRequests.IbanMasked", "AuditLogs.IpAddress",
            "RefreshTokens.CreatedByIp", "ShopperRefreshTokens.CreatedByIp", "IntakeFormSubmissions.IpAddress+UserAgent",
            ShopperPurgeService.SubmissionAuditFingerprintTarget, ShopperPurgeService.SubmissionAuditRawTextTarget);
        records.Where(r => r.Action == DataDestruction.Actions.Nulled).Should().OnlyContain(r => r.RowCount == 1
            && r.Trigger == DataDestruction.Triggers.Retention && r.Actor == DataDestruction.SystemActor
            && r.LicenseId == null && r.SubjectId == null && r.RequestId == null);
        records.Single(r => r.Target == "ShopperDeletionRequests.PhoneAtRequest").Criterion.Should().Contain("HandledAt+30g");
        records.Single(r => r.Target == "BankSubjectRequests.IbanMasked").Criterion.Should().Contain("ClosedAt+30g");
        records.Single(r => r.Target == ShopperPurgeService.SubmissionAuditRawTextTarget).Criterion.Should().Be("CreatedAt+30g");
        records.Where(r => r.Action == DataDestruction.Actions.RunCompleted).Should().HaveCount(2)
            .And.OnlyContain(r => r.Target == SecurityDataRetentionJob.JobId && r.RowCount == 0
                && r.Trigger == DataDestruction.Triggers.Retention && r.Actor == DataDestruction.SystemActor);
        records.Should().HaveCount(10);
    }

    [Fact]
    public async Task Telefon_ve_maske_bosaltmasi_IP_suresi_kapaliyken_de_kosar()
    {
        // Silme talebi telefonu kendi süresiyle işler: RetentionDays=0 (IP/UA kapalı) onu da durdurmasın.
        using var db = OwnDb();
        var phone = DeletionRequest(DateTimeOffset.UtcNow.AddDays(-31));
        var bank = BankRequest(DateTimeOffset.UtcNow.AddDays(-31));
        var audit = NewAudit(Guid.NewGuid(), Old);
        db.ShopperDeletionRequests.Add(phone);
        db.BankSubjectRequests.Add(bank);
        db.AuditLogs.Add(audit);
        await db.SaveChangesAsync();

        await Make(db, retentionDays: 0).AnonymiseAsync(CancellationToken.None);

        (await db.ShopperDeletionRequests.AsNoTracking().SingleAsync(r => r.Id == phone.Id)).PhoneAtRequest.Should().BeEmpty();
        (await db.BankSubjectRequests.AsNoTracking().SingleAsync(r => r.Id == bank.Id)).IbanMasked.Should().BeEmpty();
        (await db.AuditLogs.AsNoTracking().SingleAsync(a => a.Id == audit.Id)).IpAddress.Should().Be("203.0.113.7");
        (await db.DataDestructionRecords.AsNoTracking().CountAsync(r => r.Action == DataDestruction.Actions.RunCompleted
            && r.Target == SecurityDataRetentionJob.JobId)).Should().Be(1);
    }

    [Fact]
    public async Task Dekont_tam_metni_30_gun_sonra_bosaltilir_parmak_izinin_suresinden_bagimsiz_satir_kalir()
    {
        // KVKK K22: gönderim kaydındaki dekont tam metni (gönderen adı, açık IBAN'lar) süresiz kalıyordu. Süresi parmak izinin
        // 90 gününden ayrı (avukat kararı); IP/UA süresi kapalıyken de işler. Metni zaten boş satır yeniden sayılmaz.
        using var db = OwnDb();
        var old = NewPaymentAudit(Guid.NewGuid(), DateTimeOffset.UtcNow.AddDays(-31));
        var recent = NewPaymentAudit(Guid.NewGuid(), DateTimeOffset.UtcNow.AddDays(-29));
        var rejected = NewPaymentAudit(Guid.NewGuid(), DateTimeOffset.UtcNow.AddDays(-31));
        rejected.ParserRawText = null;
        db.PaymentSubmissionAudits.AddRange(old, recent, rejected);
        await db.SaveChangesAsync();

        await Make(db, retentionDays: 0).AnonymiseAsync(CancellationToken.None);

        var rows = await db.PaymentSubmissionAudits.AsNoTracking().ToDictionaryAsync(a => a.Id);
        rows.Should().HaveCount(3, "satır mali iz olarak kalır");
        rows[old.Id].ParserRawText.Should().BeNull();
        rows[old.Id].IpAddress.Should().Be("203.0.113.7", "parmak izinin süresi ayrı (burada kapalı)");
        rows[old.Id].FraudFlags.Should().Be(old.FraudFlags);
        rows[recent.Id].ParserRawText.Should().Be(recent.ParserRawText);
        var record = (await db.DataDestructionRecords.AsNoTracking().ToListAsync()).Should()
            .ContainSingle(r => r.Target == ShopperPurgeService.SubmissionAuditRawTextTarget).Subject;
        (record.RowCount, record.Action, record.Trigger, record.Criterion).Should().Be(
            (1, DataDestruction.Actions.Nulled, DataDestruction.Triggers.Retention, "CreatedAt+30g"));

        // Süre kapalı: metin kalır.
        using var other = OwnDb();
        var kept = NewPaymentAudit(Guid.NewGuid(), DateTimeOffset.UtcNow.AddDays(-400));
        other.PaymentSubmissionAudits.Add(kept);
        await other.SaveChangesAsync();
        await Make(other, submissionRawTextDays: 0).AnonymiseAsync(CancellationToken.None);
        (await other.PaymentSubmissionAudits.AsNoTracking().SingleAsync(a => a.Id == kept.Id)).ParserRawText
            .Should().Be(kept.ParserRawText);
    }

    [Fact]
    public async Task Telefon_suresi_kapaliysa_telefon_ve_maske_kalir_hepsi_kapaliysa_is_kosmaz()
    {
        using var db = OwnDb();
        var phone = DeletionRequest(DateTimeOffset.UtcNow.AddDays(-31));
        var bank = BankRequest(DateTimeOffset.UtcNow.AddDays(-31));
        db.ShopperDeletionRequests.Add(phone);
        db.BankSubjectRequests.Add(bank);
        await db.SaveChangesAsync();

        await Make(db, deletionRequestPhoneDays: 0).AnonymiseAsync(CancellationToken.None);
        (await db.ShopperDeletionRequests.AsNoTracking().SingleAsync(r => r.Id == phone.Id)).PhoneAtRequest
            .Should().Be(phone.PhoneAtRequest);
        (await db.BankSubjectRequests.AsNoTracking().SingleAsync(r => r.Id == bank.Id)).IbanMasked.Should().Be("TR12…345");
        (await db.DataDestructionRecords.AsNoTracking().CountAsync()).Should().Be(1, "IP/UA açık: koşu kaydı yine yazılır");

        await Make(db, retentionDays: 0, deletionRequestPhoneDays: 0).AnonymiseAsync(CancellationToken.None);
        (await db.DataDestructionRecords.AsNoTracking().CountAsync()).Should().Be(2, "dekont metni süresi açık: koşu kaydı yazılır");

        await Make(db, retentionDays: 0, deletionRequestPhoneDays: 0, submissionRawTextDays: 0)
            .AnonymiseAsync(CancellationToken.None);
        (await db.DataDestructionRecords.AsNoTracking().CountAsync()).Should().Be(2, "iş kapalı: koşu kaydı da yazılmaz");
    }

    [Fact]
    public async Task Bosaltma_duserse_yoneticiye_e_posta_gider_hata_yukari_cikar_kosu_kaydi_yazilmaz()
    {
        // İç imha prosedürü §4.3 (KVKK Ö-13): periyodik imha düşerse yönetici e-postayla uyarılır; iş Failed kalır.
        var failing = new FailingSaveInterceptor(); var email = new TestEmailSender();
        using var db = OwnDb(failing);
        db.ShopperDeletionRequests.Add(DeletionRequest(DateTimeOffset.UtcNow.AddDays(-31)));
        await db.SaveChangesAsync();
        failing.Armed = true;

        var act = () => Make(db, email: email).AnonymiseAsync(CancellationToken.None);

        await act.Should().ThrowAsync<DbUpdateException>();
        var sent = email.Sent.Should().ContainSingle().Subject;
        sent.Subject.Should().Be(SecurityDataRetentionJob.FailureSubject);
        sent.PlainBody.Should().Contain(SecurityDataRetentionJob.JobId).And.Contain(nameof(DbUpdateException));
        failing.Armed = false;
        (await db.DataDestructionRecords.AsNoTracking().CountAsync()).Should().Be(0);
    }
}
