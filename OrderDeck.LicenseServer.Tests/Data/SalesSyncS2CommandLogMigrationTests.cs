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

namespace OrderDeck.LicenseServer.Tests.Data;

/// <summary>
/// SalesSyncS2CommandLogs göçü (yayın ve etiket senkronu S2, spec 2026-10-13 §3.1 S2 satırı, §4.6) gerçek SQL Server'da.
/// Göç yalnız yeni bir tablo ekler: <c>SalesCommandLogs</c> (PK <c>CommandId</c>, Licenses'a cascade FK, indeks
/// <c>(LicenseId, CreatedAt)</c>). Deploy'un otomatik geri alması göçü geri ALMAZ (spec §8.6): S2 geri alınırsa önceki
/// imaj bu şemada koşar — var olan tabloların kolonları değişmemeli, günlük satırı olan lisans silinebilmeli. Göç
/// hedeften bir önceki noktaya kadar uygulanır, sonra yalnız hedef (<see cref="SalesSyncS2MigrationTests"/> deseni).
/// </summary>
[Collection(SqlServerCollection.Name)]
[Trait("Category", "Testcontainers")]
public sealed class SalesSyncS2CommandLogMigrationTests
{
    private readonly SqlServerContainerFixture _sql;

    public SalesSyncS2CommandLogMigrationTests(SqlServerContainerFixture sql) => _sql = sql;

    private sealed record Col(string Name, string Type, bool Nullable);

    private sealed record IndexInfo(string KeyColumns, bool IsUnique);

    /// <summary>Önceki imajın yazdığı tablolar: göç bunlara dokunmamalı.</summary>
    private static readonly string[] ExistingTables =
    [
        "Licenses", "StreamSessions", "Orders", "Shipments", "WpfReplicas", "SalesLegacyTraces",
        "LicenseShippingSettings", "StockMovements",
    ];

    /// <summary>Tablonun tam kolon kümesi (spec §3.1, S2 satırı). Tür <c>INFORMATION_SCHEMA</c> biçiminde.</summary>
    private static readonly Col[] ExpectedColumns =
    [
        new("CommandId", "uniqueidentifier", false),
        new("LicenseId", "uniqueidentifier", false),
        new("ReplicaId", "uniqueidentifier", false),
        new("Kind", "nvarchar(32)", false),
        new("RequestHash", "binary(32)", false),
        new("Status", "nvarchar(16)", false),
        new("ResultJson", "nvarchar(max)", false),
        new("CreatedAt", "datetimeoffset", false),
    ];

    internal static (string Previous, string Target) Migrations(LicenseDbContext db)
    {
        var all = db.Database.GetMigrations().ToList();
        var target = all.Single(m => m.EndsWith("_SalesSyncS2CommandLogs", StringComparison.Ordinal));
        return (all[all.IndexOf(target) - 1], target);
    }

    [Fact]
    public async Task Tablo_PK_indeks_ve_cascade_FK_ile_bos_dogar_var_olan_tablolar_degismez()
    {
        var connectionString = await _sql.CreateDatabaseAsync();
        await using var db = new LicenseDbContext(
            new DbContextOptionsBuilder<LicenseDbContext>().UseSqlServer(connectionString).Options);
        var migrator = db.GetService<IMigrator>();
        var (previous, target) = Migrations(db);

        await migrator.MigrateAsync(previous);
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();

        (await ColumnsAsync(conn, ["SalesCommandLogs"]))["SalesCommandLogs"].Should().BeEmpty("göç öncesi tablo yok");
        var before = await ColumnsAsync(conn, ExistingTables);
        foreach (var table in ExistingTables)
            before[table].Should().NotBeEmpty($"{table} göç öncesi var");

        var accountId = Guid.NewGuid();
        var licenseId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await ExecAsync(conn, """
            INSERT INTO Customers (Id, Email, Name, PasswordHash, AuthVersion, CreatedAt, Unsubscribed)
            VALUES (@id, @email, N'Göç Testi Yayıncı', @ozet, 0, @now, 0)
            """,
            ("@id", accountId), ("@email", $"yayinci-{Guid.NewGuid():N}@example.test"),
            ("@ozet", $"h-{Guid.NewGuid():N}"), ("@now", now));
        await ExecAsync(conn, """
            INSERT INTO Licenses (Id, LicenseKey, CustomerId, SkuCode, ActivationSlots, IssuedAt, ExpiresAt,
                                  ShopperAppEnabled, SalesSyncMode)
            VALUES (@id, @key, @customer, 'STD', 1, @now, @expires, 0, 1)
            """,
            ("@id", licenseId), ("@key", $"LIC-{Guid.NewGuid():N}"), ("@customer", accountId), ("@now", now),
            ("@expires", now.AddYears(1)));

        await migrator.MigrateAsync(target);

        // 1. Yalnız ekleyici: var olan tabloların kolonları aynen duruyor (önceki imaj yazmayı sürdürür).
        var after = await ColumnsAsync(conn, ExistingTables);
        foreach (var table in ExistingTables)
            after[table].Should().BeEquivalentTo(before[table], $"{table}: S2 göçü var olan tabloya dokunmaz");

        // 2. Yeni tablo: tam kolon kümesi, PK CommandId, (LicenseId, CreatedAt) indeksi, cascade FK, boş.
        (await ColumnsAsync(conn, ["SalesCommandLogs"]))["SalesCommandLogs"].Should().BeEquivalentTo(ExpectedColumns);
        (await IndexAsync(conn, "SalesCommandLogs", "PK_SalesCommandLogs"))
            .Should().Be(new IndexInfo("CommandId", true), "CommandId istemcinin kimliği, tekildir");
        (await IndexAsync(conn, "SalesCommandLogs", "IX_SalesCommandLogs_LicenseId_CreatedAt"))
            .Should().Be(new IndexInfo("LicenseId,CreatedAt", false), "lisansın denetim sorgusu ve saklama süresi");
        (await ScalarAsync<string>(conn, """
            SELECT delete_referential_action_desc FROM sys.foreign_keys
            WHERE parent_object_id = OBJECT_ID('SalesCommandLogs') AND referenced_object_id = OBJECT_ID('Licenses')
            """)).Should().Be("CASCADE");
        (await ScalarAsync<int>(conn, "SELECT COUNT(*) FROM SalesCommandLogs")).Should().Be(0, "göç veri yazmaz");

        // 3. EF modeli şemayla aynı: S2 imajı satırı yazıp okuyabiliyor.
        var commandId = Guid.NewGuid();
        var hash = Enumerable.Range(1, SalesCommandLog.RequestHashLength).Select(i => (byte)i).ToArray();
        db.SalesCommandLogs.Add(new SalesCommandLog
        {
            CommandId = commandId, LicenseId = licenseId, ReplicaId = Guid.NewGuid(), Kind = "cancel",
            RequestHash = hash, Status = SalesCommandLog.StatusApplied, ResultJson = "{\"changed\":[]}",
            CreatedAt = now,
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        (await db.SalesCommandLogs.AsNoTracking().SingleAsync(c => c.CommandId == commandId))
            .Should().BeEquivalentTo(new { Kind = "cancel", RequestHash = hash, Status = "applied" });

        // 4. Önceki imaj günlük tablosunu bilmez: günlüğü olan lisansı silebilmeli, satır cascade ile gider.
        (await ExecAsync(conn, "DELETE FROM Licenses WHERE Id = @id", ("@id", licenseId))).Should().Be(1);
        (await ScalarAsync<int>(conn, "SELECT COUNT(*) FROM SalesCommandLogs WHERE LicenseId = @id", ("@id", licenseId)))
            .Should().Be(0, "lisansın günlüğü cascade ile silinir");
    }

    private static async Task<Dictionary<string, List<Col>>> ColumnsAsync(SqlConnection conn, IEnumerable<string> tables)
    {
        var result = tables.ToDictionary(t => t, _ => new List<Col>());
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT TABLE_NAME, COLUMN_NAME, DATA_TYPE, CHARACTER_MAXIMUM_LENGTH, NUMERIC_PRECISION, NUMERIC_SCALE, IS_NULLABLE
            FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = 'dbo' AND TABLE_NAME IN ({string.Join(", ", result.Keys.Select((_, i) => $"@t{i}"))})
            """;
        var index = 0;
        foreach (var table in result.Keys)
            cmd.Parameters.AddWithValue($"@t{index++}", table);

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var dataType = reader.GetString(2);
            var type = dataType switch
            {
                "nvarchar" or "varchar" or "nchar" or "char" or "varbinary" or "binary" =>
                    reader.GetInt32(3) == -1 ? $"{dataType}(max)" : $"{dataType}({reader.GetInt32(3)})",
                "decimal" or "numeric" => $"{dataType}({reader.GetByte(4)},{reader.GetInt32(5)})",
                _ => dataType,
            };
            result[reader.GetString(0)].Add(new Col(reader.GetString(1), type, reader.GetString(6) == "YES"));
        }
        return result;
    }

    private static async Task<IndexInfo?> IndexAsync(SqlConnection conn, string table, string name)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT STRING_AGG(c.name, ',') WITHIN GROUP (ORDER BY ic.key_ordinal), i.is_unique
            FROM sys.indexes i
            JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.key_ordinal > 0
            JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE i.object_id = OBJECT_ID(@table) AND i.name = @name
            GROUP BY i.is_unique
            """;
        cmd.Parameters.AddWithValue("@table", table);
        cmd.Parameters.AddWithValue("@name", name);
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;
        return new IndexInfo(reader.GetString(0), reader.GetBoolean(1));
    }

    private static async Task<int> ExecAsync(SqlConnection conn, string sql, params (string Name, object Value)[] parameters)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value);
        return await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<T> ScalarAsync<T>(SqlConnection conn, string sql, params (string Name, object Value)[] parameters)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value);
        return (T)(await cmd.ExecuteScalarAsync())!;
    }
}

/// <summary>
/// Göç betiğinin biçimi (spec §3.1 göç notları): ONLINE yok, veri yazımı yok, var olan tabloya ALTER yok. Veritabanı
/// gerekmez (<see cref="IMigrator.GenerateScript"/> bağlantı açmaz); Docker'sız da koşar.
/// </summary>
public sealed class SalesSyncS2CommandLogMigrationScriptTests
{
    [Fact]
    public void Goc_betigi_ONLINE_veri_yazimi_ve_var_olan_tabloya_ALTER_icermez()
    {
        using var db = new LicenseDbContext(new DbContextOptionsBuilder<LicenseDbContext>()
            .UseSqlServer("Server=betik-uretimi;Database=yok").Options);
        var (previous, target) = SalesSyncS2CommandLogMigrationTests.Migrations(db);

        var script = db.GetService<IMigrator>().GenerateScript(previous, target);

        Regex.IsMatch(script, @"\bONLINE\b", RegexOptions.IgnoreCase)
            .Should().BeFalse("Express'te ONLINE indeks işlemi yok (spec §3.1 göç notları)");

        var dataWrites = script.Split('\n')
            .Where(line => Regex.IsMatch(line, @"^\s*(INSERT|UPDATE|DELETE|MERGE)\b", RegexOptions.IgnoreCase))
            .Where(line => !line.Contains("__EFMigrationsHistory", StringComparison.Ordinal))
            .ToList();
        dataWrites.Should().BeEmpty("göç yalnız tablo ekler, veri yazmaz (spec §3.1)");

        script.Should().Contain("CREATE TABLE [SalesCommandLogs]");
        script.Should().Contain("CREATE INDEX [IX_SalesCommandLogs_LicenseId_CreatedAt]");
        Regex.IsMatch(script, @"ALTER\s+TABLE\s+\[(?!SalesCommandLogs\])", RegexOptions.IgnoreCase)
            .Should().BeFalse("var olan tablolar değişmez");
    }
}
