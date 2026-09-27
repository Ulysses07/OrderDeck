using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Domain.Bank;
using OrderDeck.LicenseServer.Services.Bank;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Bank;

public sealed class BankMatchSweepJobTests
{
    /// <summary>Her SaveChanges'te izlenen satır sayısını kaydeder; istenirse belirli bir hareketin önerisini düşürür.</summary>
    private sealed class SaveProbe : SaveChangesInterceptor
    {
        public List<int> TrackedPerSave { get; } = new();
        public Guid? FailFor { get; set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var tracker = eventData.Context!.ChangeTracker;
            TrackedPerSave.Add(tracker.Entries().Count());
            if (FailFor is { } id && tracker.Entries<PaymentMatch>().Any(e => e.Entity.BankTransactionId == id))
                throw new InvalidOperationException("db gitti");
            return ValueTask.FromResult(result);
        }
    }

    private static LicenseDbContext NewDb(IInterceptor? interceptor = null)
    {
        var options = new DbContextOptionsBuilder<LicenseDbContext>().UseInMemoryDatabase($"sweep-{Guid.NewGuid():N}");
        if (interceptor is not null) options.AddInterceptors(interceptor);
        return new LicenseDbContext(options.Options);
    }

    /// <summary>Üretimdeki gibi: iş ve eşleştirici aynı kapsamlı bağlamı paylaşır.</summary>
    private static BankMatchSweepJob Job(LicenseDbContext db)
        => new(db, new PaymentMatcher(db, Options.Create(new BankOptions()), NullLogger<PaymentMatcher>.Instance),
            NullLogger<BankMatchSweepJob>.Instance);

    private static BankTransaction Tx(Guid lic, string description, TimeSpan age,
        BankTransactionDirection direction = BankTransactionDirection.Incoming, decimal amount = 100m, string code = "FT") => new()
    {
        Id = Guid.NewGuid(), LicenseId = lic, ObifinId = Random.Shared.NextInt64(1, 1_000_000_000), ObifinAccountId = 1,
        BankaKodu = "qnb", Direction = direction, Amount = amount, Currency = "TL", OccurredAt = DateTimeOffset.UtcNow - age,
        Description = description, TransactionCode = code, FetchedAt = DateTimeOffset.UtcNow - age,
    };

    [Fact]
    public async Task Eslesmesi_olmayan_gelen_hareket_eslestirilir_giden_sifir_ve_eski_hareket_atlanir()
    {
        using var db = NewDb(); var lic = Guid.NewGuid();
        var customer = new WpfCustomerProjection { Id = Guid.NewGuid(), LicenseId = lic, Platform = "youtube", Username = "ayse_gul34", UpdatedAt = DateTimeOffset.UtcNow };
        var named = Tx(lic, "HAVALE ayse_gul34", TimeSpan.FromDays(2));
        var noSignal = Tx(lic, "HAVALE siparis", TimeSpan.FromDays(29));
        var outgoing = Tx(lic, "HAVALE ayse_gul34", TimeSpan.FromDays(1), BankTransactionDirection.Outgoing);
        var zero = Tx(lic, "HAVALE ayse_gul34", TimeSpan.FromDays(1), amount: 0m);
        var old = Tx(lic, "HAVALE ayse_gul34", TimeSpan.FromDays(BankMatchSweepJob.LookbackDays + 1));
        db.WpfCustomerProjections.Add(customer);
        db.BankTransactions.AddRange(named, noSignal, outgoing, zero, old);
        await db.SaveChangesAsync();

        var swept = await Job(db).RunAsync(CancellationToken.None);

        swept.Should().Be(2);
        var matches = await db.PaymentMatches.AsNoTracking().ToListAsync();
        matches.Select(m => m.BankTransactionId).Should().BeEquivalentTo(new[] { named.Id, noSignal.Id });
        matches.Single(m => m.BankTransactionId == named.Id).ProposedWpfCustomerId.Should().Be(customer.Id);
        matches.Single(m => m.BankTransactionId == noSignal.Id).Status.Should().Be(PaymentMatchStatus.NoProposal);
    }

    [Fact]
    public async Task Eslesmesi_olan_harekete_dokunulmaz()
    {
        using var db = NewDb(); var lic = Guid.NewGuid();
        db.WpfCustomerProjections.Add(new WpfCustomerProjection { Id = Guid.NewGuid(), LicenseId = lic, Platform = "youtube", Username = "ayse_gul34", UpdatedAt = DateTimeOffset.UtcNow });
        var tx = Tx(lic, "HAVALE ayse_gul34", TimeSpan.FromDays(1));
        var stamp = DateTimeOffset.UtcNow.AddDays(-1);
        // Eşleştirici bugün başka bir sonuç üretirdi (kullanıcı adı var): tarama yine de dokunmamalı.
        var existing = new PaymentMatch
        {
            Id = Guid.NewGuid(), LicenseId = lic, BankTransactionId = tx.Id, Status = PaymentMatchStatus.NoProposal,
            Layer = PaymentMatchLayer.None, Evidence = "no-signal", CreatedAt = stamp, UpdatedAt = stamp,
        };
        db.BankTransactions.Add(tx); db.PaymentMatches.Add(existing);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var swept = await Job(db).RunAsync(CancellationToken.None);

        swept.Should().Be(0);
        var after = await db.PaymentMatches.AsNoTracking().SingleAsync();
        after.Status.Should().Be(PaymentMatchStatus.NoProposal);
        after.Evidence.Should().Be("no-signal");
        after.UpdatedAt.Should().Be(stamp);
    }

    [Fact]
    public async Task Tek_kosu_en_fazla_500_hareket_isler_en_eskiden_baslar_izleyiciyi_50de_bir_bosaltir()
    {
        var probe = new SaveProbe();
        using var db = NewDb(probe); var lic = Guid.NewGuid();
        var txs = Enumerable.Range(0, BankMatchSweepJob.MaxPerRun + 1)
            .Select(i => Tx(lic, "HAVALE siparis", TimeSpan.FromDays(20) - TimeSpan.FromMinutes(i)))
            .ToList();
        db.BankTransactions.AddRange(txs);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        probe.TrackedPerSave.Clear();

        var first = await Job(db).RunAsync(CancellationToken.None);

        first.Should().Be(BankMatchSweepJob.MaxPerRun);
        var matched = await db.PaymentMatches.AsNoTracking().Select(m => m.BankTransactionId).ToListAsync();
        matched.Should().NotContain(txs[^1].Id, "en yeni hareket sıradaki koşuya kalır");
        probe.TrackedPerSave.Max().Should().BeLessThanOrEqualTo(BankMatchSweepJob.TrackerClearInterval,
            "öneriler koşu boyunca izleyicide birikmez");

        var second = await Job(db).RunAsync(CancellationToken.None);

        second.Should().Be(1);
        (await db.PaymentMatches.CountAsync()).Should().Be(BankMatchSweepJob.MaxPerRun + 1);
    }

    [Fact]
    public async Task Dislanan_hareket_eslesme_alir_sonraki_kosuda_yeniden_taranmaz()
    {
        using var db = NewDb(); var lic = Guid.NewGuid();
        var pos = Tx(lic, "POS TAHSILAT", TimeSpan.FromDays(3), code: "CCP");
        db.BankTransactions.Add(pos);
        await db.SaveChangesAsync();

        (await Job(db).RunAsync(CancellationToken.None)).Should().Be(1);
        var match = await db.PaymentMatches.AsNoTracking().SingleAsync();
        match.Status.Should().Be(PaymentMatchStatus.NoProposal);
        match.Evidence.Should().Be("excluded:CCP");

        (await Job(db).RunAsync(CancellationToken.None)).Should().Be(0);
        (await db.PaymentMatches.AsNoTracking().SingleAsync()).UpdatedAt.Should().Be(match.UpdatedAt);
    }

    [Fact]
    public async Task Dusen_hareket_digerlerini_bekletmez_kosu_sonda_hata_verir()
    {
        // En eskiden başlanır: kalıcı düşen tek bir hareket her koşunun başında durup arkasındakileri aç bırakmasın.
        var probe = new SaveProbe();
        using var db = NewDb(probe); var lic = Guid.NewGuid();
        var poisoned = Tx(lic, "HAVALE siparis", TimeSpan.FromDays(10));
        var others = new[] { Tx(lic, "HAVALE siparis", TimeSpan.FromDays(5)), Tx(lic, "HAVALE siparis", TimeSpan.FromDays(4)) };
        db.BankTransactions.Add(poisoned); db.BankTransactions.AddRange(others);
        await db.SaveChangesAsync();
        probe.FailFor = poisoned.Id;

        var act = () => Job(db).RunAsync(CancellationToken.None);

        await act.Should().ThrowAsync<AggregateException>();
        var matched = await db.PaymentMatches.AsNoTracking().Select(m => m.BankTransactionId).ToListAsync();
        matched.Should().BeEquivalentTo(others.Select(t => t.Id), "düşen hareket eşleşmesiz kalır, sonraki koşu yeniden dener");
    }
}
