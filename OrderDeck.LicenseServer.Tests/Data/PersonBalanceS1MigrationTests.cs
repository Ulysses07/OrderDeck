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
[Collection(SqlServerExpressCollection.Name)]
[Trait("Category", "Testcontainers")]
public sealed class PersonBalanceS1MigrationExpressTests
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
            .Should().BeFalse("bir grubun bütün satırları aynı DrawGroupId'yi taşır");
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

    /// <summary>Toplu projeksiyon ve hareket sayısı. Göçün iki offline indeks kurulumu ve harman değişimi satır
    /// sayısıyla büyür; süre bu sayılarla günlüğe yazılır — ölçüm, iddia değil (emsal
    /// <see cref="SalesSyncS1MigrationScenario.BulkOrderCount"/>). Prod sayıları Burak'ın salt okunur sorgusundan gelir
    /// (spec §7.1 adım 0); sınıra yakınsa iki indeks ayrı bir göçe bölünür.</summary>
    public const int BulkProjectionCount = 30_000;

    /// <inheritdoc cref="BulkProjectionCount"/>
    public const int BulkTransactionCount = 30_000;

    /// <summary>S1 öncesi imajın <c>CustomerBalanceTransactions</c>'a yazdığı TAM kolon listesi. Senaryo bunu göç
    /// öncesi şemayla karşılaştırır: liste eksik kalırsa "eski imaj yazabiliyor" iddiası boşa düşerdi.</summary>
    internal static readonly string[] PreS1TransactionColumns =
    [
        "Id", "LicenseId", "WpfCustomerId", "Amount", "Kind", "OriginalAmount", "ShippingDeducted", "Reason",
        "ReversesTransactionId", "SaleScope", "CreatedByCustomerId", "CreatedAt",
    ];

    private sealed record IndexInfo(string KeyColumns, string? Filter, bool IsUnique);

    private sealed record ForeignKeyInfo(string Parent, string Referenced, string OnDelete);

    private sealed record StatInfo(string Name, bool AutoCreated, bool UserCreated);

    /// <summary>GroupId'ye bağlı, indekse ait olmayan istatistikler — göçün düşürdüğü küme (otomatik ve kullanıcı;
    /// <c>PersonBalanceS1.DropGroupIdStatistics</c> ile aynı süzgeç).</summary>
    private const string StatsOnGroupIdSql = """
        SELECT s.name, s.auto_created, s.user_created FROM sys.stats AS s
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
        var (_, bulkLicenseId) = await NewLicenseAsync(conn);
        await SeedBulkAsync(conn, bulkLicenseId, customerId);

        // GroupId'de iki tür istatistik (spec §3.1, §8.2-11) ve göç ikisi varken de koşmalı. Kullanıcı istatistiği
        // ALTER COLUMN'u engeller (Msg 5074, göçün düşürmesi şart); otomatik istatistiği ALTER COLUMN kendisi düşürür.
        // Sıra önemli: sütunda istatistik varken optimizer onu kullanır, otomatik istatistik açmaz. Önce istatistiksiz
        // sütunda GroupId yüklemli bir sorgu otomatik istatistiği (_WA_Sys_*) açtırır, kullanıcı istatistiği ondan sonra.
        (await StatsOnGroupIdAsync(conn)).Should().BeEmpty("göç öncesi şemada GroupId'de istatistik yok");
        await ExecAsync(conn,
            "SELECT LicenseId, COUNT(*) FROM dbo.WpfCustomerProjections WHERE GroupId = N'grup1' GROUP BY LicenseId;");
        (await StatsOnGroupIdAsync(conn)).Should().Contain(s => s.AutoCreated,
            "GroupId yüklemi istatistiksiz sütunda otomatik istatistik açtırır");
        await ExecAsync(conn, "CREATE STATISTICS st_GroupId_test ON dbo.WpfCustomerProjections (GroupId);");
        var statsBefore = await StatsOnGroupIdAsync(conn);
        statsBefore.Should().Contain(s => s.AutoCreated).And.Contain(s => s.UserCreated);
        output.WriteLine("GroupId istatistikleri (göç öncesi): " + string.Join(", ",
            statsBefore.Select(s => $"{s.Name} ({(s.AutoCreated ? "otomatik" : "kullanıcı")})")));

        var projections = await ScalarAsync<int>(conn, "SELECT COUNT(*) FROM WpfCustomerProjections");
        var transactions = await ScalarAsync<int>(conn, "SELECT COUNT(*) FROM CustomerBalanceTransactions");
        var watch = Stopwatch.StartNew();
        await migrator.MigrateAsync(target);
        watch.Stop();
        output.WriteLine(
            $"PersonBalanceS1 göçü: {await ScalarAsync<string>(conn, "SELECT CAST(SERVERPROPERTY('Edition') AS nvarchar(128))")}, " +
            $"WpfCustomerProjections={projections}, CustomerBalanceTransactions={transactions} → {watch.ElapsedMilliseconds} ms");

        (await db.Database.GetAppliedMigrationsAsync()).Should().Contain(target);
        (await StatsOnGroupIdAsync(conn)).Select(s => s.Name).Should().NotIntersectWith(
            statsBefore.Select(s => s.Name), "göç öncesi GroupId istatistiklerinin (otomatik ve kullanıcı) hiçbiri kalmaz");

        // §3.1 satır 1–2: DrawGroupId + filtreli, tekil olmayan indeks.
        (await ColumnAsync(conn, "CustomerBalanceTransactions", "DrawGroupId"))
            .Should().Be(("uniqueidentifier", true, (string?)null));
        (await IndexAsync(conn, "CustomerBalanceTransactions", DrawIndex))
            .Should().Be(new IndexInfo("DrawGroupId", "([DrawGroupId] IS NOT NULL)", false));

        // §3.1 satır 3–4: GroupId BIN2, tür/uzunluk/boşluk aynı; (LicenseId, GroupId) filtreli indeks.
        (await ColumnAsync(conn, "WpfCustomerProjections", "GroupId"))
            .Should().Be(("nvarchar", true, Bin2));
        (await ScalarAsync<short>(conn,
                "SELECT max_length FROM sys.columns WHERE object_id = OBJECT_ID(N'WpfCustomerProjections') AND name = N'GroupId'"))
            .Should().Be(128, "nvarchar(64) = 128 bayt");
        (await IndexAsync(conn, "WpfCustomerProjections", GroupIndex))
            .Should().Be(new IndexInfo("LicenseId,GroupId", "([MergedIntoId] IS NULL AND [GroupId] IS NOT NULL)", false));

        // §3.1 satır 5–6: bayrak kolonu ve taban tablosu.
        (await ColumnAsync(conn, "Licenses", "PersonBalancePoolSince"))
            .Should().Be(("datetimeoffset", true, (string?)null));
        (await ColumnsAsync(conn, "PersonBalanceBaselines")).Should().BeEquivalentTo(
            ["LicenseId", "WpfCustomerId", "Balance", "LedgerSum", "CapturedAt", "ReviewedAt"]);
        (await IndexAsync(conn, "PersonBalanceBaselines", "PK_PersonBalanceBaselines"))
            .Should().Be(new IndexInfo("LicenseId,WpfCustomerId", null, true));
        (await ForeignKeysAsync(conn, "PersonBalanceBaselines")).Should().BeEquivalentTo(
            [new ForeignKeyInfo("PersonBalanceBaselines", "Licenses", "CASCADE")],
            "lisans silinince taban da gider; projeksiyona FK yok (WpfCustomerId = Guid.Empty lisans işaret satırı)");

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

    /// <summary>
    /// Toplu satırlar ayrı bir lisansa, küme tabanlı tek INSERT'lerle (satır satır gidiş-dönüş yok). Metinler kişisel
    /// veri değil: sentetik kullanıcı adı ve grup numarası. Projeksiyonların üçte biri gruplu, onda biri kopya
    /// (yönlendirme; <c>MergedIntoId</c>'ye öz-FK yok, hedef bu ölçümde önemsiz) — ikisi de yeni indeksin süzgecine
    /// girer. Hareketler asıl kayıtlara dağılır, dörtte biri kapsamlı düşüm.
    /// </summary>
    private static async Task SeedBulkAsync(SqlConnection conn, Guid licenseId, Guid customerId)
    {
        var now = DateTimeOffset.UtcNow;
        await ExecAsync(conn, """
            WITH n AS (
                SELECT TOP (@count) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS i
                FROM sys.all_objects a CROSS JOIN sys.all_objects b)
            INSERT INTO WpfCustomerProjections
                (Id, LicenseId, Platform, Username, IdentityKey, GroupId, MergedIntoId, UpdatedAt,
                 CreatedByShopper, IsBlacklisted, RecipientPaysActive, SmsConsent, WhatsAppConsent)
            SELECT NEWID(), @licenseId, N'tiktok', CONCAT(N'kullanici', i), CONCAT(N'kullanici', i),
                   CASE WHEN i % 3 = 0 THEN CONCAT(N'grup', i % 4000) END,
                   CASE WHEN i % 10 = 0 THEN NEWID() END,
                   @now, 0, 0, 0, 0, 0
            FROM n
            """, ("@count", BulkProjectionCount), ("@licenseId", licenseId), ("@now", now));

        var canonical = await ScalarAsync<int>(conn,
            "SELECT COUNT(*) FROM WpfCustomerProjections WHERE LicenseId = @licenseId AND MergedIntoId IS NULL",
            ("@licenseId", licenseId));
        await ExecAsync(conn, $"""
            WITH p AS (
                SELECT Id, ROW_NUMBER() OVER (ORDER BY Id) AS k
                FROM WpfCustomerProjections WHERE LicenseId = @licenseId AND MergedIntoId IS NULL),
            n AS (
                SELECT TOP (@count) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS i
                FROM sys.all_objects a CROSS JOIN sys.all_objects b)
            INSERT INTO CustomerBalanceTransactions ({string.Join(", ", PreS1TransactionColumns)})
            SELECT NEWID(), @licenseId, p.Id,
                   CASE WHEN n.i % 4 = 0 THEN -40.00 ELSE 50.00 END,
                   CASE WHEN n.i % 4 = 0 THEN N'purchase-deduction' ELSE N'refund-full' END,
                   CASE WHEN n.i % 4 = 0 THEN 40.00 ELSE 50.00 END, NULL, NULL, NULL,
                   CASE WHEN n.i % 4 = 0 THEN CONCAT(N'session:', n.i) END,
                   @customerId, DATEADD(SECOND, n.i, @now)
            FROM n JOIN p ON p.k = (n.i - 1) % @canonical + 1
            """,
            ("@count", BulkTransactionCount), ("@licenseId", licenseId), ("@canonical", canonical),
            ("@customerId", customerId), ("@now", now));
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

    private static async Task<List<StatInfo>> StatsOnGroupIdAsync(SqlConnection conn)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = StatsOnGroupIdSql;
        var stats = new List<StatInfo>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            stats.Add(new StatInfo(reader.GetString(0), reader.GetBoolean(1), reader.GetBoolean(2)));
        return stats;
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

    /// <summary>İndeksin anahtar kolonları (key_ordinal sırasıyla, virgülle), süzgeci ve tekilliği; yoksa null.</summary>
    private static async Task<IndexInfo?> IndexAsync(SqlConnection conn, string table, string name)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT STRING_AGG(c.name, ',') WITHIN GROUP (ORDER BY ic.key_ordinal), i.filter_definition, i.is_unique
            FROM sys.indexes i
            JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.key_ordinal > 0
            JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE i.object_id = OBJECT_ID(@t) AND i.name = @n
            GROUP BY i.filter_definition, i.is_unique
            """;
        cmd.Parameters.AddWithValue("@t", table);
        cmd.Parameters.AddWithValue("@n", name);
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;
        return new IndexInfo(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetBoolean(2));
    }

    private static async Task<List<ForeignKeyInfo>> ForeignKeysAsync(SqlConnection conn, string table)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT OBJECT_NAME(parent_object_id), OBJECT_NAME(referenced_object_id), delete_referential_action_desc
            FROM sys.foreign_keys WHERE parent_object_id = OBJECT_ID(@t)
            """;
        cmd.Parameters.AddWithValue("@t", table);
        var keys = new List<ForeignKeyInfo>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            keys.Add(new ForeignKeyInfo(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        return keys;
    }

    private static async Task ExecAsync(SqlConnection conn, string sql, params (string Name, object Value)[] parameters)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<T> ScalarAsync<T>(SqlConnection conn, string sql, params (string Name, object Value)[] parameters)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        return (T)(await cmd.ExecuteScalarAsync())!;
    }
}
