using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Privacy;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Privacy;

/// <summary>
/// KVKK K-3, karar 24: veri sorumlusu bilgisinin eşzamanlı iki yazımı (çift tıklama, panel ile yönetim aynı anda) gerçek SQL
/// Server'da. İkinci yazan çakışır (birincil anahtar, sürüm jetonu ya da (müşteri, sürüm) tekil indeksi), satırı taze okuyup
/// bir kez yeniden dener: içerik aynıysa hiçbir şey yazmaz, değilse yeni sürüm alır. Hiçbir sürüm numarası iki içeriğe
/// verilmez. InMemory'de SaveChanges işlem değildir ve tekil indeks yoktur: çakışan yazımın bir kısmı kalırdı, burada sınanır.
/// Testcontainers kullanır; CI ubuntu işinde koşar.
/// </summary>
[Collection(SqlServerCollection.Name)]
[Trait("Category", "Testcontainers")]
public sealed class DataControllerProfilesRelationalTests : IAsyncLifetime
{
    private readonly SqlServerContainerFixture _sql;
    private string _connStr = null!;

    public DataControllerProfilesRelationalTests(SqlServerContainerFixture sql) => _sql = sql;

    public async Task InitializeAsync()
    {
        _connStr = await _sql.CreateDatabaseAsync();
        await using var db = NewDb();
        await db.Database.EnsureCreatedAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private LicenseDbContext NewDb(IInterceptor? interceptor = null)
    {
        var opt = new DbContextOptionsBuilder<LicenseDbContext>().UseSqlServer(_connStr);
        if (interceptor is not null) opt.AddInterceptors(interceptor);
        return new LicenseDbContext(opt.Options);
    }

    private async Task<Guid> SeedCustomerAsync()
    {
        await using var db = NewDb();
        var id = Guid.NewGuid();
        db.Customers.Add(new Customer { Id = id, Email = $"dcr-{Guid.NewGuid():N}@example.test", Name = "Dcr",
            PasswordHash = $"h-{Guid.NewGuid():N}", CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        return id;
    }

    private static DataControllerInput Input(string legalName) => DataControllerProfilesTests.Valid() with { LegalName = legalName };

    /// <summary>Yazımın kaydetmesinden hemen önce başka bir bağlamın yazımını koşturur (<paramref name="times"/> kez).</summary>
    private SaveHookInterceptor Interleave(Guid customerId, Func<int, string> otherName, int times = 1)
    {
        var hook = new SaveHookInterceptor();
        var fired = 0;
        hook.BeforeSave = async () =>
        {
            fired++;
            if (fired >= times) hook.BeforeSave = null;
            await using var other = NewDb();
            await DataControllerProfiles.UpsertAsync(other, customerId, Input(otherName(fired)), "panel", CancellationToken.None);
        };
        return hook;
    }

    private async Task<List<(int Version, string LegalName, string CreatedBy)>> VersionsAsync(Guid customerId)
    {
        await using var db = NewDb();
        return (await db.DataControllerProfileVersions.AsNoTracking().Where(v => v.CustomerId == customerId)
                .OrderBy(v => v.Version).ToListAsync())
            .Select(v => (v.Version, v.LegalName, v.CreatedBy)).ToList();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ilk_kayit_yarisinda_ikinci_yazan_satiri_taze_okuyup_yeniden_dener(bool sameContent)
    {
        var customerId = await SeedCustomerAsync();
        var mine = "Benim Unvanım";
        await using var db = NewDb(Interleave(customerId, _ => sameContent ? mine : "Diğer Unvan"));

        var view = await DataControllerProfiles.UpsertAsync(db, customerId, Input(mine), "admin:a", CancellationToken.None);

        view.LegalName.Should().Be(mine);
        if (sameContent)
        {
            view.Version.Should().Be(1, "aynı içerik yeni sürüm almaz (çift tıklama)");
            (await VersionsAsync(customerId)).Should().Equal((1, mine, "panel"));
        }
        else
        {
            view.Version.Should().Be(2);
            (await VersionsAsync(customerId)).Should().Equal((1, "Diğer Unvan", "panel"), (2, mine, "admin:a"));
        }
        await using var verify = NewDb();
        (await verify.DataControllerProfiles.AsNoTracking().SingleAsync(p => p.CustomerId == customerId))
            .Should().Match<DataControllerProfile>(p => p.LegalName == mine && p.Version == view.Version);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Guncelleme_yarisinda_ikinci_yazan_satiri_taze_okuyup_yeniden_dener(bool sameContent)
    {
        var customerId = await SeedCustomerAsync();
        await using (var seed = NewDb())
            await DataControllerProfiles.UpsertAsync(seed, customerId, Input("İlk Unvan"), "panel", CancellationToken.None);
        var mine = "Benim Unvanım";
        await using var db = NewDb(Interleave(customerId, _ => sameContent ? mine : "Diğer Unvan"));

        var view = await DataControllerProfiles.UpsertAsync(db, customerId, Input(mine), "admin:a", CancellationToken.None);

        if (sameContent)
        {
            view.Version.Should().Be(2);
            (await VersionsAsync(customerId)).Should().Equal((1, "İlk Unvan", "panel"), (2, mine, "panel"));
        }
        else
        {
            view.Version.Should().Be(3);
            (await VersionsAsync(customerId)).Should().Equal(
                (1, "İlk Unvan", "panel"), (2, "Diğer Unvan", "panel"), (3, mine, "admin:a"));
        }
    }

    [Fact]
    public async Task Yeniden_denemede_de_cakisirsa_hata_verir_bu_yazim_kaydedilmez()
    {
        var customerId = await SeedCustomerAsync();
        await using var db = NewDb(Interleave(customerId, n => $"Diğer Unvan {n}", times: 2));

        var act = () => DataControllerProfiles.UpsertAsync(db, customerId, Input("Benim Unvanım"), "admin:a", CancellationToken.None);

        await act.Should().ThrowAsync<DataControllerConflictException>().WithMessage(DataControllerProfiles.ConflictMessage);
        (await VersionsAsync(customerId)).Should().Equal((1, "Diğer Unvan 1", "panel"), (2, "Diğer Unvan 2", "panel"));
        db.ChangeTracker.Entries().Should().NotContain(e => e.State == EntityState.Added || e.State == EntityState.Modified,
            "düşen yazım izleyicide kalmaz: kapsamın sonraki bir SaveChanges'i (denetim satırı) onu yeniden denemesin");
    }
}
