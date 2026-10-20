using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain.Bank;
using OrderDeck.LicenseServer.Services.Bank;
using OrderDeck.LicenseServer.Services.Observability;
using OrderDeck.LicenseServer.Services.Privacy;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Bank;

/// <summary>Banka verisi saklama tablosu (KVKK K-6, Ö-14, karar 5): süreler işlem tarihinden, çekimden en az 7 gün; her imha
/// kalıcı kayıt yazar; iş düşerse e-posta. Gerçek SQL Server'daki çevirisi <see cref="BankDataRetentionRelationalTests"/>.</summary>
public sealed class BankDataRetentionJobTests
{
    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);

    /// <summary>Her SaveChanges'te kaç satırın güncellendiğini kaydeder: parti sınırı kanıtı.</summary>
    private sealed class ModifiedCountInterceptor : SaveChangesInterceptor
    {
        public List<int> ModifiedPerSave { get; } = new();

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            ModifiedPerSave.Add(eventData.Context!.ChangeTracker.Entries().Count(e => e.State == EntityState.Modified));
            return ValueTask.FromResult(result);
        }
    }

    /// <summary>Kurulan her kaydı düşürür (yalnız <see cref="Armed"/> iken): DB kesintisi benzetimi.</summary>
    private sealed class FailingSaveInterceptor : SaveChangesInterceptor
    {
        public bool Armed { get; set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
            => Armed ? throw new DbUpdateException("DB kesintisi benzetimi") : ValueTask.FromResult(result);
    }

    private static LicenseDbContext NewDb(IInterceptor? interceptor = null)
    {
        var options = new DbContextOptionsBuilder<LicenseDbContext>().UseInMemoryDatabase($"bank-ret-{Guid.NewGuid():N}");
        if (interceptor is not null) options.AddInterceptors(interceptor);
        return new LicenseDbContext(options.Options);
    }

    private static AdminJobAlerts Alerts(TestEmailSender email)
        => new(email, new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [AdminJobAlerts.AlertEmailKey] = $"ops-{Guid.NewGuid():N}@example.test",
        }).Build(), NullLogger<AdminJobAlerts>.Instance);

    private static BankDataRetentionJob Job(LicenseDbContext db, TestEmailSender? email = null)
        => new(db, Options.Create(new BankOptions { HashKey = new string('k', 32) }), Alerts(email ?? new TestEmailSender()),
            NullLogger<BankDataRetentionJob>.Instance);

    /// <summary>İşlem tarihi <paramref name="age"/> önce; çekim aynı an ya da <paramref name="fetchedAge"/> önce.</summary>
    private static BankTransaction Row(TimeSpan age, TimeSpan? fetchedAge = null) => new()
    {
        Id = Guid.NewGuid(), LicenseId = Guid.NewGuid(), ObifinId = Random.Shared.NextInt64(1, 1_000_000), ObifinAccountId = 1,
        BankaKodu = "qnb", Direction = BankTransactionDirection.Incoming, Amount = 10m, Currency = "TL",
        OccurredAt = DateTimeOffset.UtcNow - age, FetchedAt = DateTimeOffset.UtcNow - (fetchedAge ?? age),
        Description = "aciklama", CounterpartyName = "ad", RawJson = "{}",
    };

    private static BankTransaction Row(int ageDays) => Row(TimeSpan.FromDays(ageDays));

    /// <summary><paramref name="linked"/>: insan kararıyla bir müşteriye bağlı (gerçek müşteri dolu).</summary>
    private static PaymentMatch MatchFor(BankTransaction tx, bool linked, string? evidence = "kanıt metni") => new()
    {
        Id = Guid.NewGuid(), LicenseId = tx.LicenseId, BankTransactionId = tx.Id, Evidence = evidence,
        ActualWpfCustomerId = linked ? Guid.NewGuid() : null, DecidedAt = linked ? DateTimeOffset.UtcNow : null,
        Layer = PaymentMatchLayer.NameAmount, Status = linked ? PaymentMatchStatus.ManualOnly : PaymentMatchStatus.Proposed,
        CreatedAt = tx.FetchedAt, UpdatedAt = tx.FetchedAt,
    };

    private static CustomerIbanMemory Memory(DateTimeOffset createdAt, DateTimeOffset? lastConfirmedAt) => new()
    {
        Id = Guid.NewGuid(), LicenseId = Guid.NewGuid(), WpfCustomerId = Guid.NewGuid(), IbanHash = $"{Guid.NewGuid():N}{Guid.NewGuid():N}",
        IbanMasked = "TR12…345", LearnedFrom = IbanMemorySource.HumanApproval, CreatedAt = createdAt, LastConfirmedAt = lastConfirmedAt,
    };

    [Fact]
    public async Task Ham_kayit_islem_tarihinden_30_gun_sonra_bosaltilir()
    {
        using var db = NewDb();
        var inside = Row(TimeSpan.FromDays(30) - Minute); var outside = Row(TimeSpan.FromDays(30) + Minute);
        db.BankTransactions.AddRange(inside, outside);
        db.PaymentMatches.AddRange(MatchFor(inside, linked: true), MatchFor(outside, linked: true));
        await db.SaveChangesAsync();

        await Job(db).RunAsync(CancellationToken.None);

        (await db.BankTransactions.FindAsync(inside.Id))!.RawJson.Should().NotBeNull("30 günün bir dakika içinde");
        var o = (await db.BankTransactions.FindAsync(outside.Id))!;
        o.RawJson.Should().BeNull("30 günü bir dakika geçti");
        o.Description.Should().Be("aciklama", "eşleşen havalenin açıklaması 180 güne kadar kalır");
    }

    [Fact]
    public async Task Eslesmeyen_havalenin_aciklamasi_ve_adi_30_gunde_eslesenin_kalir()
    {
        // KVKK K-4 öneri 2: müşteri olmayan gönderenin (pazaryeri, iade, yakın) açıklaması kısa sürede silinir.
        using var db = NewDb();
        var unmatched = Row(TimeSpan.FromDays(30) + Minute);
        var proposedOnly = Row(TimeSpan.FromDays(30) + Minute);
        var linked = Row(TimeSpan.FromDays(30) + Minute);
        var young = Row(TimeSpan.FromDays(30) - Minute);
        db.BankTransactions.AddRange(unmatched, proposedOnly, linked, young);
        db.PaymentMatches.AddRange(MatchFor(proposedOnly, linked: false), MatchFor(linked, linked: true));
        await db.SaveChangesAsync();

        var r = await Job(db).RunAsync(CancellationToken.None);

        r.UnmatchedDescriptionsPurged.Should().Be(2, "öneri eşleşme değildir");
        foreach (var id in new[] { unmatched.Id, proposedOnly.Id })
        {
            var t = (await db.BankTransactions.FindAsync(id))!;
            t.Description.Should().BeNull(); t.CounterpartyName.Should().BeNull(); t.DescriptionPurgedAt.Should().NotBeNull();
            t.Amount.Should().Be(10m, "tutar ve tarih 180 güne kadar kalır");
        }
        (await db.BankTransactions.FindAsync(linked.Id))!.Description.Should().Be("aciklama");
        (await db.BankTransactions.FindAsync(young.Id))!.Description.Should().Be("aciklama");
    }

    [Fact]
    public async Task Gerekce_metni_hareketin_aciklamasiyla_birlikte_gider_satirin_olusturulma_anina_bakilmaz()
    {
        // KVKK Ö-14: gerekçe metni bağlı hareketin tarihine bağlanır. Tarama ya da elle eşleme satırı geç oluşturduysa metin
        // daha geç silinmez; eski bir satırın genç hareketteki gerekçesi de satırın yaşı yüzünden erken gitmez.
        using var db = NewDb();
        var old = Row(TimeSpan.FromDays(30) + Minute); var young = Row(TimeSpan.FromDays(10));
        var oldMatch = MatchFor(old, linked: false); var youngMatch = MatchFor(young, linked: false);
        oldMatch.CreatedAt = DateTimeOffset.UtcNow;
        youngMatch.CreatedAt = DateTimeOffset.UtcNow.AddDays(-200);
        db.BankTransactions.AddRange(old, young);
        db.PaymentMatches.AddRange(oldMatch, youngMatch);
        await db.SaveChangesAsync();

        var r = await Job(db).RunAsync(CancellationToken.None);

        r.EvidencePurged.Should().Be(1);
        var o = (await db.PaymentMatches.FindAsync(oldMatch.Id))!;
        o.Evidence.Should().BeNull("hareketin açıklaması 30 günde gitti, satır dün oluşturulmuş olsa da");
        o.Status.Should().Be(PaymentMatchStatus.Proposed, "yalnız gerekçe metni boşaltılır, satır kalır");
        o.UpdatedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1), "her yazan eşzamanlılık jetonunu ilerletir");
        (await db.PaymentMatches.FindAsync(youngMatch.Id))!.Evidence.Should().Be("kanıt metni", "hareketin açıklaması duruyor");
    }

    [Fact]
    public async Task Yalniz_dekont_bagi_olan_hareket_eslesen_sayilir_aciklamasi_ve_gerekcesi_kalir()
    {
        // "Eşleşen" = gerçek müşteri YA DA dekont bağı (PaymentId). Gap çözümüyle dekonta bağlanmış ama gerçek müşterisi boş satır
        // 30 günde açıklamasını kaybetmemeli; "eşleşmeyen" sorgusunun ikinci koşulu başka hiçbir testte sınanmıyor.
        using var db = NewDb();
        var tx = Row(TimeSpan.FromDays(30) + Minute);
        var match = MatchFor(tx, linked: false);
        match.PaymentId = Guid.NewGuid();
        match.Status = PaymentMatchStatus.ConfirmedByHuman;
        db.BankTransactions.Add(tx);
        db.PaymentMatches.Add(match);
        await db.SaveChangesAsync();

        var r = await Job(db).RunAsync(CancellationToken.None);

        r.UnmatchedDescriptionsPurged.Should().Be(0);
        var t = (await db.BankTransactions.FindAsync(tx.Id))!;
        (t.Description, t.CounterpartyName, t.DescriptionPurgedAt).Should().Be(("aciklama", "ad", (DateTimeOffset?)null));
        (await db.PaymentMatches.FindAsync(match.Id))!.Evidence.Should().Be("kanıt metni");
    }

    [Fact]
    public async Task Hareket_eslesme_ve_gap_180_gunde_silinir()
    {
        using var db = NewDb();
        var old = Row(TimeSpan.FromDays(180) + Minute); var young = Row(TimeSpan.FromDays(180) - Minute);
        db.BankTransactions.AddRange(old, young);
        db.PaymentMatches.AddRange(MatchFor(old, linked: true), MatchFor(young, linked: true));
        var oldGap = new PaymentMatchGap { Id = Guid.NewGuid(), LicenseId = Guid.NewGuid(), PaymentId = Guid.NewGuid(),
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-181) };
        var youngGap = new PaymentMatchGap { Id = Guid.NewGuid(), LicenseId = Guid.NewGuid(), PaymentId = Guid.NewGuid(),
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-179) };
        db.PaymentMatchGaps.AddRange(oldGap, youngGap);
        await db.SaveChangesAsync();

        var r = await Job(db).RunAsync(CancellationToken.None);

        (r.TransactionsDeleted, r.MatchesDeleted, r.GapsDeleted).Should().Be((1, 1, 1));
        (await db.BankTransactions.Select(t => t.Id).ToListAsync()).Should().BeEquivalentTo(new[] { young.Id });
        (await db.PaymentMatches.Select(m => m.BankTransactionId).ToListAsync()).Should().BeEquivalentTo(new[] { young.Id });
        (await db.PaymentMatchGaps.Select(g => g.Id).ToListAsync()).Should().BeEquivalentTo(new[] { youngGap.Id });
    }

    [Fact]
    public async Task Gec_cekilen_eski_hareket_cekimden_7_gun_gecmeden_dokunulmaz()
    {
        // Banka geç muhasebeleştirdi: işlem tarihi 200 gün önce, çekim dün. Eşleştirme ve dekont bağdaştırması yetişsin.
        using var db = NewDb();
        var late = Row(TimeSpan.FromDays(200), fetchedAge: TimeSpan.FromDays(1));
        var lateDue = Row(TimeSpan.FromDays(200), fetchedAge: TimeSpan.FromDays(7) + Minute);
        db.BankTransactions.AddRange(late, lateDue);
        await db.SaveChangesAsync();

        var r = await Job(db).RunAsync(CancellationToken.None);

        var t = (await db.BankTransactions.FindAsync(late.Id))!;
        t.RawJson.Should().NotBeNull(); t.Description.Should().Be("aciklama");
        r.TransactionsDeleted.Should().Be(1, "alt sınır yalnız çekimden sonraki 7 gündür: max(işlem tarihi + 180, çekim + 7)");
        (await db.BankTransactions.AnyAsync(x => x.Id == lateDue.Id)).Should().BeFalse();
    }

    [Fact]
    public async Task Iban_bagi_son_dogrulamadan_12_ay_sonra_silinir()
    {
        using var db = NewDb();
        var now = DateTimeOffset.UtcNow;
        var staleConfirmed = Memory(now.AddMonths(-20), now.AddMonths(-12).AddDays(-1));
        var recentlyConfirmed = Memory(now.AddMonths(-20), now.AddMonths(-1));
        var staleNeverConfirmed = Memory(now.AddMonths(-12).AddDays(-1), null);
        var youngNeverConfirmed = Memory(now.AddMonths(-1), null);
        db.CustomerIbanMemories.AddRange(staleConfirmed, recentlyConfirmed, staleNeverConfirmed, youngNeverConfirmed);
        await db.SaveChangesAsync();

        var r = await Job(db).RunAsync(CancellationToken.None);

        r.IbanMemoriesDeleted.Should().Be(2);
        (await db.CustomerIbanMemories.Select(m => m.Id).ToListAsync())
            .Should().BeEquivalentTo(new[] { recentlyConfirmed.Id, youngNeverConfirmed.Id });
    }

    [Fact]
    public async Task Her_imha_partisi_kisisel_veri_icermeyen_kalici_kaydi_lisans_basina_yazar()
    {
        // Görev 9 incelemesi: yayıncı kendi verisinin imha kanıtını isteyebilir — lisansları karıştıran parti satırın lisansı
        // başına ayrı kayıt yazar, LicenseId null kalmaz.
        using var db = NewDb();
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        BankTransaction For(Guid lic, int ageDays) { var t = Row(ageDays); t.LicenseId = lic; return t; }
        var old = For(a, 200);
        var unmatchedB = For(b, 100);
        db.BankTransactions.AddRange(old, For(a, 100), For(a, 100), unmatchedB);
        db.PaymentMatches.AddRange(MatchFor(old, linked: true), MatchFor(unmatchedB, linked: false));
        var gap = new PaymentMatchGap { Id = Guid.NewGuid(), LicenseId = b, PaymentId = Guid.NewGuid(),
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-181) };
        db.PaymentMatchGaps.Add(gap);
        var memory = Memory(DateTimeOffset.UtcNow.AddMonths(-13), null); memory.LicenseId = a;
        db.CustomerIbanMemories.Add(memory);
        await db.SaveChangesAsync();

        await Job(db).RunAsync(CancellationToken.None);

        var records = await db.DataDestructionRecords.AsNoTracking()
            .Where(r => r.Action != DataDestruction.Actions.RunCompleted).ToListAsync();
        records.Should().OnlyContain(r => r.Trigger == DataDestruction.Triggers.Retention && r.Actor == DataDestruction.SystemActor
            && r.LicenseId != null);
        records.Where(r => r.Action == DataDestruction.Actions.Deleted).Select(r => (r.Target, r.LicenseId, r.RowCount))
            .Should().BeEquivalentTo(new[]
            {
                ("BankTransactions", (Guid?)a, 1), ("PaymentMatches", (Guid?)a, 1), ("PaymentMatchGaps", (Guid?)b, 1),
                ("CustomerIbanMemories", (Guid?)a, 1),
            });
        records.Where(r => r.Target == "BankTransactions.RawJson").Select(r => (r.LicenseId, r.RowCount, r.Action))
            .Should().BeEquivalentTo(new[]
            {
                ((Guid?)a, 2, DataDestruction.Actions.Nulled), ((Guid?)b, 1, DataDestruction.Actions.Nulled),
            });
        records.Where(r => r.Target == "BankTransactions.Description+CounterpartyName").Select(r => (r.LicenseId, r.RowCount))
            .Should().BeEquivalentTo(new[] { ((Guid?)a, 2), ((Guid?)b, 1) });
        records.Where(r => r.Target == "PaymentMatches.Evidence").Select(r => (r.LicenseId, r.RowCount))
            .Should().BeEquivalentTo(new[] { ((Guid?)b, 1) });
    }

    [Fact]
    public async Task Silinecek_satir_olmayan_kosu_da_kosu_kaydi_birakir()
    {
        // İç imha prosedürü §4.2-3: her çalışma kayıt bırakır, "son başarılı çalışma" tablodan (ve dış kopyadan) okunur.
        using var db = NewDb();
        db.BankTransactions.Add(Row(1));
        await db.SaveChangesAsync();

        await Job(db).RunAsync(CancellationToken.None);

        (await db.DataDestructionRecords.AsNoTracking().ToListAsync()).Should().ContainSingle()
            .Which.Should().Match<OrderDeck.LicenseServer.Domain.DataDestructionRecord>(r =>
                r.Action == DataDestruction.Actions.RunCompleted && r.Target == BankDataRetentionJob.JobId && r.RowCount == 0
                && r.Trigger == DataDestruction.Triggers.Retention && r.Actor == DataDestruction.SystemActor && r.LicenseId == null
                && !r.Criterion.Contains(';'));
    }

    [Fact]
    public async Task Parti_boyutunu_asan_satirlar_parti_parti_bosaltilir_izleyici_birikmez()
    {
        // İlk koşu (ya da uzun kesintiden sonraki) on binlerce satırı tek listede belleğe almasın: her SaveChanges en
        // fazla BatchSize satır yazar, partiler arasında izleyici boşaltılır.
        var counter = new ModifiedCountInterceptor();
        using var db = NewDb(counter);
        var total = BankDataRetentionJob.BatchSize * 2 + 1;
        db.BankTransactions.AddRange(Enumerable.Range(0, total).Select(_ => Row(100)));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        counter.ModifiedPerSave.Clear();

        var r = await Job(db).RunAsync(CancellationToken.None);

        r.RawPurged.Should().Be(total); r.UnmatchedDescriptionsPurged.Should().Be(total);
        counter.ModifiedPerSave.Should().HaveCountGreaterThan(1);
        counter.ModifiedPerSave.SkipLast(1).Should().OnlyContain(n => n > 0 && n <= BankDataRetentionJob.BatchSize);
        counter.ModifiedPerSave[^1].Should().Be(0, "son kayıt yalnız koşu kaydını ekler");
        db.ChangeTracker.Entries().Should().BeEmpty("partiler arasında ve sonda izleyici boşaltılır");
        (await db.BankTransactions.CountAsync(t => t.RawJson != null || t.Description != null || t.CounterpartyName != null))
            .Should().Be(0);
    }

    [Fact]
    public async Task Is_duserse_yoneticiye_e_posta_gider_ve_hata_yukari_cikar()
    {
        // KVKK Ö-13: iş sessizce düşerse saklama süreleri fark edilmeden aşılır.
        var failing = new FailingSaveInterceptor();
        var email = new TestEmailSender();
        using var db = NewDb(failing);
        db.BankTransactions.Add(Row(100));
        await db.SaveChangesAsync();
        failing.Armed = true;

        var act = () => Job(db, email).RunAsync(CancellationToken.None);

        await act.Should().ThrowAsync<DbUpdateException>();
        var sent = email.Sent.Should().ContainSingle().Subject;
        sent.Subject.Should().Be(BankDataRetentionJob.FailureSubject);
        sent.PlainBody.Should().Contain(nameof(DbUpdateException));
        failing.Armed = false;
        (await db.DataDestructionRecords.AsNoTracking().CountAsync(r => r.Action == DataDestruction.Actions.RunCompleted))
            .Should().Be(0, "düşen koşu başarılı koşu kaydı bırakmaz");
    }
}
