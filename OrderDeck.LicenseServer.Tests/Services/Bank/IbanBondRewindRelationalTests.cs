using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Domain.Bank;
using OrderDeck.LicenseServer.Services.Bank;
using OrderDeck.LicenseServer.Services.Privacy;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Bank;

/// <summary>Eşleme kaldırılınca IBAN bağının süresinin geri çekilmesi ve kalan doğrulama yoksa bağın imha kaydıyla silinmesi
/// (KVKK K-6; <c>PaymentMatchReconciler.RewindIbanConfirmationAsync</c>) gerçek SQL Server'da: göçlerle kurulmuş şema,
/// kalan doğrulamaları bulan birleştirme sorgusu, eşzamanlılık jetonu ve işlemsel geri alma. InMemory işlemsel değildir:
/// düşen bir denemenin imha kaydı orada depoda kalır; yeniden denemede tek kayıt yazıldığı ancak burada sınanır.</summary>
[Collection(SqlServerCollection.Name)]
[Trait("Category", "Testcontainers")]
public sealed class IbanBondRewindRelationalTests : IAsyncLifetime
{
    private readonly SqlServerContainerFixture _sql;
    private string _connStr = null!;
    private RelationalApiFactory _factory = null!;
    public IbanBondRewindRelationalTests(SqlServerContainerFixture sql) => _sql = sql;

    public async Task InitializeAsync()
    {
        _connStr = await _sql.CreateDatabaseAsync();
        _factory = new RelationalApiFactory(_connStr);
        _ = _factory.Services; // göçleri şimdi kur
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    private sealed record Seeded(Guid LicenseId, Guid WpfCustomerId, string Hash);

    private async Task<Seeded> SeedAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var now = DateTimeOffset.UtcNow;
        var customer = new Customer { Id = Guid.NewGuid(), Email = $"ib-{Guid.NewGuid():N}@example.test", Name = "Bag",
            PasswordHash = $"h-{Guid.NewGuid():N}", CreatedAt = now };
        var license = new License { Id = Guid.NewGuid(), CustomerId = customer.Id, LicenseKey = "ib-" + Guid.NewGuid().ToString("N")[..12],
            SkuCode = "STD", ActivationSlots = 1, IssuedAt = now, ExpiresAt = now.AddYears(1) };
        var wpf = new WpfCustomerProjection { Id = Guid.NewGuid(), LicenseId = license.Id, Platform = "youtube",
            Username = "ornek_musteri34", UpdatedAt = now };
        db.Customers.Add(customer); db.Licenses.Add(license); db.WpfCustomerProjections.Add(wpf);
        await db.SaveChangesAsync();
        return new Seeded(license.Id, wpf.Id, $"{Guid.NewGuid():N}{Guid.NewGuid():N}");
    }

    private async Task<BankTransaction> TxAsync(Seeded s, int ageDays)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var at = DateTimeOffset.UtcNow.AddDays(-ageDays);
        var tx = new BankTransaction { Id = Guid.NewGuid(), LicenseId = s.LicenseId, ObifinId = Random.Shared.NextInt64(1, 1_000_000_000),
            ObifinAccountId = 6, BankaKodu = "qnb", Direction = BankTransactionDirection.Incoming, Amount = 100m + ageDays, Currency = "TL",
            OccurredAt = at, FetchedAt = at, Description = "EFT GELEN", CounterpartyIbanHash = s.Hash,
            CounterpartyIbanHashValid = true }; // Görev 11'den sonraki çekim: özet geçerli IBAN'dan (KVKK Ö-7)
        db.BankTransactions.Add(tx);
        await db.SaveChangesAsync();
        return tx;
    }

    private async Task ManualMatchAsync(Seeded s, BankTransaction tx)
    {
        using var scope = _factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<PaymentMatchReconciler>()
            .ManualMatchAsync(s.LicenseId, tx.Id, s.WpfCustomerId, CancellationToken.None);
    }

    [Fact]
    public async Task Kaldirma_bagin_suresini_kalan_son_dogrulamaya_geri_ceker()
    {
        var s = await SeedAsync();
        var first = await TxAsync(s, 40);
        var last = await TxAsync(s, 3);
        await ManualMatchAsync(s, first);
        await ManualMatchAsync(s, last);

        using (var scope = _factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<PaymentMatchReconciler>()
                .UnmatchAsync(s.LicenseId, last.Id, CancellationToken.None, Guid.NewGuid().ToString());

        using var verify = _factory.Services.CreateScope();
        var db = verify.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var memory = await db.CustomerIbanMemories.AsNoTracking().SingleAsync(m => m.LicenseId == s.LicenseId);
        memory.LastConfirmedAt.Should().Be(first.OccurredAt, "kalan tek doğrulama ilk havale");
        (await db.DataDestructionRecords.AsNoTracking().CountAsync(r => r.LicenseId == s.LicenseId)).Should().Be(0, "geri çekme veri silmez");
    }

    [Fact]
    public async Task Kalan_dogrulama_yoksa_bag_silinir_yeniden_denemede_tek_imha_kaydi_yazilir()
    {
        // Öğreten havale 180 günlük kuralla silinmiş; bağın süresini yalnız tazeleyen havale taşıyor. Kaldırmanın ilk kaydı
        // kanıt temizliğinin ilerlettiği jetona takılır: SQL Server bütün işlemi (bağ silmesi ve imha kaydı dahil) geri alır,
        // deneme yeniden koşar. İlk denemenin kaydı izde kalsaydı burada iki satır yazılırdı.
        var s = await SeedAsync();
        var source = await TxAsync(s, 200);
        var refreshing = await TxAsync(s, 3);
        await ManualMatchAsync(s, source);
        await ManualMatchAsync(s, refreshing);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            db.PaymentMatches.RemoveRange(await db.PaymentMatches.Where(m => m.BankTransactionId == source.Id).ToListAsync());
            db.BankTransactions.Remove(await db.BankTransactions.SingleAsync(t => t.Id == source.Id));
            await db.SaveChangesAsync(); // saklama işinin 180 günlük silmesi
        }
        var race = new EvidenceCleanupBeforeFirstMatchSave(_connStr, refreshing.Id);
        await using var admin = new LicenseDbContext(new DbContextOptionsBuilder<LicenseDbContext>()
            .UseSqlServer(_connStr).AddInterceptors(race).Options);
        var log = new LogRecorder<PaymentMatchReconciler>();
        var recon = new PaymentMatchReconciler(admin,
            new PaymentMatcher(admin, Options.Create(new BankOptions()), NullLogger<PaymentMatcher>.Instance),
            Options.Create(new BankOptions()), log);
        var actor = Guid.NewGuid().ToString();

        await recon.UnmatchAsync(s.LicenseId, refreshing.Id, CancellationToken.None, actor);

        race.Fired.Should().Be(1);
        log.Entries.Should().Contain(e => e.Message.Contains("yeniden deneniyor"), "ilk kayıt jetona takıldı");
        using var verify = _factory.Services.CreateScope();
        var vdb = verify.ServiceProvider.GetRequiredService<LicenseDbContext>();
        (await vdb.CustomerIbanMemories.AsNoTracking().CountAsync(m => m.LicenseId == s.LicenseId)).Should().Be(0);
        var record = (await vdb.DataDestructionRecords.AsNoTracking().Where(r => r.LicenseId == s.LicenseId).ToListAsync())
            .Should().ContainSingle().Subject;
        (record.Target, record.Action, record.RowCount, record.Trigger, record.Actor).Should().Be(("CustomerIbanMemories",
            DataDestruction.Actions.Deleted, 1, DataDestruction.Triggers.Retention, actor));
        record.Criterion.Should().Be("LastConfirmedAt+12ay, kaldırma, kalan doğrulama yok");
    }

    /// <summary>Bir eşleşmeyi değiştiren İLK kayıttan hemen önce başka bir bağlamda o hareketin kanıtını boşaltır (saklama işi
    /// ya da KVKK silmesi gibi): jeton ilerler, ilk kayıt çakışır.</summary>
    private sealed class EvidenceCleanupBeforeFirstMatchSave(string connStr, Guid transactionId) : SaveChangesInterceptor
    {
        public int Fired { get; private set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Fired == 0 && eventData.Context!.ChangeTracker.Entries<PaymentMatch>().Any(e => e.State == EntityState.Modified))
            {
                Fired++;
                await using var cleanup = new LicenseDbContext(new DbContextOptionsBuilder<LicenseDbContext>().UseSqlServer(connStr).Options);
                var m = await cleanup.PaymentMatches.SingleAsync(x => x.BankTransactionId == transactionId, cancellationToken);
                m.Evidence = null; m.UpdatedAt = m.UpdatedAt.AddMilliseconds(1);
                await cleanup.SaveChangesAsync(cancellationToken);
            }
            return result;
        }
    }
}
