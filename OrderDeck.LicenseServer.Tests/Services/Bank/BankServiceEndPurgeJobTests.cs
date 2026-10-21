using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Domain.Bank;
using OrderDeck.LicenseServer.Services.Bank;
using OrderDeck.LicenseServer.Services.Observability;
using OrderDeck.LicenseServer.Services.Privacy;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Bank;

/// <summary>KVKK K-6: hizmet sonu + 30 gün banka verisi YEREL olarak silinir (Obifin'e gidilmez); lisansı yenilenen silinmez;
/// lisansı biten önce durdurulur; yöneticiye özet gider; Obifin tarafı (Kaldır ve sil, yazılı silme teyidi) her gün hatırlatılır.</summary>
public sealed class BankServiceEndPurgeJobTests
{
    /// <summary>Her çağrıda patlar: otomatik iş Obifin'e HİÇ gitmemeli (gitseydi iş lisansı "DÜŞTÜ" sayar, test kırılır).</summary>
    private sealed class NoCallObifin : IObifinClient
    {
        private static Exception Fail() => new InvalidOperationException("Otomatik hizmet sonu işi Obifin'e gitmemeli.");
        public Task<IReadOnlyList<ObifinAccountDto>> ListAccountsAsync(ObifinCredentials c, CancellationToken ct = default) => throw Fail();
        public Task<IReadOnlyList<ObifinBankConnectionDto>> ListBankConnectionsAsync(ObifinCredentials c, CancellationToken ct = default) => throw Fail();
        public Task AddBankConnectionAsync(ObifinCredentials c, string b, IReadOnlyDictionary<string, string> f, CancellationToken ct = default) => throw Fail();
        public Task RemoveBankConnectionAsync(ObifinCredentials c, long id, CancellationToken ct = default) => throw Fail();
        public Task<ObifinPage<ObifinTransactionDto>> ListTransactionsAsync(ObifinCredentials c, DateOnly f, DateOnly t, long? s, int p, int ps, CancellationToken ct = default) => throw Fail();
    }

    /// <summary>İşin bağlamında bir lisansın hareketini silen İLK kayıttan hemen önce bir kez koşar: işin bağlantıları seçmesinden
    /// SONRA, sıradaki lisansa geçmeden önce yönetici sayfasının yazısını (Etkinleştir, Kaldır ve sil) sıraya dayalı kurar.</summary>
    private sealed class OnFirstTransactionDelete(Func<Guid, Task> action) : SaveChangesInterceptor
    {
        private bool _fired;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var deleted = eventData.Context!.ChangeTracker.Entries<BankTransaction>()
                .FirstOrDefault(e => e.State == EntityState.Deleted);
            if (!_fired && deleted is not null)
            {
                _fired = true;
                await action(deleted.Entity.LicenseId);
            }
            return result;
        }
    }

    /// <summary>Yalnız <see cref="License"/>'ın hareketini silen her kayıtta eşzamanlı bir eşleştirme yazısını taklit eder:
    /// sonlandırıcının yeniden denemesi de çakışır (<see cref="BankDeletionIncompleteException"/>).</summary>
    private sealed class ConflictFor : SaveChangesInterceptor
    {
        public Guid License { get; set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<BankTransaction>()
                .Any(e => e.State == EntityState.Deleted && e.Entity.LicenseId == License))
                throw new DbUpdateConcurrencyException("eşzamanlı eşleştirme yazısı (test)");
            return ValueTask.FromResult(result);
        }
    }

    /// <summary>Lisans yenilenmesinin (Unverified'a dönüş) kaydında patlar: işin ön adımlarından biri düşünce.</summary>
    private sealed class FailOnRevive : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<ObifinConnection>()
                .Any(e => e.State == EntityState.Modified && e.Entity.Status == ObifinConnectionStatus.Unverified))
                throw new InvalidOperationException("veritabanı yazılamadı (test)");
            return ValueTask.FromResult(result);
        }
    }

    private static LicenseDbContext NewDb(IInterceptor? interceptor = null)
    {
        var options = new DbContextOptionsBuilder<LicenseDbContext>().UseInMemoryDatabase($"bank-end-{Guid.NewGuid():N}");
        if (interceptor is not null) options.AddInterceptors(interceptor);
        return new LicenseDbContext(options.Options);
    }

    /// <summary>Kancalı bağlam + aynı InMemory veritabanına kancasız ikinci bağlam üreten fabrika (yönetici sayfasının bağlamı).</summary>
    private static (LicenseDbContext Db, Func<LicenseDbContext> Other) SharedDb(IInterceptor interceptor)
    {
        var name = $"bank-end-{Guid.NewGuid():N}";
        var root = new InMemoryDatabaseRoot();
        var db = new LicenseDbContext(new DbContextOptionsBuilder<LicenseDbContext>().UseInMemoryDatabase(name, root)
            .AddInterceptors(interceptor).Options);
        return (db, () => new LicenseDbContext(new DbContextOptionsBuilder<LicenseDbContext>().UseInMemoryDatabase(name, root)
            .Options));
    }

    /// <summary>Sonlandırıcı beş argümanla, bağdaştırıcı aynı bağlamı paylaşır (<see cref="BankConnectionTerminatorTests.Terminator"/>);
    /// Obifin istemcisi hiç çağrılmamalı.</summary>
    private static BankServiceEndPurgeJob Job(LicenseDbContext db, TestEmailSender email)
        => new(db, BankConnectionTerminatorTests.Terminator(db, new NoCallObifin()),
            new AdminJobAlerts(email, new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                [AdminJobAlerts.AlertEmailKey] = $"ops-{Guid.NewGuid():N}@example.test",
            }).Build(), NullLogger<AdminJobAlerts>.Instance),
            NullLogger<BankServiceEndPurgeJob>.Instance);

    /// <summary>Lisans + bağlantı (korunan alanlar sahte: iş kimlik çözmez, Obifin'e gitmez) + bir hareket + yerel banka bağlantısı
    /// (#501) + onaylı hesap 6 + onaysız hesap 9.</summary>
    private static async Task<Guid> SeedAsync(LicenseDbContext db, ObifinConnectionStatus status, DateTimeOffset? disabledAt,
        DateTimeOffset licenseExpiresAt, bool disabledByAdmin = false)
    {
        var customerId = Guid.NewGuid();
        db.Customers.Add(new Customer { Id = customerId, Email = $"e-{Guid.NewGuid():N}@x", Name = "E",
            PasswordHash = $"h-{Guid.NewGuid():N}", CreatedAt = DateTimeOffset.UtcNow });
        var lic = Guid.NewGuid();
        db.Licenses.Add(new License { Id = lic, LicenseKey = $"LDK-{Guid.NewGuid():N}", CustomerId = customerId, SkuCode = "STD",
            ActivationSlots = 1, IssuedAt = DateTimeOffset.UtcNow.AddYears(-1), ExpiresAt = licenseExpiresAt });
        var now = DateTimeOffset.UtcNow;
        var connId = Guid.NewGuid();
        db.ObifinConnections.Add(new ObifinConnection { Id = connId, LicenseId = lic, BaseUrl = "https://example.invalid",
            UserCode = $"u-{Guid.NewGuid():N}@x", PasswordProtected = $"p-{Guid.NewGuid():N}", ApiKeyProtected = $"a-{Guid.NewGuid():N}",
            Status = status, DisabledAt = disabledAt, DisabledByAdmin = disabledByAdmin,
            LastError = disabledByAdmin ? BankConnectionTerminator.StoppedByAdminMessage
                : status == ObifinConnectionStatus.Disabled ? ObifinConnectionService.LicenseEndedMessage : null,
            CreatedAt = now, UpdatedAt = now });
        db.BankConnections.Add(new BankConnection { Id = Guid.NewGuid(), LicenseId = lic, ObifinConnectionId = connId,
            BankaKodu = "qnb", BankaApiId = 501, Label = "QNB", Status = BankConnectionStatus.Active, CreatedAt = now });
        db.BankAccounts.AddRange(
            new BankAccount { Id = Guid.NewGuid(), LicenseId = lic, ObifinAccountId = 6, BankaKodu = "qnb",
                IbanMasked = "TR12…345", IbanHash = $"{Guid.NewGuid():N}{Guid.NewGuid():N}", NotificationNote = "bildirim",
                Currency = "TL", RefreshedAt = now, ApprovedAt = now, ApprovedBy = "yonetici-1" },
            new BankAccount { Id = Guid.NewGuid(), LicenseId = lic, ObifinAccountId = 9, BankaKodu = "garanti",
                IbanMasked = "?", Currency = "TL", RefreshedAt = now });
        db.BankTransactions.Add(new BankTransaction { Id = Guid.NewGuid(), LicenseId = lic, ObifinId = 1, ObifinAccountId = 6,
            BankaKodu = "qnb", Direction = BankTransactionDirection.Incoming, Amount = 10m, Currency = "TL", OccurredAt = now, FetchedAt = now });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return lic;
    }

    private static readonly DateTimeOffset Ended = DateTimeOffset.UtcNow.AddDays(-40);
    private static DateTimeOffset Active => DateTimeOffset.UtcNow.AddDays(30);

    [Fact]
    public void Hizmet_sonu_suresi_30_gun_sayfanin_sabiti_ona_bagli()
    {
        // Karar 1: hizmet sonu + 30 gün (öneri; avukat onaylamalı). Sayfanın "silinecek" tarihi ve "Durdur" bildirimi aynı sabitten.
        BankServiceEndPurgeJob.GracePeriod.Should().Be(TimeSpan.FromDays(30));
        OrderDeck.LicenseServer.Pages.Admin.Obifin.IndexModel.ServiceEndGraceDays
            .Should().Be((int)BankServiceEndPurgeJob.GracePeriod.TotalDays);
    }

    [Fact]
    public async Task Otuz_gunu_gecen_baglantinin_verisi_yerelde_silinir_Obifine_gidilmez_otuz_gun_icindeki_kalir()
    {
        using var db = NewDb(); var email = new TestEmailSender();
        var due = await SeedAsync(db, ObifinConnectionStatus.Disabled, DateTimeOffset.UtcNow.AddDays(-31), Ended);
        var waiting = await SeedAsync(db, ObifinConnectionStatus.Disabled, DateTimeOffset.UtcNow.AddDays(-29), Ended);

        var deleted = await Job(db, email).RunAsync(CancellationToken.None);

        deleted.Should().Be(1, "NoCallObifin'e dokunulsaydı lisans DÜŞTÜ sayılırdı");
        (await db.BankTransactions.AnyAsync(t => t.LicenseId == due)).Should().BeFalse();
        var conn = await db.ObifinConnections.AsNoTracking().SingleAsync(c => c.LicenseId == due);
        conn.BankDataPurgedAt.Should().NotBeNull("Obifin kimliği yönetici 'Kaldır ve sil' diyene kadar kalır");
        conn.Status.Should().Be(ObifinConnectionStatus.Disabled);
        (await db.BankConnections.AnyAsync(b => b.LicenseId == due && b.BankaApiId == 501)).Should().BeTrue();
        var accounts = await db.BankAccounts.AsNoTracking().Where(a => a.LicenseId == due).ToListAsync();
        accounts.Should().ContainSingle("onaylı hesap 'Kaldır ve sil'in sahiplik kanıtı olarak kalır, onaysız silinir")
            .Which.Should().Match<BankAccount>(a => a.ObifinAccountId == 6 && a.IbanMasked == "" && a.IbanHash == null
                && a.NotificationNote == null);
        (await db.BankTransactions.AnyAsync(t => t.LicenseId == waiting)).Should().BeTrue();
        (await db.DataDestructionRecords.AnyAsync(r => r.LicenseId == due && r.Trigger == DataDestruction.Triggers.ServiceEnd
            && r.Actor == DataDestruction.SystemActor)).Should().BeTrue();
        var mail = email.Sent.Should().ContainSingle().Subject;
        mail.Subject.Should().Be(BankServiceEndPurgeJob.DoneSubject);
        mail.PlainBody.Should().Contain(due.ToString()).And.Contain("Kaldır ve sil").And.Contain("YAZILI teyit")
            .And.NotContain(waiting.ToString());
    }

    [Fact]
    public async Task Basarili_kosu_silecek_baglanti_olmasa_da_kosu_kaydi_birakir_e_posta_gitmez()
    {
        // Görev 9 kuralı (iç imha prosedürü §4.2-3): haftalık "son başarılı çalışma" kontrolü bu satırdan yapılır.
        using var db = NewDb(); var email = new TestEmailSender();

        var deleted = await Job(db, email).RunAsync(CancellationToken.None);

        deleted.Should().Be(0);
        var run = (await db.DataDestructionRecords.AsNoTracking().ToListAsync()).Should().ContainSingle().Subject;
        (run.Target, run.Action, run.RowCount).Should().Be((BankServiceEndPurgeJob.JobId, DataDestruction.Actions.RunCompleted, 0));
        email.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task Islenmis_baglanti_ertesi_gun_yeniden_islenmez_yalniz_Kaldir_ve_sil_hatirlatmasi_gelir()
    {
        using var db = NewDb();
        await SeedAsync(db, ObifinConnectionStatus.Disabled, DateTimeOffset.UtcNow.AddDays(-31), Ended);
        var firstRun = new TestEmailSender();
        await Job(db, firstRun).RunAsync(CancellationToken.None);
        var secondRun = new TestEmailSender();

        var second = await Job(db, secondRun).RunAsync(CancellationToken.None);

        second.Should().Be(0);
        firstRun.Sent.Should().ContainSingle().Which.Subject.Should().Be(BankServiceEndPurgeJob.DoneSubject,
            "bu koşuda silinen 'yapıldı' e-postasında; hatırlatmaya girmez");
        secondRun.Sent.Should().ContainSingle().Which.Subject.Should().Be(BankServiceEndPurgeJob.ObifinPendingSubject);
    }

    [Fact]
    public async Task Kaldir_ve_sil_bekleyen_ve_teyidi_gelmeyen_lisanslar_tek_hatirlatmada_14_gunu_gecen_GECIKTI()
    {
        // KVKK Ö-9 öneri 3: otomatik iş Obifin'e gitmiyor (karar 12); Obifin'in çekmeyi ve saklamayı bırakması izlenmeli.
        using var db = NewDb(); var email = new TestEmailSender();
        var awaitingRemoval = await SeedAsync(db, ObifinConnectionStatus.Disabled, DateTimeOffset.UtcNow.AddDays(-35), Ended);
        (await db.ObifinConnections.SingleAsync(c => c.LicenseId == awaitingRemoval)).BankDataPurgedAt =
            DateTimeOffset.UtcNow.AddDays(-3);
        var awaitingConfirmation = Guid.NewGuid();   // bağlantısı ve lisansı silinmiş olabilir: kayıt FK'siz
        db.ObifinCopyDeletions.Add(new ObifinCopyDeletion { Id = Guid.NewGuid(), LicenseId = awaitingConfirmation,
            RequestedAt = DateTimeOffset.UtcNow.AddDays(-15), RequestedBy = "yonetici-1" });
        await db.SaveChangesAsync();

        var deleted = await Job(db, email).RunAsync(CancellationToken.None);

        deleted.Should().Be(0, "yerel verisi silinmiş bağlantı yeniden işlenmez");
        var mail = email.Sent.Should().ContainSingle().Subject;
        mail.Subject.Should().Be(BankServiceEndPurgeJob.ObifinOverdueSubject, "teyit isteği 14 günü geçti");
        mail.PlainBody.Should().Contain(awaitingRemoval.ToString()).And.Contain("Kaldır ve sil")
            .And.Contain(awaitingConfirmation.ToString()).And.Contain("yazılı silme teyidi").And.Contain("(GECİKTİ)");
        mail.PlainBody.Split('\n').Single(l => l.Contains(awaitingRemoval.ToString()))
            .Should().NotContain("(GECİKTİ)", "yerel silme 3 gün önce: 14 günün içinde");
    }

    [Fact]
    public async Task Teyit_bekleyen_14_gun_icindeyse_olagan_hatirlatma_gider_teyit_alininca_hatirlatma_durur()
    {
        using var db = NewDb(); var email = new TestEmailSender();
        var pending = new ObifinCopyDeletion { Id = Guid.NewGuid(), LicenseId = Guid.NewGuid(),
            RequestedAt = DateTimeOffset.UtcNow.AddDays(-2), RequestedBy = "yonetici-1" };
        db.ObifinCopyDeletions.Add(pending);
        await db.SaveChangesAsync();

        await Job(db, email).RunAsync(CancellationToken.None);
        (await db.ObifinCopyDeletions.SingleAsync()).ConfirmedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        await Job(db, email).RunAsync(CancellationToken.None);

        var mail = email.Sent.Should().ContainSingle("teyit alındıktan sonra hatırlatma gitmez").Subject;
        mail.Subject.Should().Be(BankServiceEndPurgeJob.ObifinPendingSubject);
        mail.PlainBody.Should().Contain(pending.LicenseId.ToString()).And.NotContain("(GECİKTİ)");
    }

    [Fact]
    public async Task Teyit_kaydinin_Obifindeki_elle_isi_hatirlatma_satirinda_her_gun_gelir()
    {
        // Görev 13 kuralı 3: "Kaldır ve sil" Obifin'e ulaşamadıysa ya da bağlantıyı kaldıramadıysa Obifin'de hâlâ çekim yapan
        // bağlantı vardır; Obifin kimliği silindiği için yönetici bunu yalnız teyit kaydından görür.
        using var db = NewDb(); var email = new TestEmailSender();
        var manual = $"kaldırılamayan banka bağlantıları #{Random.Shared.Next(100, 999)}: Obifin panelinden elle kaldırın";
        var withManual = new ObifinCopyDeletion { Id = Guid.NewGuid(), LicenseId = Guid.NewGuid(),
            RequestedAt = DateTimeOffset.UtcNow.AddDays(-1), RequestedBy = "yonetici-1", ManualAction = manual };
        var writtenOnly = new ObifinCopyDeletion { Id = Guid.NewGuid(), LicenseId = Guid.NewGuid(),
            RequestedAt = DateTimeOffset.UtcNow.AddDays(-1), RequestedBy = "yonetici-1" };
        db.ObifinCopyDeletions.AddRange(withManual, writtenOnly);
        await db.SaveChangesAsync();

        await Job(db, email).RunAsync(CancellationToken.None);

        var lines = email.Sent.Should().ContainSingle().Subject.PlainBody.Split('\n');
        lines.Single(l => l.Contains(withManual.LicenseId.ToString())).Should().Contain(manual);
        lines.Single(l => l.Contains(writtenOnly.LicenseId.ToString())).Should().NotContain("elle iş");
    }

    [Fact]
    public async Task Lisans_yenilenmisse_veri_silinmez_baglanti_Unverified_olur_yoneticiye_uyari_gider()
    {
        // KVKK K-6 + veri kaybı: lisans 30 gün içinde uzatıldı (Extend bağlantıya dokunmuyor). Ödeme yapan yayıncının verisi
        // silinmemeli; bağlantı yönetici Doğrula'ya basana dek Unverified bekler.
        using var db = NewDb(); var email = new TestEmailSender();
        var lic = await SeedAsync(db, ObifinConnectionStatus.Disabled, DateTimeOffset.UtcNow.AddDays(-31), licenseExpiresAt: Active);

        var deleted = await Job(db, email).RunAsync(CancellationToken.None);

        deleted.Should().Be(0);
        (await db.BankTransactions.AnyAsync(t => t.LicenseId == lic)).Should().BeTrue();
        var conn = await db.ObifinConnections.AsNoTracking().SingleAsync(c => c.LicenseId == lic);
        conn.Status.Should().Be(ObifinConnectionStatus.Unverified);
        (conn.DisabledAt, conn.BankDataPurgedAt).Should().Be(((DateTimeOffset?)null, (DateTimeOffset?)null));
        conn.LastError.Should().Be(ObifinConnectionService.LicenseRenewedMessage);
        var mail = email.Sent.Should().ContainSingle().Subject;
        mail.Subject.Should().Be(BankServiceEndPurgeJob.RenewedSubject);
        mail.PlainBody.Should().Contain(lic.ToString()).And.Contain("Doğrula");
    }

    [Fact]
    public async Task Yerel_silmeden_sonra_lisans_yenilenirse_baglanti_yine_acilir()
    {
        using var db = NewDb(); var email = new TestEmailSender();
        var lic = await SeedAsync(db, ObifinConnectionStatus.Disabled, DateTimeOffset.UtcNow.AddDays(-31), Ended);
        await Job(db, email).RunAsync(CancellationToken.None);
        (await db.Licenses.SingleAsync(l => l.Id == lic)).ExpiresAt = Active;
        await db.SaveChangesAsync();

        await Job(db, email).RunAsync(CancellationToken.None);

        var conn = await db.ObifinConnections.AsNoTracking().SingleAsync(c => c.LicenseId == lic);
        conn.Status.Should().Be(ObifinConnectionStatus.Unverified);
        conn.BankDataPurgedAt.Should().BeNull("yeniden toplanan veri sonraki hizmet sonunda yine silinebilsin");
    }

    [Fact]
    public async Task Lisans_yenilenince_yerel_verisi_silinmis_baglantinin_capasi_acilis_gunune_cekilir_silinmemisinki_kalir()
    {
        // Karar 28 (Görev 13 kuralı 2): lisansı 30 günden sonra yenilenen, kendiliğinden durdurulmuş bağlantının yerel verisi
        // silinmiş olabilir. Doğrula'dan sonraki ilk artımlı çekim imleçten sonraki 31 günü bir kerede çekerdi (durdurulmuş
        // dönemin hareketleri, silinmiş verinin yerine). Çapa açılış günü − 2'ye çekilir, imleç kalır. Silinmemişte aradaki
        // havaleler gelsin: çapa değişmez.
        using var db = NewDb(); var email = new TestEmailSender();
        var purged = await SeedAsync(db, ObifinConnectionStatus.Disabled, DateTimeOffset.UtcNow.AddDays(-45), Active);
        var paused = await SeedAsync(db, ObifinConnectionStatus.Disabled, DateTimeOffset.UtcNow.AddDays(-10), Active);
        var oldAnchor = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-60);
        foreach (var c in await db.ObifinConnections.ToListAsync())
        {
            c.InitialCollectFrom = oldAnchor; c.BackfillCompletedAt = DateTimeOffset.UtcNow.AddDays(-58);
            c.LastObifinTransactionId = 100;
            if (c.LicenseId == purged) c.BankDataPurgedAt = DateTimeOffset.UtcNow.AddDays(-15);
        }
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var before = BankDataCleanupJob.AnchorFor(DateTimeOffset.UtcNow);

        await Job(db, email).RunAsync(CancellationToken.None);

        var after = BankDataCleanupJob.AnchorFor(DateTimeOffset.UtcNow);
        var reopened = await db.ObifinConnections.AsNoTracking().SingleAsync(c => c.LicenseId == purged);
        reopened.Status.Should().Be(ObifinConnectionStatus.Unverified);
        reopened.InitialCollectFrom.Should().BeOneOf(new DateOnly?[] { before, after }, "açılış günü − InitialGraceDays");
        reopened.LastObifinTransactionId.Should().Be(100, "imleç kalır; pencere çapadan başlar");
        var resumed = await db.ObifinConnections.AsNoTracking().SingleAsync(c => c.LicenseId == paused);
        resumed.Status.Should().Be(ObifinConnectionStatus.Unverified);
        resumed.InitialCollectFrom.Should().Be(oldAnchor, "yerel verisi silinmemiş durdurmada aradaki havaleler gelsin");
    }

    [Fact]
    public async Task Yonetici_durdurduysa_lisans_gecerli_olsa_da_otuz_gunde_silinir_kendiliginden_acilmaz()
    {
        using var db = NewDb(); var email = new TestEmailSender();
        var lic = await SeedAsync(db, ObifinConnectionStatus.Disabled, DateTimeOffset.UtcNow.AddDays(-31),
            licenseExpiresAt: Active, disabledByAdmin: true);

        var deleted = await Job(db, email).RunAsync(CancellationToken.None);

        deleted.Should().Be(1);
        var conn = await db.ObifinConnections.AsNoTracking().SingleAsync(c => c.LicenseId == lic);
        conn.Status.Should().Be(ObifinConnectionStatus.Disabled);
        conn.DisabledByAdmin.Should().BeTrue();
        conn.BankDataPurgedAt.Should().NotBeNull();
        email.Sent.Select(m => m.Subject).Should().Equal(BankServiceEndPurgeJob.DoneSubject);
    }

    [Fact]
    public async Task Lisansi_biten_ama_hala_Verified_baglanti_bugun_durdurulur_silinmez()
    {
        using var db = NewDb(); var email = new TestEmailSender();
        var lic = await SeedAsync(db, ObifinConnectionStatus.Verified, null, Ended);

        var deleted = await Job(db, email).RunAsync(CancellationToken.None);

        deleted.Should().Be(0);
        var conn = await db.ObifinConnections.AsNoTracking().SingleAsync(c => c.LicenseId == lic);
        conn.Status.Should().Be(ObifinConnectionStatus.Disabled);
        conn.DisabledAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1), "30 günlük sayaç bugün başlar");
        conn.DisabledByAdmin.Should().BeFalse("lisans bitişi: yenilenince kendiliğinden açılır");
        email.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task Tarihsiz_Disabled_baglanti_bugun_damgalanir_yonetici_durdurmasi_sayilir_acilmaz_silinmez()
    {
        // P0 öncesi elle (SQL ile) Disabled yapılmış satırın DisabledAt'i yok: damgalanmasa hizmet sonu silmesine hiç girmezdi.
        // Kod bu durumu hiç yazmaz; yazan yöneticidir — lisans geçerli olsa da kendiliğinden açılmaz.
        using var db = NewDb(); var email = new TestEmailSender();
        var lic = await SeedAsync(db, ObifinConnectionStatus.Disabled, disabledAt: null, licenseExpiresAt: Active);

        var deleted = await Job(db, email).RunAsync(CancellationToken.None);

        deleted.Should().Be(0);
        (await db.BankTransactions.AnyAsync(t => t.LicenseId == lic)).Should().BeTrue();
        var conn = await db.ObifinConnections.AsNoTracking().SingleAsync(c => c.LicenseId == lic);
        conn.Status.Should().Be(ObifinConnectionStatus.Disabled);
        conn.DisabledByAdmin.Should().BeTrue();
        conn.DisabledAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1), "30 günlük sayaç bugün başlar");
        email.Sent.Should().BeEmpty("yenilenme e-postası gitmez: bağlantı açılmadı");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Is_sectikten_sonra_etkinlestirilen_ya_da_kaldirilan_baglanti_atlanir_DUSTU_sayilmaz(bool removedByAdmin)
    {
        // Görev 13 kuralı 1: yönetici sayfası obifin-poll kilidini almaz. İş bağlantıları seçtikten sonra yönetici birini
        // Etkinleştir'e basarsa sonlandırıcı onu silmez (NotStoppedMessage); "Kaldır ve sil"e basarsa bağlantı satırı yoktur
        // (NoConnectionMessage), veriyi yöneticinin yolu sildi. İkisi de iş hatası değildir: "atlandı" notu, Hangfire Failed
        // olmaz, "DÜŞTÜ" e-postası gitmez, koşu kaydı yazılır.
        Guid a = default, b = default;
        Func<LicenseDbContext> other = null!;
        var hook = new OnFirstTransactionDelete(async first =>
        {
            var target = first == a ? b : a;
            using var admin = other();
            var terminator = BankConnectionTerminatorTests.Terminator(admin, new NoCallObifin());
            if (removedByAdmin)
                // Korunan alanlar sahte: kimlik çözülmez, Obifin'e gidilmez ("ulaşılamadı"), yerel silme yine yapılır.
                await terminator.RemoveAndDeleteAsync(target, DataDestruction.Triggers.AdminRemove, "yonetici-1",
                    CancellationToken.None);
            else
                await terminator.ResumeAsync(target, CancellationToken.None);
        });
        (var db, other) = SharedDb(hook);
        using var _ = db;
        var email = new TestEmailSender();
        a = await SeedAsync(db, ObifinConnectionStatus.Disabled, DateTimeOffset.UtcNow.AddDays(-31), Active, disabledByAdmin: true);
        b = await SeedAsync(db, ObifinConnectionStatus.Disabled, DateTimeOffset.UtcNow.AddDays(-31), Active, disabledByAdmin: true);

        var deleted = await Job(db, email).RunAsync(CancellationToken.None);

        deleted.Should().Be(1);
        var purged = (await db.ObifinConnections.AsNoTracking().Where(c => c.BankDataPurgedAt != null).ToListAsync())
            .Should().ContainSingle().Subject.LicenseId;
        var skipped = purged == a ? b : a;
        if (removedByAdmin)
        {
            (await db.ObifinConnections.AnyAsync(c => c.LicenseId == skipped)).Should().BeFalse();
            (await db.ObifinCopyDeletions.AnyAsync(d => d.LicenseId == skipped)).Should().BeTrue();
        }
        else
        {
            var conn = await db.ObifinConnections.AsNoTracking().SingleAsync(c => c.LicenseId == skipped);
            conn.Status.Should().Be(ObifinConnectionStatus.Unverified);
            (await db.BankTransactions.AnyAsync(t => t.LicenseId == skipped)).Should().BeTrue("etkinleştirilen bağlantı silinmez");
        }
        var mail = email.Sent.Should().ContainSingle().Subject;
        mail.Subject.Should().Be(BankServiceEndPurgeJob.DoneSubject);
        mail.PlainBody.Split('\n').Single(l => l.Contains(skipped.ToString())).Should().Contain("atlandı");
        mail.PlainBody.Should().NotContain("DÜŞTÜ");
        (await db.DataDestructionRecords.AnyAsync(r => r.Target == BankServiceEndPurgeJob.JobId
            && r.Action == DataDestruction.Actions.RunCompleted)).Should().BeTrue();
    }

    [Fact]
    public async Task Bir_lisansin_silmesi_duserse_siradaki_silinir_DUSTU_e_postasi_gider_is_Failed_kosu_kaydi_yazilmaz()
    {
        // Görev 13 kuralı 4: yerel silmenin ikinci jeton çakışması BankDeletionIncompleteException'dır; iş bunu düşme sayar,
        // ertesi gün kalanı siler. Bir lisansın hatası sıradakini bekletmez.
        var conflict = new ConflictFor();
        using var db = NewDb(conflict);
        var email = new TestEmailSender();
        var ok = await SeedAsync(db, ObifinConnectionStatus.Disabled, DateTimeOffset.UtcNow.AddDays(-31), Ended);
        var failing = await SeedAsync(db, ObifinConnectionStatus.Disabled, DateTimeOffset.UtcNow.AddDays(-31), Ended);
        conflict.License = failing;

        var act = () => Job(db, email).RunAsync(CancellationToken.None);

        await act.Should().ThrowAsync<AggregateException>();
        (await db.BankTransactions.AnyAsync(t => t.LicenseId == ok)).Should().BeFalse("sıradaki lisans silindi");
        (await db.BankTransactions.AnyAsync(t => t.LicenseId == failing)).Should().BeTrue();
        (await db.ObifinConnections.AsNoTracking().SingleAsync(c => c.LicenseId == failing)).BankDataPurgedAt
            .Should().BeNull("ertesi gün yeniden seçilsin");
        var mail = email.Sent.Should().ContainSingle().Subject;
        mail.Subject.Should().Be(BankServiceEndPurgeJob.FailedSubject);
        mail.PlainBody.Split('\n').Single(l => l.Contains(failing.ToString()))
            .Should().Contain("DÜŞTÜ").And.Contain(nameof(BankDeletionIncompleteException));
        (await db.DataDestructionRecords.AnyAsync(r => r.Action == DataDestruction.Actions.RunCompleted))
            .Should().BeFalse("düşen koşu 'son başarılı çalışma' sayılmaz");
    }

    [Fact]
    public async Task On_adimlardan_biri_duserse_yoneticiye_e_posta_gider_is_Failed()
    {
        // KVKK Ö-13 (banka saklama ve denetim budaması işlerinin kalıbı): imha işi düşerse yönetici e-postayla öğrenir; yalnız
        // Hangfire panosunda kalmaz.
        using var db = NewDb(new FailOnRevive()); var email = new TestEmailSender();
        await SeedAsync(db, ObifinConnectionStatus.Disabled, DateTimeOffset.UtcNow.AddDays(-31), licenseExpiresAt: Active);

        var act = () => Job(db, email).RunAsync(CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        var mail = email.Sent.Should().ContainSingle().Subject;
        mail.Subject.Should().Be(BankServiceEndPurgeJob.FailedSubject);
        mail.PlainBody.Should().Contain(BankServiceEndPurgeJob.JobId).And.Contain(nameof(InvalidOperationException));
    }
}
