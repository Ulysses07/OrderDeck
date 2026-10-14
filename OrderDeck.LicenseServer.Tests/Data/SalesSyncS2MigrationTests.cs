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
/// SalesSyncS2ShippingSettings göçü (yayın ve etiket senkronu S2, spec 2026-10-13 §3.1 S2 satırı, §4.8) gerçek SQL
/// Server'da. Göç yalnız yeni bir tablo ekler: <c>LicenseShippingSettings</c> (PK <c>LicenseId</c>, Licenses'a cascade
/// FK). Deploy'un otomatik geri alması göçü geri ALMAZ (spec §8.6): S2 geri alınırsa S1 imajı bu şemada koşar. Bu
/// yüzden S1 imajının kolon listeleriyle yazdığı INSERT/UPDATE/DELETE'ler göçten sonra da geçmeli — lisans silmek
/// dahil (cascade olmasaydı ayar satırı olan lisansı eski imaj silemezdi). Göç hedeften bir önceki noktaya kadar
/// uygulanır, eski şemaya satır yazılır, sonra yalnız hedef uygulanır (<see cref="SalesSyncS1MigrationTests"/>
/// deseni).
/// </summary>
[Collection(SqlServerCollection.Name)]
[Trait("Category", "Testcontainers")]
public sealed class SalesSyncS2MigrationTests
{
    private readonly SqlServerContainerFixture _sql;

    public SalesSyncS2MigrationTests(SqlServerContainerFixture sql) => _sql = sql;

    private sealed record Col(string Name, string Type, bool Nullable);

    private sealed record IndexInfo(string KeyColumns, string? Filter, bool IsUnique);

    // S1 imajının (EF modelinin) bu tablolara yazdığı TAM kolon listeleri: S1 öncesi listeler + S1'in eklediği
    // kolonlar. rowversion kolonları (Licenses.RowVersion, ChangeSeq) INSERT'te verilemez; motor üretir. Test
    // bunları göç öncesi şemayla karşılaştırır — liste eksik kalırsa "S1 imajı yazabiliyor" iddiası boşa düşerdi.
    private static readonly string[] S1LicenseColumns =
        [.. SalesSyncS1MigrationScenario.PreS1LicenseColumns, "SalesSyncMode", "SalesSyncModeChangedAt"];

    private static readonly string[] S1StreamSessionColumns =
    [
        .. SalesSyncS1MigrationScenario.PreS1StreamSessionColumns,
        "OwnerReplicaId", "OwnerMachineName", "ForceEndedBy", "ReleasedAt", "UnsentLabelCount",
    ];

    private static readonly string[] S1OrderColumns =
    [
        .. SalesSyncS1MigrationScenario.PreS1OrderColumns,
        "ParentOrderId", "ShipmentId", "OriginReplicaId", "PrintBatchId", "PrintSeq", "PriceCorrectedAt", "PaidPrice",
        "SessionReleasedAt", "PriceSetAt", "CancelSetAt", "BackupSetAt", "ShipmentSetAt", "TextScrubbedAt",
    ];

    private static readonly string[] S1ShipmentColumns =
    [
        .. SalesSyncS1MigrationScenario.PreS1ShipmentColumns,
        "Version", "AmountDerivedAt", "CommandedAt", "OriginReplicaId",
    ];

    private static readonly string[] S1WpfReplicaColumns =
    [
        "LicenseId", "ReplicaId", "MachineName", "HardwareFingerprint", "HardwareFingerprintConflictAt",
        "ClientVersion", "Protocol", "FeatureLevel", "SeenMode", "SeenModeAt", "SeenModeOnAt", "PreviousReplicaId",
        "FirstSeenAt", "LastSeenAt", "BackfillCompletedAt", "PaymentHistoryCompletedAt", "BackfillDivergedLocal",
        "BackfillDivergedServer", "ReportedThreshold", "ReportedFee",
    ];

    private static readonly string[] S1SalesLegacyTraceColumns = ["LicenseId", "LastLegacySalesPushAt"];

    private static readonly Dictionary<string, string[]> S1Tables = new()
    {
        ["Licenses"] = S1LicenseColumns,
        ["StreamSessions"] = S1StreamSessionColumns,
        ["Orders"] = S1OrderColumns,
        ["Shipments"] = S1ShipmentColumns,
        ["WpfReplicas"] = S1WpfReplicaColumns,
        ["SalesLegacyTraces"] = S1SalesLegacyTraceColumns,
    };

    private static readonly string[] EngineGenerated = ["RowVersion", "ChangeSeq"];

    /// <summary>Tablonun tam kolon kümesi (spec §3.1, S2 satırı). Tür <c>INFORMATION_SCHEMA</c> biçiminde.</summary>
    private static readonly Col[] ExpectedShippingSettingsColumns =
    [
        new("LicenseId", "uniqueidentifier", false),
        new("FreeShippingThreshold", "decimal(18,2)", true),
        new("ShippingFee", "decimal(18,2)", true),
        new("Version", "int", false),
        new("UpdatedAt", "datetimeoffset", false),
        new("UpdatedBy", "nvarchar(64)", true),
    ];

    internal static (string Previous, string Target) Migrations(LicenseDbContext db)
    {
        var all = db.Database.GetMigrations().ToList();
        var target = all.Single(m => m.EndsWith("_SalesSyncS2ShippingSettings", StringComparison.Ordinal));
        return (all[all.IndexOf(target) - 1], target);
    }

    [Fact]
    public async Task Tablo_PK_ve_cascade_FK_ile_bos_dogar_S1_imaji_yazmayi_surdurur()
    {
        var connectionString = await _sql.CreateDatabaseAsync();
        await using var db = new LicenseDbContext(
            new DbContextOptionsBuilder<LicenseDbContext>().UseSqlServer(connectionString).Options);
        var migrator = db.GetService<IMigrator>();
        var (previous, target) = Migrations(db);

        // Hedeften BİR ÖNCEKİ noktaya kadar: tablo yok.
        await migrator.MigrateAsync(previous);
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();

        (await ColumnsAsync(conn, ["LicenseShippingSettings"]))["LicenseShippingSettings"]
            .Should().BeEmpty("göç öncesi tablo yok");
        var before = await ColumnsAsync(conn, S1Tables.Keys);
        foreach (var (table, columns) in S1Tables)
            before[table].Select(c => c.Name).Except(EngineGenerated)
                .Should().BeEquivalentTo(columns, $"{table}: S1 imajının kolon listesi göç öncesi şemayla aynı");

        var accountId = Guid.NewGuid();
        var licenseId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await ExecAsync(conn, """
            INSERT INTO Customers (Id, Email, Name, PasswordHash, AuthVersion, CreatedAt, Unsubscribed)
            VALUES (@id, @email, N'Göç Testi Yayıncı', @ozet, 0, @now, 0)
            """,
            ("@id", accountId), ("@email", $"yayinci-{Guid.NewGuid():N}@example.test"),
            ("@ozet", $"h-{Guid.NewGuid():N}"), ("@now", now));
        await InsertLicenseS1Async(conn, licenseId, accountId, now);

        await migrator.MigrateAsync(target);

        // 1. Yalnız ekleyici: var olan tabloların kolonları aynen duruyor.
        var after = await ColumnsAsync(conn, S1Tables.Keys);
        foreach (var table in S1Tables.Keys)
            after[table].Should().BeEquivalentTo(before[table], $"{table}: S2 göçü var olan tabloya dokunmaz");

        // 2. Yeni tablo: tam kolon kümesi, PK LicenseId, Licenses'a cascade FK, boş (veri yazımı yok).
        (await ColumnsAsync(conn, ["LicenseShippingSettings"]))["LicenseShippingSettings"]
            .Should().BeEquivalentTo(ExpectedShippingSettingsColumns);
        (await IndexAsync(conn, "LicenseShippingSettings", "PK_LicenseShippingSettings"))
            .Should().Be(new IndexInfo("LicenseId", null, true), "lisans başına tek ayar satırı (spec §3.1)");
        (await ScalarAsync<string>(conn, """
            SELECT delete_referential_action_desc FROM sys.foreign_keys
            WHERE parent_object_id = OBJECT_ID('LicenseShippingSettings') AND referenced_object_id = OBJECT_ID('Licenses')
            """)).Should().Be("CASCADE");
        (await ScalarAsync<int>(conn, "SELECT COUNT(*) FROM LicenseShippingSettings")).Should().Be(0, "göç veri yazmaz");

        // 3. EF modeli şemayla aynı: S2 imajı satırı yazıp okuyabiliyor; jeton sürüm.
        db.LicenseShippingSettings.Add(new LicenseShippingSettings
        {
            LicenseId = licenseId, FreeShippingThreshold = 500.50m, ShippingFee = 60m, Version = 1,
            UpdatedAt = now, UpdatedBy = LicenseShippingSettings.UpdatedByCli,
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        (await db.LicenseShippingSettings.AsNoTracking().SingleAsync(s => s.LicenseId == licenseId))
            .Should().BeEquivalentTo(new { FreeShippingThreshold = 500.50m, ShippingFee = 60m, Version = 1, UpdatedBy = "cli" });

        // 4. S1 imajı yazmayı sürdürür (genişlet/daralt, spec §8.6).
        await AssertS1ImageKeepsWritingAsync(conn, accountId, licenseId);
    }

    /// <summary>S1 imajının bu şemadaki yazımları: tam S1 kolon listeleriyle INSERT ve UPDATE geçer; ayar satırı olan
    /// lisans silinebilir (bugünkü <c>AdminCustomersController</c> yolu; S1 imajı ayar tablosunu tanımaz) ve ayar satırı
    /// cascade ile gider.</summary>
    private static async Task AssertS1ImageKeepsWritingAsync(SqlConnection conn, Guid accountId, Guid licenseId)
    {
        var now = DateTimeOffset.UtcNow;
        var replicaId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var shipmentId = Guid.NewGuid();

        await ExecAsync(conn, Insert("WpfReplicas", S1WpfReplicaColumns), Values(S1WpfReplicaColumns, new()
        {
            ["LicenseId"] = licenseId, ["ReplicaId"] = replicaId, ["Protocol"] = (byte)2, ["FeatureLevel"] = (byte)1,
            ["SeenMode"] = (byte)0, ["SeenModeAt"] = now, ["FirstSeenAt"] = now, ["LastSeenAt"] = now,
            ["BackfillDivergedLocal"] = 0, ["BackfillDivergedServer"] = 0, ["ReportedThreshold"] = 500m,
            ["ReportedFee"] = 60m,
        }));
        await ExecAsync(conn, Insert("SalesLegacyTraces", S1SalesLegacyTraceColumns), Values(S1SalesLegacyTraceColumns, new()
        {
            ["LicenseId"] = licenseId, ["LastLegacySalesPushAt"] = now,
        }));
        await ExecAsync(conn, Insert("StreamSessions", S1StreamSessionColumns), Values(S1StreamSessionColumns, new()
        {
            ["Id"] = sessionId, ["LicenseId"] = licenseId, ["Title"] = "S1 imajının yayını", ["StartedAt"] = now,
            ["Platforms"] = "tiktok", ["UpdatedAt"] = now, ["OwnerReplicaId"] = replicaId, ["OwnerMachineName"] = "PC-A",
        }));
        await ExecAsync(conn, Insert("Shipments", S1ShipmentColumns), Values(S1ShipmentColumns, new()
        {
            ["Id"] = shipmentId, ["LicenseId"] = licenseId, ["CustomerId"] = "c1", ["Status"] = 0,
            ["CumulativeAmount"] = 90m, ["CreatedAt"] = now, ["UpdatedAt"] = now, ["Version"] = 0,
            ["OriginReplicaId"] = replicaId,
        }));
        await ExecAsync(conn, Insert("Orders", S1OrderColumns), Values(S1OrderColumns, new()
        {
            ["Id"] = orderId, ["LicenseId"] = licenseId, ["SessionId"] = sessionId, ["CustomerId"] = "c1",
            ["Platform"] = "tiktok", ["Username"] = "kullanici1", ["MessageText"] = "B1", ["Code"] = "B1",
            ["Price"] = 90m, ["AddedAt"] = now, ["IsShippingFee"] = false, ["IsBackupPromoted"] = false,
            ["IsTentativeBackup"] = false, ["UpdatedAt"] = now, ["SyncVersion"] = 1, ["OriginReplicaId"] = replicaId,
            ["ShipmentId"] = shipmentId,
        }));

        (await ExecAsync(conn, "UPDATE StreamSessions SET EndedAt = @now, ReleasedAt = @now WHERE Id = @id",
            ("@id", sessionId), ("@now", now))).Should().Be(1);
        (await ExecAsync(conn, """
            UPDATE Orders SET CancelledAt = @now, CancelSetAt = @now, SessionReleasedAt = @now, SyncVersion = SyncVersion + 1
            WHERE Id = @id AND SyncVersion = 1
            """, ("@id", orderId), ("@now", now))).Should().Be(1);
        (await ExecAsync(conn, "UPDATE Shipments SET Version = Version + 1, AmountDerivedAt = @now WHERE Id = @id AND Version = 0",
            ("@id", shipmentId), ("@now", now))).Should().Be(1);
        (await ExecAsync(conn, "UPDATE Licenses SET SalesSyncMode = 1, SalesSyncModeChangedAt = @now WHERE Id = @id",
            ("@id", licenseId), ("@now", now))).Should().Be(1);
        (await ExecAsync(conn, "UPDATE WpfReplicas SET LastSeenAt = @now, ReportedFee = 65 WHERE LicenseId = @l AND ReplicaId = @r",
            ("@l", licenseId), ("@r", replicaId), ("@now", now))).Should().Be(1);

        // Yeni lisans S1 imajının listesiyle açılır; ayar satırı olmadan da okunur.
        var newLicenseId = Guid.NewGuid();
        await InsertLicenseS1Async(conn, newLicenseId, accountId, now);
        (await ScalarAsync<int>(conn, "SELECT COUNT(*) FROM LicenseShippingSettings WHERE LicenseId = @id",
            ("@id", newLicenseId))).Should().Be(0);

        // Lisans silme: S1 imajı ayar tablosunu bilmez. Bağımlı satırlar (sipariş, oturum, dosya, kopya, iz) önceden
        // de vardı; yeni tek bağımlı ayar satırıdır ve cascade ile gider — FK silmeyi düşürmez.
        await ExecAsync(conn, "DELETE FROM Orders WHERE LicenseId = @id", ("@id", licenseId));
        await ExecAsync(conn, "DELETE FROM StreamSessions WHERE LicenseId = @id", ("@id", licenseId));
        (await ExecAsync(conn, "DELETE FROM Licenses WHERE Id = @id", ("@id", licenseId))).Should().Be(1);
        (await ScalarAsync<int>(conn, "SELECT COUNT(*) FROM LicenseShippingSettings WHERE LicenseId = @id",
            ("@id", licenseId))).Should().Be(0, "lisansın ayar satırı cascade ile silinir");
    }

    private static Task InsertLicenseS1Async(SqlConnection conn, Guid licenseId, Guid accountId, DateTimeOffset now) =>
        ExecAsync(conn, Insert("Licenses", S1LicenseColumns), Values(S1LicenseColumns, new()
        {
            ["Id"] = licenseId, ["LicenseKey"] = $"LIC-{Guid.NewGuid():N}", ["CustomerId"] = accountId,
            ["SkuCode"] = "STD", ["ActivationSlots"] = 1, ["IssuedAt"] = now, ["ExpiresAt"] = now.AddYears(1),
            ["ShopperAppEnabled"] = false, ["SalesSyncMode"] = (byte)0,
        }));

    /// <summary>Listenin her kolonu için bir parametre: <c>INSERT INTO t (a, b) VALUES (@a, @b)</c>.</summary>
    private static string Insert(string table, string[] columns) =>
        $"INSERT INTO [{table}] ({string.Join(", ", columns.Select(c => $"[{c}]"))}) "
        + $"VALUES ({string.Join(", ", columns.Select(c => $"@{c}"))})";

    /// <summary>Listedeki her kolonun değeri; verilmeyen kolon NULL (boş olamayan kolonu unutan test SQL hatasıyla
    /// düşer, sessizce geçmez).</summary>
    private static (string Name, object Value)[] Values(string[] columns, Dictionary<string, object> values) =>
        columns.Select(c => ($"@{c}", values.TryGetValue(c, out var v) ? v : (object)DBNull.Value)).ToArray();

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
            SELECT STRING_AGG(c.name, ',') WITHIN GROUP (ORDER BY ic.key_ordinal), i.filter_definition, i.is_unique
            FROM sys.indexes i
            JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.key_ordinal > 0
            JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE i.object_id = OBJECT_ID(@table) AND i.name = @name
            GROUP BY i.filter_definition, i.is_unique
            """;
        cmd.Parameters.AddWithValue("@table", table);
        cmd.Parameters.AddWithValue("@name", name);
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;
        return new IndexInfo(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetBoolean(2));
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
/// Göç betiğinin biçimi (spec §3.1 göç notları): ONLINE yok ve veri yazımı yok. Veritabanı gerekmez —
/// <see cref="IMigrator.GenerateScript"/> bağlantı açmaz — bu yüzden container koleksiyonunun dışında ve Docker'sız da
/// koşar (<see cref="SalesSyncS1MigrationScriptTests"/> deseni).
/// </summary>
public sealed class SalesSyncS2MigrationScriptTests
{
    [Fact]
    public void Goc_betigi_ONLINE_ve_veri_yazimi_icermez()
    {
        using var db = new LicenseDbContext(new DbContextOptionsBuilder<LicenseDbContext>()
            .UseSqlServer("Server=betik-uretimi;Database=yok").Options);
        var (previous, target) = SalesSyncS2MigrationTests.Migrations(db);

        var script = db.GetService<IMigrator>().GenerateScript(previous, target);

        Regex.IsMatch(script, @"\bONLINE\b", RegexOptions.IgnoreCase)
            .Should().BeFalse("Express'te ONLINE indeks işlemi yok (spec §3.1 göç notları)");

        // Göç geçmişi satırı (__EFMigrationsHistory) EF'in kendi kaydıdır; onun dışında hiçbir veri yazımı yok.
        var dataWrites = script.Split('\n')
            .Where(line => Regex.IsMatch(line, @"^\s*(INSERT|UPDATE|DELETE|MERGE)\b", RegexOptions.IgnoreCase))
            .Where(line => !line.Contains("__EFMigrationsHistory", StringComparison.Ordinal))
            .ToList();
        dataWrites.Should().BeEmpty("S2 göçü yalnız tablo ekler, veri yazmaz (spec §3.1)");

        script.Should().Contain("CREATE TABLE [LicenseShippingSettings]");
        Regex.IsMatch(script, @"ALTER\s+TABLE\s+\[(?!LicenseShippingSettings\])", RegexOptions.IgnoreCase)
            .Should().BeFalse("var olan tablolar değişmez");
    }
}
