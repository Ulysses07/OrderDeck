using System.Security.Cryptography;
using System.Text;
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
/// SalesSyncS3PaymentRequests göçü (yayın ve etiket senkronu S3, spec 2026-10-13 §3.1 S3 satırları, §6.4) gerçek SQL
/// Server'da. Göç yalnız üç yeni tablo ekler: <c>PaymentRequestRecords</c> (kira ve teslim kaydı),
/// <c>PaymentRequestMembers</c> (üye kesişimi) ve <c>PaymentRequestRecordOrders</c> (isteğin kapsadığı siparişler).
/// Deploy'un otomatik geri alması göçü geri ALMAZ (spec §8.6): S3 geri alınırsa S2 imajı bu şemada koşar — var olan
/// tabloların kolonları değişmemeli, kaydı olan lisans silinebilmeli. Göç hedeften bir önceki noktaya kadar uygulanır,
/// sonra yalnız hedef (<see cref="SalesSyncS2CommandLogMigrationTests"/> deseni).
///
/// <para>DM6'nın hakemi filtreli tekil indekstir: kişi×kapsamda tek <c>Sending</c>. Anahtar PersonKey metninin değil
/// özetinin (<c>PersonKeyHash</c>) üzerindedir; böylece harf ya da sondaki boşlukla ayrılan iki kişi birbirini
/// engellemez (§2, §10.1-16). Kapsam anahtarı ise varsayılan (CI) harmanda kalır — <c>SaleScope</c> ile aynı anlam.</para>
/// </summary>
[Collection(SqlServerCollection.Name)]
[Trait("Category", "Testcontainers")]
public sealed class SalesSyncS3PaymentRequestsMigrationTests
{
    private readonly SqlServerContainerFixture _sql;

    public SalesSyncS3PaymentRequestsMigrationTests(SqlServerContainerFixture sql) => _sql = sql;

    private sealed record Col(string Name, string Type, bool Nullable);

    private sealed record IndexInfo(string KeyColumns, string? Filter, bool IsUnique);

    private sealed record ForeignKeyInfo(string Parent, string Referenced, string OnDelete);

    private const string Records = "PaymentRequestRecords";
    private const string Members = "PaymentRequestMembers";
    private const string RecordOrders = "PaymentRequestRecordOrders";

    private static readonly string[] NewTables = [Records, Members, RecordOrders];

    /// <summary>Önceki imajın yazdığı tablolar: göç bunlara dokunmamalı.</summary>
    private static readonly string[] ExistingTables =
    [
        "Licenses", "StreamSessions", "Orders", "Shipments", "WpfReplicas", "SalesCommandLogs",
        "CustomerBalanceTransactions", "LicenseShippingSettings",
    ];

    /// <summary>Kayıt tablosunun tam kolon kümesi (spec §3.1, S3 satırı). Tür <c>INFORMATION_SCHEMA</c> biçiminde.</summary>
    private static readonly Col[] ExpectedRecordColumns =
    [
        new("Id", "uniqueidentifier", false),
        new("LicenseId", "uniqueidentifier", false),
        new("PersonKey", "nvarchar(80)", false),
        new("PersonKeyHash", "binary(32)", false),
        new("ScopeKey", "nvarchar(128)", false),
        new("OwnerWpfCustomerId", "nvarchar(64)", false),
        new("ReplicaId", "uniqueidentifier", false),
        new("MachineName", "nvarchar(128)", true),
        new("Kind", "tinyint", false),
        new("State", "tinyint", false),
        new("ProductTotal", "decimal(18,2)", true),
        new("TotalBeforeBalance", "decimal(18,2)", true),
        new("NetAmount", "decimal(18,2)", true),
        new("AppliedAmount", "decimal(18,2)", true),
        new("FullyFromBalance", "bit", true),
        new("ShippingSettingsVersion", "int", true),
        new("RequestHash", "binary(32)", false),
        new("StartedAt", "datetimeoffset", false),
        new("LeaseUntil", "datetimeoffset", true),
        new("FinishedAt", "datetimeoffset", true),
        new("Version", "int", false),
    ];

    private static readonly Col[] ExpectedMemberColumns =
    [
        new("RecordId", "uniqueidentifier", false),
        new("WpfCustomerId", "nvarchar(64)", false),
        new("LicenseId", "uniqueidentifier", false),
        new("ScopeKey", "nvarchar(128)", false),
    ];

    private static readonly Col[] ExpectedRecordOrderColumns =
    [
        new("RecordId", "uniqueidentifier", false),
        new("OrderId", "uniqueidentifier", false),
        new("LicenseId", "uniqueidentifier", false),
    ];

    internal static (string Previous, string Target) Migrations(LicenseDbContext db)
    {
        var all = db.Database.GetMigrations().ToList();
        var target = all.Single(m => m.EndsWith("_SalesSyncS3PaymentRequests", StringComparison.Ordinal));
        return (all[all.IndexOf(target) - 1], target);
    }

    [Fact]
    public async Task Tablolar_PK_indeksler_harman_ve_cascade_ile_bos_dogar_var_olan_tablolar_degismez()
    {
        var connectionString = await _sql.CreateDatabaseAsync();
        await using var db = NewContext(connectionString);
        var migrator = db.GetService<IMigrator>();
        var (previous, target) = Migrations(db);

        await migrator.MigrateAsync(previous);
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();

        var absent = await ColumnsAsync(conn, NewTables);
        foreach (var table in NewTables)
            absent[table].Should().BeEmpty($"göç öncesi {table} yok");
        var before = await ColumnsAsync(conn, ExistingTables);
        foreach (var table in ExistingTables)
            before[table].Should().NotBeEmpty($"{table} göç öncesi var");

        var licenseId = await InsertLicenseAsync(conn);

        await migrator.MigrateAsync(target);

        // 1. Yalnız ekleyici: var olan tabloların kolonları aynen duruyor (önceki imaj yazmayı sürdürür, §8.6).
        var after = await ColumnsAsync(conn, ExistingTables);
        foreach (var table in ExistingTables)
            after[table].Should().BeEquivalentTo(before[table], $"{table}: S3 göçü var olan tabloya dokunmaz");

        // 2. Yeni tablolar: tam kolon kümesi, tür ve boşluk.
        var columns = await ColumnsAsync(conn, NewTables);
        columns[Records].Should().BeEquivalentTo(ExpectedRecordColumns);
        columns[Members].Should().BeEquivalentTo(ExpectedMemberColumns);
        columns[RecordOrders].Should().BeEquivalentTo(ExpectedRecordOrderColumns);

        // 3. Harman: PersonKey bayt bayt (BIN2, ordinal yeniden denetim için); ScopeKey SaleScope'la aynı (varsayılan).
        (await ColumnCollationAsync(conn, Records, "PersonKey")).Should().Be("Latin1_General_100_BIN2");
        (await ColumnCollationAsync(conn, Records, "ScopeKey"))
            .Should().Be(await ColumnCollationAsync(conn, "CustomerBalanceTransactions", "SaleScope"),
                "kapsam anahtarı defterdeki SaleScope ile aynı karşılaştırma anlamını taşır");

        // 4. Version: NOT NULL DEFAULT 0 (kolonu vermeyen ham INSERT'e düşer).
        (await ColumnDefaultAsync(conn, Records, "Version")).Should().Be("((0))");

        // 5. Birincil anahtarlar.
        (await IndexAsync(conn, Records, "PK_PaymentRequestRecords"))
            .Should().Be(new IndexInfo("Id", null, true), "Id istemcinin recordId'si");
        (await IndexAsync(conn, Members, "PK_PaymentRequestMembers"))
            .Should().Be(new IndexInfo("RecordId,WpfCustomerId", null, true));
        (await IndexAsync(conn, RecordOrders, "PK_PaymentRequestRecordOrders"))
            .Should().Be(new IndexInfo("RecordId,OrderId", null, true));

        // 6. İndeksler: DM6'nın hakemi (filtreli tekil, sabit ad), kişi×kapsam geçmişi ve üye kesişimi.
        (await IndexAsync(conn, Records, PaymentRequestRecord.SendingIndexName))
            .Should().Be(new IndexInfo("LicenseId,PersonKeyHash,ScopeKey", "([State]=(0))", true),
                "kişi×kapsamda aynı anda en çok bir Sending (DM6)");
        (await IndexAsync(conn, Records, "IX_PaymentRequestRecords_LicenseId_PersonKeyHash_ScopeKey_StartedAt"))
            .Should().Be(new IndexInfo("LicenseId,PersonKeyHash,ScopeKey,StartedAt", null, false));
        (await IndexAsync(conn, Members, "IX_PaymentRequestMembers_LicenseId_WpfCustomerId_ScopeKey"))
            .Should().Be(new IndexInfo("LicenseId,WpfCustomerId,ScopeKey", null, false));
        (await IndexNamesAsync(conn, NewTables)).Should().BeEquivalentTo(new[]
        {
            "PK_PaymentRequestRecords", PaymentRequestRecord.SendingIndexName,
            "IX_PaymentRequestRecords_LicenseId_PersonKeyHash_ScopeKey_StartedAt",
            "PK_PaymentRequestMembers", "IX_PaymentRequestMembers_LicenseId_WpfCustomerId_ScopeKey",
            "PK_PaymentRequestRecordOrders",
        }, "başka indeks yok");

        // 7. Yabancı anahtarlar: yalnız Records → Licenses ve iki alt tablo → Records, hepsi cascade. Alt tablolardan
        //    Licenses'a FK yok (SQL Server çoklu cascade yolunu reddeder), Orders'a FK yok (geçmiş siparişi sunucuda
        //    olmayabilir).
        (await ForeignKeysAsync(conn, NewTables)).Should().BeEquivalentTo(new[]
        {
            new ForeignKeyInfo(Records, "Licenses", "CASCADE"),
            new ForeignKeyInfo(Members, Records, "CASCADE"),
            new ForeignKeyInfo(RecordOrders, Records, "CASCADE"),
        });

        foreach (var table in NewTables)
            (await ScalarAsync<int>(conn, $"SELECT COUNT(*) FROM {table}")).Should().Be(0, $"göç {table}'a veri yazmaz");

        // 8. EF modeli şemayla aynı: S3 imajı kaydı üyeleri ve siparişleriyle yazıp okuyabiliyor.
        var recordId = Guid.NewGuid();
        var ownerId = Guid.NewGuid().ToString("N");
        var rawMemberId = $"yerel-{Guid.NewGuid():N}";
        var orderId = Guid.NewGuid();
        var personKey = $"c:{ownerId}";
        var now = DateTimeOffset.UtcNow;
        db.PaymentRequestRecords.Add(new PaymentRequestRecord
        {
            Id = recordId, LicenseId = licenseId, PersonKey = personKey, PersonKeyHash = Hash(personKey),
            ScopeKey = "cumulative", OwnerWpfCustomerId = ownerId, ReplicaId = Guid.NewGuid(), MachineName = "PC-A",
            Kind = PaymentRequestKind.Lease, State = PaymentRequestState.Sending, ProductTotal = 550m,
            TotalBeforeBalance = 550m, ShippingSettingsVersion = 3, RequestHash = Hash($"istek-{recordId:N}"),
            StartedAt = now, LeaseUntil = now.AddSeconds(120),
            Members =
            [
                new PaymentRequestMember { LicenseId = licenseId, WpfCustomerId = ownerId, ScopeKey = "cumulative" },
                new PaymentRequestMember { LicenseId = licenseId, WpfCustomerId = rawMemberId, ScopeKey = "cumulative" },
            ],
            RecordOrders = [new PaymentRequestRecordOrder { LicenseId = licenseId, OrderId = orderId }],
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var saved = await db.PaymentRequestRecords.AsNoTracking()
            .Include(r => r.Members).Include(r => r.RecordOrders)
            .SingleAsync(r => r.Id == recordId);
        saved.Should().BeEquivalentTo(new
        {
            PersonKey = personKey, Kind = PaymentRequestKind.Lease, State = PaymentRequestState.Sending,
            ProductTotal = 550m, NetAmount = (decimal?)null, FullyFromBalance = (bool?)null, Version = 0,
        });
        saved.Members.Select(m => m.WpfCustomerId).Should().BeEquivalentTo(new[] { ownerId, rawMemberId });
        saved.RecordOrders.Select(o => o.OrderId).Should().Equal(orderId);
        (await ScalarAsync<byte>(conn, $"SELECT State FROM {Records} WHERE Id = @id", ("@id", recordId)))
            .Should().Be(0, "Sending telde ve indeks süzgecinde 0");

        // 9. Önceki imaj bu tabloları bilmez: kaydı olan lisansı silebilmeli; kayıt, üyeler ve siparişler gider.
        (await ExecAsync(conn, "DELETE FROM Licenses WHERE Id = @id", ("@id", licenseId))).Should().Be(1);
        foreach (var table in NewTables)
            (await ScalarAsync<int>(conn, $"SELECT COUNT(*) FROM {table} WHERE LicenseId = @id", ("@id", licenseId)))
                .Should().Be(0, $"lisansın {table} satırları cascade ile silinir");
    }

    [Fact]
    public async Task Filtreli_tekil_indeks_kisi_ozeti_ve_kapsam_basina_tek_Sending_birakir()
    {
        var connectionString = await _sql.CreateDatabaseAsync();
        await using var db = NewContext(connectionString);
        await db.GetService<IMigrator>().MigrateAsync(Migrations(db).Target);
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();
        var licenseId = await InsertLicenseAsync(conn);

        // Enum ile süzgeç aynı değeri konuşur: EF'in yazdığı Sending, ham State = 0 ile çakışır.
        var groupKey = $"g:Grup-{Guid.NewGuid():N}";
        db.PaymentRequestRecords.Add(NewRecord(licenseId, groupKey, "session:ABC", PaymentRequestState.Sending));
        await db.SaveChangesAsync();
        (await InsertViolationAsync(conn, licenseId, groupKey, "session:ABC", state: 0))
            .Should().Be(2601, "aynı kişi×kapsamda ikinci Sending reddedilir (DM6)");

        // Sending olmayan kayıtlar sınırsız: aynı anahtarda teslim edilmiş ve belirsiz kayıtlar birikir.
        await InsertRecordAsync(conn, licenseId, groupKey, "session:ABC", state: 1);
        await InsertRecordAsync(conn, licenseId, groupKey, "session:ABC", state: 1);
        await InsertRecordAsync(conn, licenseId, groupKey, "session:ABC", state: 2);

        // Kapsam varsayılan (CI) harmanda: yalnız harfle ayrılan kapsam aynı kapsamdır (SaleScope ile aynı anlam).
        (await InsertViolationAsync(conn, licenseId, groupKey, "session:abc", state: 0))
            .Should().Be(2601, "kapsam anahtarı harf farkını yutar");

        // Başka kapsam serbest.
        await InsertRecordAsync(conn, licenseId, groupKey, "cumulative", state: 0);

        // Yalnız harfle ayrılan iki kişi (iki ayrı GroupId) farklı özet taşır: birbirini engellemez. Metin BIN2'de
        // de ayrıdır.
        var lowerGroupKey = groupKey.ToLowerInvariant();
        await InsertRecordAsync(conn, licenseId, lowerGroupKey, "session:ABC", state: 0);
        (await ScalarAsync<int>(conn, $"SELECT COUNT(*) FROM {Records} WHERE LicenseId = @l AND PersonKey = @k",
            ("@l", licenseId), ("@k", lowerGroupKey))).Should().Be(1, "BIN2 harf farkını ayırır");

        // Yalnız sondaki boşlukla ayrılan iki kişi de: özetleri farklı, ikisi de Sending olabilir. Metin eşitliği ise
        // sondaki boşluğu BIN2'de bile yutar — hakemin metin değil özet olmasının nedeni bu (§2).
        var ownerKey = $"r:yerel-{Guid.NewGuid():N}";
        await InsertRecordAsync(conn, licenseId, ownerKey, "session:ABC", state: 0);
        await InsertRecordAsync(conn, licenseId, ownerKey + " ", "session:ABC", state: 0);
        (await ScalarAsync<int>(conn, $"SELECT COUNT(*) FROM {Records} WHERE LicenseId = @l AND PersonKey = @k",
            ("@l", licenseId), ("@k", ownerKey))).Should().Be(2, "SQL Server = sondaki boşluğu harmandan bağımsız yutar");

        // Başka lisans aynı kişi×kapsamda kendi kirasını açabilir.
        var otherLicenseId = await InsertLicenseAsync(conn);
        await InsertRecordAsync(conn, otherLicenseId, groupKey, "session:ABC", state: 0);

        (await ScalarAsync<int>(conn, $"SELECT COUNT(*) FROM {Records} WHERE LicenseId = @l AND State = 0",
            ("@l", licenseId))).Should().Be(5, "beş ayrı kişi×kapsam anahtarında birer Sending");
    }

    private static LicenseDbContext NewContext(string connectionString) =>
        new(new DbContextOptionsBuilder<LicenseDbContext>().UseSqlServer(connectionString).Options);

    private static byte[] Hash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));

    private static PaymentRequestRecord NewRecord(Guid licenseId, string personKey, string scopeKey,
        PaymentRequestState state)
    {
        var id = Guid.NewGuid();
        return new PaymentRequestRecord
        {
            Id = id, LicenseId = licenseId, PersonKey = personKey, PersonKeyHash = Hash(personKey), ScopeKey = scopeKey,
            OwnerWpfCustomerId = Guid.NewGuid().ToString("N"), ReplicaId = Guid.NewGuid(),
            Kind = PaymentRequestKind.Lease, State = state, RequestHash = Hash($"istek-{id:N}"),
            StartedAt = DateTimeOffset.UtcNow,
        };
    }

    private static async Task<Guid> InsertLicenseAsync(SqlConnection conn)
    {
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
        return licenseId;
    }

    /// <summary>Ham INSERT: <c>Version</c> verilmez (DEFAULT 0'a düşer), tutarlar boş.</summary>
    private static Task<int> InsertRecordAsync(SqlConnection conn, Guid licenseId, string personKey, string scopeKey,
        byte state)
    {
        var id = Guid.NewGuid();
        return ExecAsync(conn, $"""
            INSERT INTO {Records} (Id, LicenseId, PersonKey, PersonKeyHash, ScopeKey, OwnerWpfCustomerId, ReplicaId,
                                   Kind, State, RequestHash, StartedAt)
            VALUES (@id, @license, @key, @keyHash, @scope, @owner, @replica, 0, @state, @requestHash, @now)
            """,
            ("@id", id), ("@license", licenseId), ("@key", personKey), ("@keyHash", Hash(personKey)),
            ("@scope", scopeKey), ("@owner", Guid.NewGuid().ToString("N")), ("@replica", Guid.NewGuid()),
            ("@state", state), ("@requestHash", Hash($"istek-{id:N}")), ("@now", DateTimeOffset.UtcNow));
    }

    /// <summary>INSERT'in Sending indeksine çarptığını doğrular; SQL Server hata numarasını döner.</summary>
    private static async Task<int> InsertViolationAsync(SqlConnection conn, Guid licenseId, string personKey,
        string scopeKey, byte state)
    {
        var act = () => InsertRecordAsync(conn, licenseId, personKey, scopeKey, state);
        var thrown = (await act.Should().ThrowAsync<SqlException>()).Which;
        thrown.Message.Should().Contain(PaymentRequestRecord.SendingIndexName, "ihlali S3-2 indeks adından tanır");
        return thrown.Number;
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

    private static Task<string> ColumnCollationAsync(SqlConnection conn, string table, string column) =>
        ScalarAsync<string>(conn, """
            SELECT COLLATION_NAME FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = 'dbo' AND TABLE_NAME = @table AND COLUMN_NAME = @column
            """, ("@table", table), ("@column", column));

    private static Task<string> ColumnDefaultAsync(SqlConnection conn, string table, string column) =>
        ScalarAsync<string>(conn, """
            SELECT COLUMN_DEFAULT FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = 'dbo' AND TABLE_NAME = @table AND COLUMN_NAME = @column
            """, ("@table", table), ("@column", column));

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

    private static async Task<List<string>> IndexNamesAsync(SqlConnection conn, IEnumerable<string> tables)
    {
        var names = new List<string>();
        foreach (var table in tables)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT name FROM sys.indexes WHERE object_id = OBJECT_ID(@table) AND name IS NOT NULL";
            cmd.Parameters.AddWithValue("@table", table);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                names.Add(reader.GetString(0));
        }
        return names;
    }

    private static async Task<List<ForeignKeyInfo>> ForeignKeysAsync(SqlConnection conn, IEnumerable<string> tables)
    {
        var result = new List<ForeignKeyInfo>();
        foreach (var table in tables)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT OBJECT_NAME(parent_object_id), OBJECT_NAME(referenced_object_id), delete_referential_action_desc
                FROM sys.foreign_keys WHERE parent_object_id = OBJECT_ID(@table)
                """;
            cmd.Parameters.AddWithValue("@table", table);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                result.Add(new ForeignKeyInfo(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }
        return result;
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
/// Göç betiğinin biçimi (spec §3.1 göç notları, §8.6): ONLINE yok, veri yazımı yok, var olan tabloya ALTER yok.
/// Veritabanı gerekmez (<see cref="IMigrator.GenerateScript"/> bağlantı açmaz); Docker'sız da koşar.
/// </summary>
public sealed class SalesSyncS3PaymentRequestsMigrationScriptTests
{
    [Fact]
    public void Goc_betigi_ONLINE_veri_yazimi_ve_var_olan_tabloya_ALTER_icermez()
    {
        using var db = new LicenseDbContext(new DbContextOptionsBuilder<LicenseDbContext>()
            .UseSqlServer("Server=betik-uretimi;Database=yok").Options);
        var (previous, target) = SalesSyncS3PaymentRequestsMigrationTests.Migrations(db);

        var script = db.GetService<IMigrator>().GenerateScript(previous, target);

        Regex.IsMatch(script, @"\bONLINE\b", RegexOptions.IgnoreCase)
            .Should().BeFalse("Express'te ONLINE indeks işlemi yok (spec §3.1 göç notları)");

        var dataWrites = script.Split('\n')
            .Where(line => Regex.IsMatch(line, @"^\s*(INSERT|UPDATE|DELETE|MERGE)\b", RegexOptions.IgnoreCase))
            .Where(line => !line.Contains("__EFMigrationsHistory", StringComparison.Ordinal))
            .ToList();
        dataWrites.Should().BeEmpty("göç yalnız tablo ekler, veri yazmaz (spec §3.1)");
        Regex.IsMatch(script, @"\bUPDATE\s+\[(?!__EFMigrationsHistory\])", RegexOptions.IgnoreCase)
            .Should().BeFalse("göç hiçbir satırı güncellemez");

        script.Should().Contain("CREATE TABLE [PaymentRequestRecords]");
        script.Should().Contain("CREATE TABLE [PaymentRequestMembers]");
        script.Should().Contain("CREATE TABLE [PaymentRequestRecordOrders]");
        script.Should().Contain($"CREATE UNIQUE INDEX [{PaymentRequestRecord.SendingIndexName}]");
        Regex.IsMatch(script, @"ALTER\s+TABLE\s+\[(?!PaymentRequest(Records|Members|RecordOrders)\])",
                RegexOptions.IgnoreCase)
            .Should().BeFalse("var olan tablolar değişmez");
    }
}
