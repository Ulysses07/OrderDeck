using System.Diagnostics;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;
using Xunit.Abstractions;

namespace OrderDeck.LicenseServer.Tests.Data;

/// <summary>
/// PersonBalanceS1 göçü (ortak bakiye S1, spec 2026-10-11 §3.1, §8.2-11) gerçek SQL Server'da, DOLU tablolara karşı:
/// hedeften bir önceki göçe kadar kur → eski şemaya satır yaz (+ GroupId'de istatistik) → yalnız hedefi uygula.
/// Deploy'un otomatik geri alması göçü geri ALMAZ; S1 öncesi imajın yazdığı kolon listeleri yeni şemada çalışmalı.
/// </summary>
[Collection(SqlServerCollection.Name)]
[Trait("Category", "Testcontainers")]
public sealed class PersonBalanceS1MigrationTests
{
    private readonly SqlServerContainerFixture _sql;
    private readonly ITestOutputHelper _output;

    public PersonBalanceS1MigrationTests(SqlServerContainerFixture sql, ITestOutputHelper output)
    {
        _sql = sql;
        _output = output;
    }

    [Fact]
    public async Task Gocu_istatistik_varken_uygular_harman_indeks_ve_tablo_kurar_eski_imaj_yazmayi_surdurur()
        => await PersonBalanceS1MigrationScenario.RunAsync(await _sql.CreateDatabaseAsync(), _output);
}

/// <summary>Aynı senaryo prod'un sürümünde: SQL Server Express (ONLINE indeks işlemi yok, spec §3.1, §8.2-11).</summary>
[Trait("Category", "Testcontainers")]
public sealed class PersonBalanceS1MigrationExpressTests : IClassFixture<SqlServerExpressContainerFixture>
{
    private readonly SqlServerExpressContainerFixture _sql;
    private readonly ITestOutputHelper _output;

    public PersonBalanceS1MigrationExpressTests(SqlServerExpressContainerFixture sql, ITestOutputHelper output)
    {
        _sql = sql;
        _output = output;
    }

    [Fact]
    public async Task Goc_Express_surumunde_kosar()
    {
        var connectionString = await _sql.CreateDatabaseAsync();
        await using (var conn = new SqlConnection(connectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT CAST(SERVERPROPERTY('Edition') AS nvarchar(128))";
            ((string)(await cmd.ExecuteScalarAsync())!).Should().StartWith("Express",
                "test prod'un sürümünde koşmalı, yoksa kanıt değildir");
        }

        await PersonBalanceS1MigrationScenario.RunAsync(connectionString, _output);
    }
}

/// <summary>Göç betiğinin biçimi — bağlantı açmaz, Docker'sız koşar.</summary>
public sealed class PersonBalanceS1MigrationScriptTests
{
    [Fact]
    public void Goc_betigi_ONLINE_ve_veri_yazimi_icermez_istatistigi_harmandan_once_duser()
    {
        using var db = new LicenseDbContext(new DbContextOptionsBuilder<LicenseDbContext>()
            .UseSqlServer("Server=betik-uretimi;Database=yok").Options);
        var (previous, target) = PersonBalanceS1MigrationScenario.Migrations(db);

        var script = db.GetService<IMigrator>().GenerateScript(previous, target);

        Regex.IsMatch(script, @"\bONLINE\b", RegexOptions.IgnoreCase)
            .Should().BeFalse("Express'te ONLINE indeks işlemi yok (spec §3.1)");
        script.Split('\n')
            .Where(line => Regex.IsMatch(line, @"^\s*(INSERT|UPDATE|DELETE|MERGE)\b", RegexOptions.IgnoreCase))
            .Where(line => !line.Contains("__EFMigrationsHistory", StringComparison.Ordinal))
            .Should().BeEmpty("S1 göçü veri yazmaz, geri doldurma yok (spec §3.1)");

        var drop = script.IndexOf("DROP STATISTICS", StringComparison.Ordinal);
        var alter = Regex.Match(script,
            @"ALTER COLUMN \[GroupId\] nvarchar\(64\) COLLATE " + PersonBalanceS1MigrationScenario.Bin2);
        drop.Should().BeGreaterThan(-1, "GroupId'ye bağlı istatistik ALTER COLUMN'u engelleyebilir");
        alter.Success.Should().BeTrue("GroupId BIN2'ye alınır (M5)");
        alter.Index.Should().BeGreaterThan(drop, "istatistik harman değişiminden ÖNCE düşer");

        script.Should().Contain($"CREATE INDEX [{PersonBalanceS1MigrationScenario.GroupIndex}]");
        script.Should().Contain($"CREATE INDEX [{PersonBalanceS1MigrationScenario.DrawIndex}]");
        script.Should().NotContain($"DROP INDEX [{PersonBalanceS1MigrationScenario.GroupIndex}]",
            "henüz olmayan indeks harman değişiminde düşürülmeye çalışılmamalı");
        Regex.IsMatch(script, $@"CREATE\s+UNIQUE\s+INDEX\s+\[{PersonBalanceS1MigrationScenario.DrawIndex}\]")
            .Should().BeFalse("bacak indeksi tekil olamaz: birleştirme aynı grubun iki bacağını aynı satıra taşıyabilir");
        script.Should().Contain("CREATE TABLE [PersonBalanceBaselines]");
        script.Should().Contain("[PersonBalancePoolSince] datetimeoffset NULL");
    }
}

/// <summary>Senaryo testten ayrı: aynı adımlar Express'e karşı da koşar.</summary>
internal static class PersonBalanceS1MigrationScenario
{
    public const string Bin2 = "Latin1_General_100_BIN2";
    public const string GroupIndex = "IX_WpfCustomerProjections_LicenseId_GroupId";
    public const string DrawIndex = "IX_CustomerBalanceTransactions_DrawGroupId";

    /// <summary>S1 öncesi imajın <c>CustomerBalanceTransactions</c>'a yazdığı TAM kolon listesi. Senaryo bunu göç
    /// öncesi şemayla karşılaştırır: liste eksik kalırsa "eski imaj yazabiliyor" iddiası boşa düşerdi.</summary>
    internal static readonly string[] PreS1TransactionColumns =
    [
        "Id", "LicenseId", "WpfCustomerId", "Amount", "Kind", "OriginalAmount", "ShippingDeducted", "Reason",
        "ReversesTransactionId", "SaleScope", "CreatedByCustomerId", "CreatedAt",
    ];

    private const string StatsOnGroupIdSql = """
        SELECT COUNT(*) FROM sys.stats AS s
        WHERE s.object_id = OBJECT_ID(N'[dbo].[WpfCustomerProjections]')
          AND (s.auto_created = 1 OR s.user_created = 1)
          AND EXISTS (SELECT 1 FROM sys.stats_columns AS sc
                      JOIN sys.columns AS c ON c.object_id = sc.object_id AND c.column_id = sc.column_id
                      WHERE sc.object_id = s.object_id AND sc.stats_id = s.stats_id AND c.name = N'GroupId')
        """;

    public static (string Previous, string Target) Migrations(LicenseDbContext db)
    {
        var all = db.Database.GetMigrations().ToList();
        var target = all.Single(m => m.EndsWith("_PersonBalanceS1", StringComparison.Ordinal));
        return (all[all.IndexOf(target) - 1], target);
    }

    public static async Task RunAsync(string connectionString, ITestOutputHelper output)
    {
        await using var db = new LicenseDbContext(new DbContextOptionsBuilder<LicenseDbContext>()
            .UseSqlServer(connectionString).Options);
        var migrator = db.GetService<IMigrator>();
        var (previous, target) = Migrations(db);
        await migrator.MigrateAsync(previous);

        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();
        (await ColumnsAsync(conn, "CustomerBalanceTransactions")).Should().BeEquivalentTo(PreS1TransactionColumns);
        var (customerId, licenseId) = await NewLicenseAsync(conn);

        // Projeksiyonlar bugünkü modelle yazılabilir: S1 bu tabloya kolon eklemez (yalnız harman ve indeks).
        var lower = Projection(licenseId, "g1");
        var upper = Projection(licenseId, "G1");
        var spaced = Projection(licenseId, "g1 ");
        db.WpfCustomerProjections.AddRange(lower, upper, spaced);
        await db.SaveChangesAsync();
        await InsertPreS1TransactionAsync(conn, licenseId, lower.Id, customerId, 50m);

        // GroupId'de istatistik. Kullanıcı istatistiği kesin oluşur; GROUP BY sorgusu motor isterse otomatik
        // istatistik de açar. İkisi de ALTER COLUMN'u engeller — göç ikisini de düşürmeli (spec §3.1).
        await ExecAsync(conn, "CREATE STATISTICS st_GroupId_test ON dbo.WpfCustomerProjections (GroupId);");
        await ExecAsync(conn,
            "SELECT GroupId, COUNT(*) FROM dbo.WpfCustomerProjections WHERE GroupId > N'' GROUP BY GroupId;");
        var statsBefore = await ScalarAsync<int>(conn, StatsOnGroupIdSql);
        statsBefore.Should().BeGreaterThan(0);
        output.WriteLine($"GroupId istatistikleri (göç öncesi): {statsBefore}");

        var watch = Stopwatch.StartNew();
        await migrator.MigrateAsync(target);
        output.WriteLine($"PersonBalanceS1 göçü: {watch.ElapsedMilliseconds} ms");

        (await db.Database.GetAppliedMigrationsAsync()).Should().Contain(target);

        // §3.1 satır 1–2: DrawGroupId + filtreli, tekil olmayan indeks.
        (await ColumnAsync(conn, "CustomerBalanceTransactions", "DrawGroupId"))
            .Should().Be(("uniqueidentifier", true, (string?)null));
        (await IndexAsync(conn, "CustomerBalanceTransactions", DrawIndex))
            .Should().Be((false, "([DrawGroupId] IS NOT NULL)"));

        // §3.1 satır 3–4: GroupId BIN2, tür/uzunluk/boşluk aynı; (LicenseId, GroupId) filtreli indeks.
        (await ColumnAsync(conn, "WpfCustomerProjections", "GroupId"))
            .Should().Be(("nvarchar", true, Bin2));
        (await ScalarAsync<short>(conn,
                "SELECT max_length FROM sys.columns WHERE object_id = OBJECT_ID(N'WpfCustomerProjections') AND name = N'GroupId'"))
            .Should().Be(128, "nvarchar(64) = 128 bayt");
        (await IndexAsync(conn, "WpfCustomerProjections", GroupIndex))
            .Should().Be((false, "([MergedIntoId] IS NULL AND [GroupId] IS NOT NULL)"));
        (await ScalarAsync<int>(conn, "SELECT COUNT(*) FROM sys.stats WHERE name = N'st_GroupId_test'"))
            .Should().Be(0, "göç istatistiği düşürdü");

        // §3.1 satır 5–6: bayrak kolonu ve taban tablosu.
        (await ColumnAsync(conn, "Licenses", "PersonBalancePoolSince"))
            .Should().Be(("datetimeoffset", true, (string?)null));
        (await ColumnsAsync(conn, "PersonBalanceBaselines")).Should().BeEquivalentTo(
            ["LicenseId", "WpfCustomerId", "Balance", "LedgerSum", "CapturedAt", "ReviewedAt"]);
        (await ScalarAsync<int>(conn, """
            SELECT COUNT(*) FROM sys.key_constraints k
            JOIN sys.index_columns ic ON ic.object_id = k.parent_object_id AND ic.index_id = k.unique_index_id
            WHERE k.parent_object_id = OBJECT_ID(N'PersonBalanceBaselines') AND k.type = 'PK'
            """)).Should().Be(2, "PK (LicenseId, WpfCustomerId)");

        // Veri yerinde; BIN2 harf farkını ayırır, sondaki boşluğu SQL '=' yine yutar (bellekteki süzgecin gerekçesi).
        (await ScalarAsync<int>(conn,
                $"SELECT COUNT(*) FROM WpfCustomerProjections WHERE LicenseId = '{licenseId}' AND GroupId = N'g1'"))
            .Should().Be(2, "'g1' ve 'g1 ' eşit (ANSI dolgu), 'G1' BIN2'de ayrı");
        (await ScalarAsync<int>(conn,
                $"SELECT COUNT(*) FROM WpfCustomerProjections WHERE LicenseId = '{licenseId}' AND GroupId = N'G1'"))
            .Should().Be(1);

        // Otomatik geri alma: S1 öncesi imaj yeni şemada yazmayı sürdürür (DrawGroupId NULL kalır).
        var oldRow = await InsertPreS1TransactionAsync(conn, licenseId, upper.Id, customerId, 20m);
        (await ScalarAsync<int>(conn,
                $"SELECT COUNT(*) FROM CustomerBalanceTransactions WHERE Id = '{oldRow}' AND DrawGroupId IS NULL"))
            .Should().Be(1);
        await NewLicenseAsync(conn); // eski kolon listesiyle Licenses INSERT'i (PersonBalancePoolSince NULL'a düşer)
    }

    private static WpfCustomerProjection Projection(Guid licenseId, string groupId) => new()
    {
        Id = Guid.NewGuid(), LicenseId = licenseId, Platform = "instagram",
        Username = $"p{Guid.NewGuid():N}", GroupId = groupId, UpdatedAt = DateTimeOffset.UtcNow,
    };

    /// <summary>Müşteri ve lisans, S1 öncesi imajın kolonlarıyla (CustomerProjectionUniqueIdentityMigrationTests yolu).</summary>
    private static async Task<(Guid CustomerId, Guid LicenseId)> NewLicenseAsync(SqlConnection conn)
    {
        var customerId = Guid.NewGuid();
        var licenseId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO Customers (Id, Email, Name, PasswordHash, AuthVersion, CreatedAt, Unsubscribed)
            VALUES (@customerId, @email, N'Ortak Bakiye Göç Testi', @ozet, 0, @now, 0);
            INSERT INTO Licenses (Id, LicenseKey, CustomerId, SkuCode, ActivationSlots, IssuedAt, ExpiresAt, ShopperAppEnabled)
            VALUES (@licenseId, @licenseKey, @customerId, 'STD', 1, @now, @expiresAt, 0);
            """;
        cmd.Parameters.AddWithValue("@customerId", customerId);
        cmd.Parameters.AddWithValue("@email", $"musteri-{Guid.NewGuid():N}@example.test");
        cmd.Parameters.AddWithValue("@ozet", $"h-{Guid.NewGuid():N}");
        cmd.Parameters.AddWithValue("@now", now);
        cmd.Parameters.AddWithValue("@licenseId", licenseId);
        cmd.Parameters.AddWithValue("@licenseKey", "pb1-" + Guid.NewGuid().ToString("N")[..12]);
        cmd.Parameters.AddWithValue("@expiresAt", now.AddYears(1));
        await cmd.ExecuteNonQueryAsync();
        return (customerId, licenseId);
    }

    private static async Task<Guid> InsertPreS1TransactionAsync(
        SqlConnection conn, Guid licenseId, Guid wpfCustomerId, Guid customerId, decimal amount)
    {
        var id = Guid.NewGuid();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            INSERT INTO CustomerBalanceTransactions ({string.Join(", ", PreS1TransactionColumns)})
            VALUES (@id, @licenseId, @wpf, @amount, N'refund-full', @amount, NULL, NULL, NULL, NULL, @customerId, @now);
            """;
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@licenseId", licenseId);
        cmd.Parameters.AddWithValue("@wpf", wpfCustomerId);
        cmd.Parameters.AddWithValue("@amount", amount);
        cmd.Parameters.AddWithValue("@customerId", customerId);
        cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow);
        await cmd.ExecuteNonQueryAsync();
        return id;
    }

    private static async Task<List<string>> ColumnsAsync(SqlConnection conn, string table)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = @t";
        cmd.Parameters.AddWithValue("@t", table);
        var names = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) names.Add(reader.GetString(0));
        return names;
    }

    private static async Task<(string Type, bool Nullable, string? Collation)> ColumnAsync(
        SqlConnection conn, string table, string column)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT DATA_TYPE, IS_NULLABLE, COLLATION_NAME FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_NAME = @t AND COLUMN_NAME = @c
            """;
        cmd.Parameters.AddWithValue("@t", table);
        cmd.Parameters.AddWithValue("@c", column);
        await using var reader = await cmd.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue($"{table}.{column} olmalı");
        return (reader.GetString(0), reader.GetString(1) == "YES", reader.IsDBNull(2) ? null : reader.GetString(2));
    }

    private static async Task<(bool IsUnique, string Filter)?> IndexAsync(SqlConnection conn, string table, string name)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT is_unique, ISNULL(filter_definition, N'') FROM sys.indexes
            WHERE object_id = OBJECT_ID(@t) AND name = @n
            """;
        cmd.Parameters.AddWithValue("@t", table);
        cmd.Parameters.AddWithValue("@n", name);
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;
        return (reader.GetBoolean(0), reader.GetString(1));
    }

    private static async Task ExecAsync(SqlConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<T> ScalarAsync<T>(SqlConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return (T)(await cmd.ExecuteScalarAsync())!;
    }
}
