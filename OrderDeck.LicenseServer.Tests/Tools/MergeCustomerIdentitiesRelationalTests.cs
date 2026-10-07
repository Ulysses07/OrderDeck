using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using OrderDeck.LicenseServer.Tools;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Tools;

/// <summary>
/// <c>merge-customer-identities</c> komutunun gövdesi gerçek SQL Server'da:
/// çıkış kodları (0 tamam, 1 atlanan grup, 3 son koşul) ve çıktıda kişisel veri
/// olmaması. Gövde, bağlantısı ve yazıcıları verilmiş
/// <see cref="MergeCustomerIdentities.RunAsync(LicenseDbContext, Guid?, bool, TextWriter, TextWriter, Microsoft.Extensions.Logging.ILoggerFactory, CancellationToken)"/>'ten
/// girilir; argüman ayrıştırması <see cref="MergeCustomerIdentitiesTests"/>'te.
/// </summary>
[Collection(SqlServerCollection.Name)]
[Trait("Category", "Testcontainers")]
public sealed class MergeCustomerIdentitiesRelationalTests : IAsyncLifetime
{
    private readonly SqlServerContainerFixture _sql;
    private string _cs = null!;
    private RelationalApiFactory _factory = null!;
    public MergeCustomerIdentitiesRelationalTests(SqlServerContainerFixture sql) => _sql = sql;

    public async Task InitializeAsync()
    {
        _cs = await _sql.CreateDatabaseAsync();
        _factory = new RelationalApiFactory(_cs); // ilk kapsamda göçleri koşturur
    }

    public Task DisposeAsync() { _factory.Dispose(); return Task.CompletedTask; }

    private sealed record Seed(Guid LicenseId, Guid Canonical, Guid Copy, string[] Personal);

    /// <summary>Bir lisans ve iki kopyalı bir kişi; kişisel değerler çıktı
    /// denetimi için döner. Hepsi üretilmiş.</summary>
    private async Task<Seed> SeedPersonAsync(decimal copyBalance = 0m)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var customer = new Customer
        {
            Id = Guid.NewGuid(), Email = $"musteri-{Guid.NewGuid():N}@example.test", Name = "CLI Testi",
            PasswordHash = $"h-{Guid.NewGuid():N}", CreatedAt = DateTimeOffset.UtcNow,
        };
        var license = new License
        {
            Id = Guid.NewGuid(), CustomerId = customer.Id, LicenseKey = "mci-" + Guid.NewGuid().ToString("N")[..12],
            SkuCode = "STD", ActivationSlots = 1, IssuedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddYears(1),
        };
        db.Customers.Add(customer);
        db.Licenses.Add(license);
        var username = "u" + Guid.NewGuid().ToString("N")[..10];
        var fullName = "Ad " + Guid.NewGuid().ToString("N")[..8];
        var phone = "+9055" + Random.Shared.Next(10_000_000, 99_999_999);
        var address = "Adres " + Guid.NewGuid().ToString("N")[..8];
        var t0 = DateTimeOffset.UtcNow.AddDays(-10);
        var canonical = new WpfCustomerProjection
        {
            Id = Guid.NewGuid(), LicenseId = license.Id, Platform = "instagram", Username = username,
            FullName = fullName, UpdatedAt = t0,
        };
        var copy = new WpfCustomerProjection
        {
            Id = Guid.NewGuid(), LicenseId = license.Id, Platform = "instagram", Username = username.ToUpperInvariant(),
            Phone = phone, Address = address, UpdatedAt = t0.AddDays(1),
        };
        db.WpfCustomerProjections.AddRange(canonical, copy);
        db.Orders.Add(new Order
        {
            Id = Guid.NewGuid(), LicenseId = license.Id, CustomerId = canonical.Id.ToString("N"), Platform = "instagram",
            Username = username, MessageText = "A1", Price = 10, AddedAt = t0, UpdatedAt = t0,
        });
        if (copyBalance != 0m)
            db.CustomerBalances.Add(new CustomerBalance
                { Id = Guid.NewGuid(), LicenseId = license.Id, WpfCustomerId = copy.Id, Balance = copyBalance, UpdatedAt = t0 });
        await db.SaveChangesAsync();
        return new Seed(license.Id, canonical.Id, copy.Id, [username, fullName, phone, address]);
    }

    private LicenseDbContext NewDb(params Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor[] interceptors)
        => new(new DbContextOptionsBuilder<LicenseDbContext>().UseSqlServer(_cs).AddInterceptors(interceptors).Options);

    private static async Task<(int Exit, string Text)> RunCliAsync(LicenseDbContext db, Guid? license, bool apply)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await MergeCustomerIdentities.RunAsync(db, license, apply, output, error, NullLoggerFactory.Instance, default);
        return (exit, output + Environment.NewLine + error);
    }

    private static void ShouldHoldNoPersonalData(string text, Seed seed)
    {
        foreach (var value in seed.Personal)
            text.Should().NotContainEquivalentOf(value, "çıktıda yalnız sayılar ve lisans Id'leri olur");
    }

    [Fact]
    public async Task Kuru_calistirma_0_doner_yazmaz_ciktida_yalniz_sayilar_ve_lisans_Idsi()
    {
        var seed = await SeedPersonAsync();
        await using var db = NewDb();

        var (exit, text) = await RunCliAsync(db, license: null, apply: false);

        exit.Should().Be(0);
        text.Should().Contain("KURU ÇALIŞTIRMA").And.Contain(seed.LicenseId.ToString())
            .And.Contain("kopyalı kişi=1 (instagram=1)").And.Contain("harf/boşluk farklı=1")
            .And.Contain("B1 kapısı");
        ShouldHoldNoPersonalData(text, seed);
        db.ChangeTracker.Clear();
        (await db.WpfCustomerProjections.IgnoreQueryFilters().CountAsync(p => p.MergedIntoId != null)).Should().Be(0);
    }

    [Fact]
    public async Task Uygulama_0_doner_B1_kapisi_sifir_ciktida_kisisel_veri_yok()
    {
        var seed = await SeedPersonAsync(copyBalance: 30m);
        await using var db = NewDb();

        var (exit, text) = await RunCliAsync(db, seed.LicenseId, apply: true);

        exit.Should().Be(0, text);
        text.Should().Contain("UYGULANDI");
        ShouldHoldNoPersonalData(text, seed);
        db.ChangeTracker.Clear();
        (await db.WpfCustomerProjections.IgnoreQueryFilters().SingleAsync(p => p.Id == seed.Copy))
            .MergedIntoId.Should().Be(seed.Canonical);
    }

    [Fact]
    public async Task B1_kapisi_tutmazsa_3_doner()
    {
        // Hesaplanan anahtarı boş ikizleri iş bilerek birleştirmez; B1'in SQL
        // kapısı onları aynı grupta görür — göç bu hâlde düşerdi.
        var seed = await SeedPersonAsync();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            db.WpfCustomerProjections.AddRange(
                new WpfCustomerProjection { Id = Guid.NewGuid(), LicenseId = seed.LicenseId, Platform = "tiktok", Username = "   ", UpdatedAt = DateTimeOffset.UtcNow },
                new WpfCustomerProjection { Id = Guid.NewGuid(), LicenseId = seed.LicenseId, Platform = "tiktok", Username = " ", UpdatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }
        await using var cliDb = NewDb();

        var (exit, text) = await RunCliAsync(cliDb, license: null, apply: true);

        exit.Should().Be(3, text);
        text.Should().Contain("SON KOŞUL TUTMADI");
    }

    /// <summary>
    /// Son koşul: zincir (kopyanın kopyası) yok. Zincir bir yarışın izidir
    /// (--apply sırasında bir bilgisayar açık kaldı); iş yalnız birleştirdiği
    /// grubu düzleştirir. Kuru çalıştırma sayıyı yazar, uygulama 3 döner.
    /// </summary>
    [Fact]
    public async Task Zincir_varsa_kuru_calistirma_yazar_uygulama_3_doner()
    {
        var seed = await SeedPersonAsync();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var root = new WpfCustomerProjection
                { Id = Guid.NewGuid(), LicenseId = seed.LicenseId, Platform = "tiktok", Username = "zincir", UpdatedAt = DateTimeOffset.UtcNow };
            var middle = new WpfCustomerProjection
                { Id = Guid.NewGuid(), LicenseId = seed.LicenseId, Platform = "tiktok", Username = "Zincir", MergedIntoId = root.Id, UpdatedAt = DateTimeOffset.UtcNow };
            var tail = new WpfCustomerProjection
                { Id = Guid.NewGuid(), LicenseId = seed.LicenseId, Platform = "tiktok", Username = "ZINCIR", MergedIntoId = middle.Id, UpdatedAt = DateTimeOffset.UtcNow };
            db.WpfCustomerProjections.AddRange(root, middle, tail);
            await db.SaveChangesAsync();
        }

        await using (var dryDb = NewDb())
        {
            var (dryExit, dryText) = await RunCliAsync(dryDb, license: null, apply: false);
            dryExit.Should().Be(0);
            dryText.Should().Contain("Zincir (kopyanın kopyası, SQL, tüm lisanslar): 1");
        }

        await using var cliDb = NewDb();
        var (exit, text) = await RunCliAsync(cliDb, license: null, apply: true);

        exit.Should().Be(3, text);
        text.Should().Contain("SON KOŞUL TUTMADI").And.Contain("zincir");
        ShouldHoldNoPersonalData(text, seed);
    }

    [Fact]
    public async Task Atlanan_grup_varsa_1_doner()
    {
        var seed = await SeedPersonAsync(copyBalance: 30m);
        var hook = new SaveHookInterceptor();
        await using var hooked = NewDb(hook);
        var fired = false;
        hook.BeforeSave = async () =>
        {
            // Eşzamanlı bakiye uygulaması asıl kaydın satırını tam o anda açar → 2601.
            if (fired || !hooked.ChangeTracker.Entries<CustomerBalance>()
                    .Any(e => e.State == EntityState.Added && e.Entity.WpfCustomerId == seed.Canonical)) return;
            fired = true;
            await using var conn = new SqlConnection(_cs);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO CustomerBalances (Id, LicenseId, WpfCustomerId, Balance, UpdatedAt) VALUES (@id, @lic, @wpf, 5, SYSDATETIMEOFFSET())";
            cmd.Parameters.AddWithValue("@id", Guid.NewGuid());
            cmd.Parameters.AddWithValue("@lic", seed.LicenseId);
            cmd.Parameters.AddWithValue("@wpf", seed.Canonical);
            await cmd.ExecuteNonQueryAsync();
        };

        var (exit, text) = await RunCliAsync(hooked, seed.LicenseId, apply: true);

        fired.Should().BeTrue();
        exit.Should().Be(1, "atlanan grup yeniden koşuyla biter; kalan kopyalar B1 kapısında görünse de çıkış 1");
        text.Should().Contain("atlanan grup=1");
        ShouldHoldNoPersonalData(text, seed);
    }
}
