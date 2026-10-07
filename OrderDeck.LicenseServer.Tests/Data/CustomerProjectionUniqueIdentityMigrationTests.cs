using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.CustomerSync;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Data;

/// <summary>
/// CustomerProjectionUniqueIdentity göçü (B1 — kişi başına tek asıl kayıt)
/// gerçek SQL Server'da, DOLU bir tabloya karşı: hedeften bir önceki göçe
/// kadar kur → eski şemaya satır yaz → yalnız hedefi uygula
/// (<see cref="CustomerProjectionFullSyncMigrationTests"/> deseni).
///
/// <para>Göçün iki kapısı var ve prod açılışını durdurabilen tek şey onlar:
/// kopyalı asıl kayıt (birleştirme koşmamış) ya da onarılmamış kimlik anahtarı
/// (NEWID varsayılanı). İkisi de OKUNUR bir mesajla düşmeli ve HİÇBİR şeyi
/// değiştirmemeli — göç kayda geçmez, eski indeks yerinde kalır, deploy'un
/// otomatik geri alması önceki imaja temiz döner.</para>
/// </summary>
[Collection(SqlServerCollection.Name)]
[Trait("Category", "Testcontainers")]
public sealed class CustomerProjectionUniqueIdentityMigrationTests
{
    private const string OldIndex = "IX_WpfCustomerProjections_LicenseId_Platform_IdentityKey";

    private readonly SqlServerContainerFixture _sql;

    public CustomerProjectionUniqueIdentityMigrationTests(SqlServerContainerFixture sql) => _sql = sql;

    /// <summary>B1'den bir önceki göçe kadar kurulmuş veritabanı ve bir lisans.</summary>
    private sealed class PreviousSchema : IAsyncDisposable
    {
        public required string ConnectionString { get; init; }
        public required LicenseDbContext Db { get; init; }
        public required IMigrator Migrator { get; init; }
        public required string Target { get; init; }
        public required Guid LicenseId { get; init; }

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    private async Task<PreviousSchema> PreviousSchemaAsync()
    {
        var cs = await _sql.CreateDatabaseAsync();
        var db = new LicenseDbContext(new DbContextOptionsBuilder<LicenseDbContext>().UseSqlServer(cs).Options);
        var migrator = db.GetService<IMigrator>();
        var all = db.Database.GetMigrations().ToList();
        var target = all.Single(m => m.EndsWith("_CustomerProjectionUniqueIdentity"));
        await migrator.MigrateAsync(all[all.IndexOf(target) - 1]);

        // B1 kolon eklemiyor (yalnız indeks): bugünkü model eski şemaya yazabilir.
        var licenseId = await NewLicenseAsync(db);
        return new PreviousSchema
        {
            ConnectionString = cs, Db = db, Migrator = migrator, Target = target, LicenseId = licenseId,
        };
    }

    private static async Task<Guid> NewLicenseAsync(LicenseDbContext db)
    {
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            Email = $"musteri-{Guid.NewGuid():N}@example.test",
            Name = "B1 Göç Testi",
            PasswordHash = $"h-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        var license = new License
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            LicenseKey = "b1m-" + Guid.NewGuid().ToString("N")[..12],
            SkuCode = "STD",
            ActivationSlots = 1,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddYears(1),
        };
        db.Customers.Add(customer);
        db.Licenses.Add(license);
        await db.SaveChangesAsync();
        return license.Id;
    }

    private static WpfCustomerProjection Row(
        Guid licenseId, string username, string platform = "tiktok", Guid? mergedInto = null) => new()
    {
        Id = Guid.NewGuid(), LicenseId = licenseId, Platform = platform, Username = username,
        MergedIntoId = mergedInto, UpdatedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task Kopyasiz_veriyle_gecer_ve_kurali_veritabani_koyar()
    {
        await using var s = await PreviousSchemaAsync();
        var otherLicense = await NewLicenseAsync(s.Db);
        var head = Row(s.LicenseId, "ayse");
        s.Db.WpfCustomerProjections.AddRange(
            head,
            Row(s.LicenseId, "AYSE", mergedInto: head.Id), // kopya aynı kimliği taşır — filtre dışı
            Row(s.LicenseId, "ayse", platform: "instagram"), // başka platform: başka kişi
            Row(otherLicense, "ayse"));                      // başka lisans: başka kişi
        await s.Db.SaveChangesAsync();

        await s.Migrator.MigrateAsync(s.Target);

        await using (var conn = new SqlConnection(s.ConnectionString))
        {
            await conn.OpenAsync();
            var index = await IndexAsync(conn, CustomerIdentityIndex.Name);
            index.Should().NotBeNull("göç tekil indeksi kurar");
            index!.Value.IsUnique.Should().BeTrue();
            index.Value.Filter.Should().Contain("[MergedIntoId] IS NULL").And.Contain("[IdentityKey]<>N''");
            (await IndexAsync(conn, OldIndex)).Should().BeNull("aynı kolonlardaki tekil olmayan indeksin yerini aldı");
        }

        // Kuralı artık veritabanı koyuyor: aynı kimliğe ikinci asıl kayıt
        // reddedilir (uygulamanın tanıdığı ihlal), kopya ise eklenebilir.
        s.Db.ChangeTracker.Clear();
        s.Db.WpfCustomerProjections.Add(Row(s.LicenseId, " Ayse "));
        var duplicate = async () => await s.Db.SaveChangesAsync();
        (await duplicate.Should().ThrowAsync<DbUpdateException>())
            .Which.Should().Match<DbUpdateException>(e => CustomerIdentityIndex.IsViolation(e));

        s.Db.ChangeTracker.Clear();
        s.Db.WpfCustomerProjections.Add(Row(s.LicenseId, "Ayse", mergedInto: head.Id));
        await s.Db.SaveChangesAsync();
    }

    [Fact]
    public async Task Kopyali_asil_kayit_varsa_okunur_mesajla_duser_hicbir_sey_degismez()
    {
        await using var s = await PreviousSchemaAsync();
        s.Db.WpfCustomerProjections.AddRange(Row(s.LicenseId, "mehmet"), Row(s.LicenseId, "Mehmet"));
        await s.Db.SaveChangesAsync();

        var act = async () => await s.Migrator.MigrateAsync(s.Target);

        (await act.Should().ThrowAsync<SqlException>()).Which.Should().Match<SqlException>(e =>
            e.Number == 50000
            && e.Message == "B1: kopyalı asıl kayıt var — önce merge-customer-identities --all --apply koşun");
        await ShouldBeUntouchedAsync(s);
    }

    [Fact]
    public async Task Onarilmamis_kimlik_anahtari_varsa_okunur_mesajla_duser_hicbir_sey_degismez()
    {
        await using var s = await PreviousSchemaAsync();
        // Geri alma penceresinde kolonu tanımayan imajın açtığı satır: IdentityKey
        // hiç verilmez, A1'in NEWID() varsayılanı düşer (büyük harfli onaltılık).
        await using (var conn = new SqlConnection(s.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO WpfCustomerProjections (Id, LicenseId, Platform, Username, UpdatedAt)
                VALUES (@id, @licenseId, 'tiktok', @username, SYSDATETIMEOFFSET())
                """;
            cmd.Parameters.AddWithValue("@id", Guid.NewGuid());
            cmd.Parameters.AddWithValue("@licenseId", s.LicenseId);
            cmd.Parameters.AddWithValue("@username", "u" + Guid.NewGuid().ToString("N")[..10]);
            await cmd.ExecuteNonQueryAsync();
        }

        var act = async () => await s.Migrator.MigrateAsync(s.Target);

        (await act.Should().ThrowAsync<SqlException>()).Which.Should().Match<SqlException>(e =>
            e.Number == 50000
            && e.Message == "B1: onarılmamış kimlik anahtarı var — önce identity-key-repair koşun (PR-1 imajı açılışta koşar)");
        await ShouldBeUntouchedAsync(s);
    }

    [Fact]
    public async Task Bos_anahtarli_iki_asil_kayit_indeksi_engellemez()
    {
        // Yalnız boşluktan oluşan kullanıcı adının anahtarı boş: kimlik değil
        // (onarım işi boş anahtar yazmaz, birleştirme onları gruplamaz).
        await using var s = await PreviousSchemaAsync();
        s.Db.WpfCustomerProjections.AddRange(Row(s.LicenseId, "   "), Row(s.LicenseId, " "));
        await s.Db.SaveChangesAsync();

        await s.Migrator.MigrateAsync(s.Target);

        (await s.Db.Database.GetAppliedMigrationsAsync()).Should().Contain(s.Target);
        s.Db.ChangeTracker.Clear();
        s.Db.WpfCustomerProjections.Add(Row(s.LicenseId, "  "));
        await s.Db.SaveChangesAsync(); // üçüncü boş anahtarlı asıl kayıt da serbest
    }

    /// <summary>Kapı düştü: göç kayda geçmedi, şema B1 öncesi gibi.</summary>
    private static async Task ShouldBeUntouchedAsync(PreviousSchema s)
    {
        (await s.Db.Database.GetAppliedMigrationsAsync()).Should().NotContain(s.Target);
        await using var conn = new SqlConnection(s.ConnectionString);
        await conn.OpenAsync();
        (await IndexAsync(conn, CustomerIdentityIndex.Name)).Should().BeNull();
        (await IndexAsync(conn, OldIndex)).Should().NotBeNull("kapı ilk adım: eski indekse dokunulmadı");
    }

    private static async Task<(bool IsUnique, string Filter)?> IndexAsync(SqlConnection conn, string name)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT is_unique, ISNULL(filter_definition, N'')
            FROM sys.indexes
            WHERE object_id = OBJECT_ID(N'WpfCustomerProjections') AND name = @name
            """;
        cmd.Parameters.AddWithValue("@name", name);
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;
        return (reader.GetBoolean(0), reader.GetString(1));
    }
}
