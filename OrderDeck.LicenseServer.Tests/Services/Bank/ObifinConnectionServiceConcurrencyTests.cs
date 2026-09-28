using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Domain.Bank;
using OrderDeck.LicenseServer.Services.Bank;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Bank;

/// <summary>
/// Onaylı kimlik değişikliğinin gölge veri silmesi ile gölge eşleştirmenin eşzamanlı yazısı — GERÇEK SQL Server'da.
/// <see cref="PaymentMatch.UpdatedAt"/> eşzamanlılık jetonudur: yüklenen eşleşmenin DELETE'i <c>WHERE UpdatedAt = özgün</c>
/// taşır. Yüklemeden sonra eşleştirici, tarama, bağdaştırma ya da admin satıra yazarsa DELETE 0 satır etkiler ve kimlik
/// yazımıyla birlikte bütün silme geri alınır. Servis eşleşmelerin jetonunu tazeleyip bir kez yeniden siler; ikinci
/// çakışma admin'e gösterilebilir <see cref="ObifinValidationException"/> olur. InMemory'de SaveChanges işlem değildir
/// (çakışmadan önceki silmeler kalır); geri almanın kendisi burada gerçek. Testcontainers kullanır; CI ubuntu işinde koşar.
/// </summary>
[Collection(SqlServerCollection.Name)]
[Trait("Category", "Testcontainers")]
public sealed class ObifinConnectionServiceConcurrencyTests : IAsyncLifetime
{
    private static readonly IDataProtectionProvider Protection = new EphemeralDataProtectionProvider();

    private readonly SqlServerContainerFixture _sql;
    private string _connStr = null!;

    public ObifinConnectionServiceConcurrencyTests(SqlServerContainerFixture sql) => _sql = sql;

    public async Task InitializeAsync()
    {
        _connStr = await _sql.CreateDatabaseAsync();
        await using var db = NewDb(interceptor: null);
        await db.Database.EnsureCreatedAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>Eşleşme silen ilk <paramref name="times"/> SaveChanges'ten hemen önce öteki bağlamın yazısını koşturur:
    /// servisin yüklediği satır kaydetmeden önce değişmiş olur.</summary>
    private sealed class BeforeMatchDelete(Func<Task> interleave, int times = 1) : SaveChangesInterceptor
    {
        public int Fired { get; private set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Fired < times && eventData.Context!.ChangeTracker.Entries<PaymentMatch>().Any(e => e.State == EntityState.Deleted))
            {
                Fired++;
                await interleave();
            }
            return result;
        }
    }

    private sealed record Seeded(Guid LicenseId, string UserCode, Guid TransactionId, Guid MatchId);

    [Fact]
    public async Task Eszamanli_eslestirme_yazisiyla_carpisan_onayli_sifirlama_tazeleyip_yine_siler()
    {
        // Admin kimlik değişikliğini onayladı; servis gölge satırları yükledikten sonra eşleştirici bir öneriyi yeniden
        // hesaplar (jeton ilerler). Sıfırlama 500 ile geri alınmaz: jeton tazelenir, satır yine silinir, kimlik yazılır.
        var s = await SeedAsync();
        var recompute = new BeforeMatchDelete(() => RecomputeConcurrentlyAsync(s));
        await using var db = NewDb(recompute);
        var newUserCode = $"api-{Guid.NewGuid():N}@x";

        var result = await Svc(db).UpsertWithResultAsync(s.LicenseId, "", newUserCode, password: null, apiKey: null,
            CancellationToken.None, allowShadowReset: true);

        result.ShadowDataReset.Should().BeTrue();
        recompute.Fired.Should().Be(1, "yeniden deneme eşzamanlı yazıyla bir kez daha karşılaşmadı");
        await using var verify = NewDb(interceptor: null);
        (await verify.PaymentMatches.AsNoTracking().CountAsync(m => m.LicenseId == s.LicenseId)).Should().Be(0,
            "tazelenen satır da silinir");
        (await verify.BankTransactions.AsNoTracking().CountAsync(t => t.LicenseId == s.LicenseId)).Should().Be(0);
        (await verify.ObifinConnections.AsNoTracking().SingleAsync(c => c.LicenseId == s.LicenseId)).UserCode
            .Should().Be(newUserCode);
    }

    [Fact]
    public async Task Yeniden_denemede_ikinci_kez_carpisirsa_admin_mesaji_verir_hicbir_sey_yazilmaz()
    {
        var s = await SeedAsync();
        var recompute = new BeforeMatchDelete(() => RecomputeConcurrentlyAsync(s), times: 2);
        await using var db = NewDb(recompute);

        var act = () => Svc(db).UpsertWithResultAsync(s.LicenseId, "", $"api-{Guid.NewGuid():N}@x", password: null,
            apiKey: null, CancellationToken.None, allowShadowReset: true);

        await act.Should().ThrowAsync<ObifinValidationException>()
            .WithMessage(ObifinConnectionService.ConcurrentMatchWriteMessage);
        recompute.Fired.Should().Be(2, "yalnız bir kez yeniden denenir");
        db.ChangeTracker.HasChanges().Should().BeFalse("düşen sıfırlama kapsamın sonraki bir kaydında tamamlanmaz");
        await using var verify = NewDb(interceptor: null);
        (await verify.PaymentMatches.AsNoTracking().CountAsync(m => m.Id == s.MatchId)).Should().Be(1, "silme geri alındı");
        (await verify.BankTransactions.AsNoTracking().CountAsync(t => t.Id == s.TransactionId)).Should().Be(1);
        (await verify.ObifinConnections.AsNoTracking().SingleAsync(c => c.LicenseId == s.LicenseId)).UserCode
            .Should().Be(s.UserCode, "kimlik yazımı silmeyle aynı işlemdeydi");
    }

    /// <summary>Eşleştiricinin yeniden hesabı gibi: satırın önerisini ayrı bir bağlamda yeniden yazar, jetonu ilerletir.</summary>
    private async Task RecomputeConcurrentlyAsync(Seeded s)
    {
        await using var other = NewDb(interceptor: null);
        var m = await other.PaymentMatches.SingleAsync(x => x.Id == s.MatchId);
        m.Evidence = $"ambiguous-name:{Random.Shared.Next(2, 9)}";
        m.UpdatedAt = DateTimeOffset.UtcNow;
        await other.SaveChangesAsync();
    }

    /// <summary>Müşteri, lisans, doğrulanmamış Obifin bağlantısı, bir gelen hareket ve onun önerisiz eşleşme satırı.
    /// Kimlik alanları üretilir.</summary>
    private async Task<Seeded> SeedAsync()
    {
        await using var db = NewDb(interceptor: null);
        var now = DateTimeOffset.UtcNow;
        var customer = new Customer
        {
            Id = Guid.NewGuid(), Email = $"o-{Guid.NewGuid():N}@example.com", Name = "Yayinci",
            PasswordHash = $"h-{Guid.NewGuid():N}", CreatedAt = now,
        };
        var license = new License
        {
            Id = Guid.NewGuid(), CustomerId = customer.Id, LicenseKey = $"obifin-{Guid.NewGuid():N}", SkuCode = "STD",
            ActivationSlots = 1, IssuedAt = now, ExpiresAt = now.AddDays(30),
        };
        db.Customers.Add(customer);
        db.Licenses.Add(license);
        await db.SaveChangesAsync();

        var userCode = $"api-{Guid.NewGuid():N}@x";
        await Svc(db).UpsertAsync(license.Id, "", userCode, $"pw-{Guid.NewGuid():N}", $"k-{Guid.NewGuid():N}",
            CancellationToken.None);
        var tx = new BankTransaction
        {
            Id = Guid.NewGuid(), LicenseId = license.Id, ObifinId = Random.Shared.NextInt64(1, 1_000_000_000), ObifinAccountId = 1,
            BankaKodu = "qnb", Direction = BankTransactionDirection.Incoming, Amount = 120m, Currency = "TL",
            OccurredAt = now.AddHours(-1), Description = "EFT GELEN", TransactionCode = "FT", FetchedAt = now,
        };
        var match = new PaymentMatch
        {
            Id = Guid.NewGuid(), LicenseId = license.Id, BankTransactionId = tx.Id, Layer = PaymentMatchLayer.None,
            Evidence = "no-signal", Status = PaymentMatchStatus.NoProposal, CreatedAt = now, UpdatedAt = now,
        };
        db.BankTransactions.Add(tx);
        db.PaymentMatches.Add(match);
        await db.SaveChangesAsync();
        return new Seeded(license.Id, userCode, tx.Id, match.Id);
    }

    private static ObifinConnectionService Svc(LicenseDbContext db)
        => new(db, new NullObifinClient(), Protection,
            new BankHasher(Options.Create(new BankOptions { HashKey = $"k-{Guid.NewGuid():N}{Guid.NewGuid():N}" })),
            Options.Create(new ObifinOptions()), NullLogger<ObifinConnectionService>.Instance);

    private LicenseDbContext NewDb(IInterceptor? interceptor)
    {
        var opt = new DbContextOptionsBuilder<LicenseDbContext>().UseSqlServer(_connStr);
        if (interceptor is not null) opt.AddInterceptors(interceptor);
        return new LicenseDbContext(opt.Options);
    }
}
