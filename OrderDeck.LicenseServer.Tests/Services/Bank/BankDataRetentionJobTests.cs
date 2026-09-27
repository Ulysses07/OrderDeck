using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain.Bank;
using OrderDeck.LicenseServer.Services.Bank;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Bank;

public sealed class BankDataRetentionJobTests
{
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

    private static LicenseDbContext NewDb(IInterceptor? interceptor = null)
    {
        var options = new DbContextOptionsBuilder<LicenseDbContext>().UseInMemoryDatabase($"bank-ret-{Guid.NewGuid():N}");
        if (interceptor is not null) options.AddInterceptors(interceptor);
        return new LicenseDbContext(options.Options);
    }

    private static BankDataRetentionJob Job(LicenseDbContext db)
        => new(db, Options.Create(new BankOptions { HashKey = new string('k', 32) }), NullLogger<BankDataRetentionJob>.Instance);

    private static BankTransaction Row(int ageDays) => Row(TimeSpan.FromDays(ageDays));

    private static BankTransaction Row(TimeSpan age) => new()
    {
        Id = Guid.NewGuid(), LicenseId = Guid.NewGuid(), ObifinId = Random.Shared.NextInt64(1, 1_000_000), ObifinAccountId = 1,
        BankaKodu = "qnb", Direction = BankTransactionDirection.Incoming, Amount = 10m, Currency = "TL",
        OccurredAt = DateTimeOffset.UtcNow - age, FetchedAt = DateTimeOffset.UtcNow - age,
        Description = "aciklama", CounterpartyName = "ad", RawJson = "{}",
    };

    private static PaymentMatch Match(TimeSpan age) => new()
    {
        Id = Guid.NewGuid(), LicenseId = Guid.NewGuid(), BankTransactionId = Guid.NewGuid(), Evidence = "kanıt metni",
        Layer = PaymentMatchLayer.NameAmount, Status = PaymentMatchStatus.Proposed,
        CreatedAt = DateTimeOffset.UtcNow - age, UpdatedAt = DateTimeOffset.UtcNow - age,
    };

    [Fact]
    public async Task Ham_json_90_gun_aciklama_180_gun_sonra_bosaltilir_tutar_ve_hash_kalir()
    {
        using var db = NewDb();
        var fresh = Row(10); var mid = Row(100); var old = Row(200);
        db.BankTransactions.AddRange(fresh, mid, old);
        await db.SaveChangesAsync();

        await Job(db).RunAsync(CancellationToken.None);

        (await db.BankTransactions.FindAsync(fresh.Id))!.RawJson.Should().NotBeNull();
        var m = (await db.BankTransactions.FindAsync(mid.Id))!;
        m.RawJson.Should().BeNull(); m.Description.Should().Be("aciklama");
        var o = (await db.BankTransactions.FindAsync(old.Id))!;
        o.RawJson.Should().BeNull(); o.Description.Should().BeNull(); o.CounterpartyName.Should().BeNull();
        o.DescriptionPurgedAt.Should().NotBeNull(); o.Amount.Should().Be(10m);
    }

    [Fact]
    public async Task Sinirda_90_ve_180_gunun_hemen_icindeki_kalir_hemen_disindaki_bosaltilir()
    {
        using var db = NewDb();
        var minute = TimeSpan.FromMinutes(1);
        var rawIn = Row(TimeSpan.FromDays(90) - minute); var rawOut = Row(TimeSpan.FromDays(90) + minute);
        var descIn = Row(TimeSpan.FromDays(180) - minute); var descOut = Row(TimeSpan.FromDays(180) + minute);
        db.BankTransactions.AddRange(rawIn, rawOut, descIn, descOut);
        await db.SaveChangesAsync();

        await Job(db).RunAsync(CancellationToken.None);

        (await db.BankTransactions.FindAsync(rawIn.Id))!.RawJson.Should().NotBeNull("90 günün bir dakika içinde");
        var ro = (await db.BankTransactions.FindAsync(rawOut.Id))!;
        ro.RawJson.Should().BeNull("90 günü bir dakika geçti"); ro.Description.Should().Be("aciklama");
        var di = (await db.BankTransactions.FindAsync(descIn.Id))!;
        di.Description.Should().Be("aciklama", "180 günün bir dakika içinde"); di.CounterpartyName.Should().Be("ad");
        di.DescriptionPurgedAt.Should().BeNull();
        var dout = (await db.BankTransactions.FindAsync(descOut.Id))!;
        dout.Description.Should().BeNull("180 günü bir dakika geçti"); dout.CounterpartyName.Should().BeNull();
        dout.DescriptionPurgedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Kanit_metni_180_gunden_eskiyse_bosaltilir_yenisi_kalir()
    {
        // Faz 1: öneri kanıtı (PaymentMatch.Evidence) açıklama/ad parçası taşıyabilir — açıklamayla aynı 180 gün.
        using var db = NewDb();
        var minute = TimeSpan.FromMinutes(1);
        var inside = Match(TimeSpan.FromDays(180) - minute); var outside = Match(TimeSpan.FromDays(180) + minute);
        db.PaymentMatches.AddRange(inside, outside);
        await db.SaveChangesAsync();

        await Job(db).RunAsync(CancellationToken.None);

        (await db.PaymentMatches.FindAsync(inside.Id))!.Evidence.Should().Be("kanıt metni");
        var o = (await db.PaymentMatches.FindAsync(outside.Id))!;
        o.Evidence.Should().BeNull();
        o.Status.Should().Be(PaymentMatchStatus.Proposed, "yalnız kanıt metni boşaltılır, öneri kalır");
        o.UpdatedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1), "her yazan eşzamanlılık jetonunu ilerletir");
    }

    [Fact]
    public async Task Parti_boyutunu_asan_satirlar_parti_parti_bosaltilir_izleyici_birikmez()
    {
        // İlk koşu (ya da uzun kesintiden sonraki) on binlerce satırı tek listede belleğe almasın: her SaveChanges en
        // fazla BatchSize satır yazar, partiler arasında izleyici boşaltılır.
        var counter = new ModifiedCountInterceptor();
        using var db = NewDb(counter);
        var total = BankDataRetentionJob.BatchSize * 2 + 1;
        db.BankTransactions.AddRange(Enumerable.Range(0, total).Select(_ => Row(200)));
        db.PaymentMatches.AddRange(Enumerable.Range(0, BankDataRetentionJob.BatchSize + 1).Select(_ => Match(TimeSpan.FromDays(200))));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        counter.ModifiedPerSave.Clear();

        var (raw, desc) = await Job(db).RunAsync(CancellationToken.None);

        raw.Should().Be(total); desc.Should().Be(total);
        counter.ModifiedPerSave.Should().NotBeEmpty().And.OnlyContain(n => n > 0 && n <= BankDataRetentionJob.BatchSize);
        db.ChangeTracker.Entries().Should().BeEmpty("partiler arasında ve sonda izleyici boşaltılır");
        (await db.BankTransactions.CountAsync(t => t.RawJson != null || t.Description != null || t.CounterpartyName != null))
            .Should().Be(0);
        (await db.PaymentMatches.CountAsync(m => m.Evidence != null)).Should().Be(0);
    }
}
