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
/// SalesSyncS1 göçünü (yayın ve etiket senkronu S1, spec 2026-10-13 §3.1)
/// gerçek SQL Server'a karşı, satırları DOLU tablolara karşı doğrular.
///
/// Neden gerekli: göç üç büyük tabloya (Orders, StreamSessions, Shipments)
/// <c>rowversion</c> ekliyor; bu, tablonun HER satırını yeniden yazar ve
/// ancak satır varken gözlenebilir (bkz. <see cref="CustomerProjectionFullSyncMigrationTests"/>).
/// Ayrıca deploy'un otomatik geri alması göçü geri ALMAZ (/ready 60 sn'de
/// gelmezse imaj S1 öncesine döner, şema yeni kalır — spec §8.6). Bu yüzden
/// S1 öncesi imajın kolon listeleriyle yazdığı INSERT/UPDATE'ler yeni şemada
/// da çalışmalı: göç yalnız ekleyici olmalı ve her yeni NOT NULL kolonun bir
/// DEFAULT'u olmalı. Bu dosya göçü ikiye bölüp (hedeften bir önceki noktaya
/// kadar → eski şemaya satır yaz → yalnız hedefi uygula) bunu kanıtlıyor.
/// </summary>
[Collection(SqlServerCollection.Name)]
[Trait("Category", "Testcontainers")]
public sealed class SalesSyncS1MigrationTests
{
    private readonly SqlServerContainerFixture _sql;
    private readonly ITestOutputHelper _output;

    public SalesSyncS1MigrationTests(SqlServerContainerFixture sql, ITestOutputHelper output)
    {
        _sql = sql;
        _output = output;
    }

    [Fact]
    public async Task Eski_satirlara_ChangeSeq_verir_yeni_kolonlari_bos_birakir_ve_eski_imaj_yazmayi_surdurur()
    {
        var connectionString = await _sql.CreateDatabaseAsync();
        await SalesSyncS1MigrationScenario.RunAsync(connectionString, _output);
    }
}

/// <summary>
/// Aynı senaryo prod'un sürümünde: SQL Server Express (spec §10.1-12, "göç
/// Express sürümünde ve ONLINE olmadan koşar; süre ölçülür"). Betik denetimi
/// (<see cref="SalesSyncS1MigrationScriptTests"/>) ONLINE'ı metinden yakalar;
/// bu test göçün Express motorunda gerçekten uygulandığını ve süresini gösterir.
/// </summary>
[Trait("Category", "Testcontainers")]
public sealed class SalesSyncS1MigrationExpressTests : IClassFixture<SqlServerExpressContainerFixture>
{
    private readonly SqlServerExpressContainerFixture _sql;
    private readonly ITestOutputHelper _output;

    public SalesSyncS1MigrationExpressTests(SqlServerExpressContainerFixture sql, ITestOutputHelper output)
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

        await SalesSyncS1MigrationScenario.RunAsync(connectionString, _output);
    }
}

/// <summary>
/// Göç betiğinin biçimi (spec §10.1-12, §3.1 göç notları). Veritabanı
/// gerekmez — <see cref="IMigrator.GenerateScript"/> bağlantı açmaz — bu
/// yüzden container koleksiyonunun dışında ve Docker'sız da koşar.
/// </summary>
public sealed class SalesSyncS1MigrationScriptTests
{
    [Fact]
    public void Goc_betigi_ONLINE_ve_veri_geri_doldurmasi_icermez()
    {
        using var db = new LicenseDbContext(new DbContextOptionsBuilder<LicenseDbContext>()
            .UseSqlServer("Server=betik-uretimi;Database=yok").Options);
        var (previous, target) = SalesSyncS1MigrationScenario.Migrations(db);

        var script = db.GetService<IMigrator>().GenerateScript(previous, target);

        // Prod SQL Server Express: ONLINE indeks işlemi yok; "ONLINE = ON" göçü
        // düşürür, /ready gelmez ve deploy geri döner (imaj döner, şema kalmaz
        // çünkü göç işlemi geri alınır — ama S1 de çıkmamış olur).
        Regex.IsMatch(script, @"\bONLINE\b", RegexOptions.IgnoreCase)
            .Should().BeFalse("Express'te ONLINE indeks işlemi yok (spec §3.1 göç notları)");

        // Geri doldurma göçte yapılmaz; onu sales-release-repair işi yapar
        // (spec §4.9). Göç içindeki bir UPDATE büyük tabloda açılışı uzatır.
        Regex.IsMatch(script, @"^\s*UPDATE\s", RegexOptions.IgnoreCase | RegexOptions.Multiline)
            .Should().BeFalse("S1 göçü veri yazmaz, yalnız şema ekler (spec §3.1)");

        script.Should().Contain("rowversion", "göç üç tabloya ChangeSeq ekler");
    }
}

/// <summary>
/// Senaryo testten ayrı: aynı adımlar başka bir SQL Server sürümüne
/// (ör. Express) karşı da aynen koşabilsin.
/// </summary>
internal static class SalesSyncS1MigrationScenario
{
    /// <summary>Büyükçe bir Orders tablosu: rowversion eklemenin ve
    /// (LicenseId, ChangeSeq) indeksinin süresi günlüğe yazılır — ölçüm,
    /// iddia değil (spec §10.1-12, §12 risk 1). Prod sayısı Burak'ın salt
    /// okunur sorgusundan gelir (§8.1 adım 0).</summary>
    public const int BulkOrderCount = 30_000;

    public static (string Previous, string Target) Migrations(LicenseDbContext db)
    {
        var all = db.Database.GetMigrations().ToList();
        var target = all.Single(m => m.EndsWith("_SalesSyncS1", StringComparison.Ordinal));
        return (all[all.IndexOf(target) - 1], target);
    }

    private sealed record Col(string Name, string Type, bool Nullable);

    private sealed record IndexInfo(string KeyColumns, string? Filter, bool IsUnique);

    // S1 öncesi imajın (EF modelinin) bu tablolara yazdığı TAM kolon
    // listeleri. Testin kendisi bunları göç öncesi şemayla karşılaştırır:
    // liste eksik kalırsa "eski imaj yazabiliyor" iddiası boşa düşerdi.
    private static readonly string[] PreS1StreamSessionColumns =
        ["Id", "LicenseId", "Title", "StartedAt", "EndedAt", "Platforms", "Notes", "UpdatedAt"];

    private static readonly string[] PreS1OrderColumns =
    [
        "Id", "LicenseId", "SessionId", "CustomerId", "Platform", "Username", "DisplayName",
        "MessageText", "Code", "Price", "AddedAt", "PrintedAt", "CancelledAt", "CancelReason",
        "IsShippingFee", "IsBackupPromoted", "IsTentativeBackup", "ProductId", "ProductVariantId",
        "UpdatedAt", "SyncVersion",
    ];

    private static readonly string[] PreS1ShipmentColumns =
        ["Id", "LicenseId", "CustomerId", "Status", "CumulativeAmount", "CreatedAt", "HeldAt", "ShippedAt", "UpdatedAt"];

    // RowVersion (rowversion) INSERT'te verilemez; motor üretir.
    private static readonly string[] PreS1LicenseColumns =
    [
        "Id", "LicenseKey", "CustomerId", "SkuCode", "ActivationSlots", "IssuedAt", "ExpiresAt",
        "RevokedAt", "RevokeReason", "LastActivationAt", "ShopperCode", "ShopperCodeUpdatedAt",
        "PaymentIban", "PaymentAccountHolder", "ShopperAppEnabled",
    ];

    /// <summary>Göçün var olan tablolara eklemesi beklenen kolonlar — ne eksik
    /// ne fazla (spec §3.1, yalnız S1 satırları). Tür <c>INFORMATION_SCHEMA</c>
    /// biçiminde: rowversion orada "timestamp" görünür.</summary>
    private static readonly Dictionary<string, Col[]> ExpectedNewColumns = new()
    {
        ["StreamSessions"] =
        [
            new("OwnerReplicaId", "uniqueidentifier", true),
            new("OwnerMachineName", "nvarchar(128)", true),
            new("ForceEndedBy", "nvarchar(64)", true),
            new("ReleasedAt", "datetimeoffset", true),
            new("UnsentLabelCount", "int", true),
            new("ChangeSeq", "timestamp", false),
        ],
        ["Orders"] =
        [
            new("ParentOrderId", "uniqueidentifier", true),
            new("ShipmentId", "uniqueidentifier", true),
            new("OriginReplicaId", "uniqueidentifier", true),
            new("PrintBatchId", "nvarchar(40)", true),
            new("PrintSeq", "int", true),
            new("PriceCorrectedAt", "datetimeoffset", true),
            new("PaidPrice", "decimal(18,2)", true),
            new("SessionReleasedAt", "datetimeoffset", true),
            new("PriceSetAt", "datetimeoffset", true),
            new("CancelSetAt", "datetimeoffset", true),
            new("BackupSetAt", "datetimeoffset", true),
            new("ShipmentSetAt", "datetimeoffset", true),
            new("TextScrubbedAt", "datetimeoffset", true),
            new("ChangeSeq", "timestamp", false),
        ],
        ["Shipments"] =
        [
            new("ChangeSeq", "timestamp", false),
            new("Version", "int", false),
            new("AmountDerivedAt", "datetimeoffset", true),
            new("CommandedAt", "datetimeoffset", true),
            new("OriginReplicaId", "uniqueidentifier", true),
        ],
        ["Licenses"] =
        [
            new("SalesSyncMode", "tinyint", false),
            new("SalesSyncModeChangedAt", "datetimeoffset", true),
            new("LastLegacySalesPushAt", "datetimeoffset", true),
        ],
    };

    private static readonly Col[] ExpectedWpfReplicaColumns =
    [
        new("LicenseId", "uniqueidentifier", false),
        new("ReplicaId", "uniqueidentifier", false),
        new("MachineName", "nvarchar(128)", true),
        new("HardwareFingerprint", "nvarchar(128)", true),
        new("ClientVersion", "nvarchar(32)", true),
        new("Protocol", "tinyint", false),
        new("FeatureLevel", "tinyint", false),
        new("SeenMode", "tinyint", true),
        new("SeenModeAt", "datetimeoffset", true),
        new("SeenModeOnAt", "datetimeoffset", true),
        new("PreviousReplicaId", "uniqueidentifier", true),
        new("FirstSeenAt", "datetimeoffset", false),
        new("LastSeenAt", "datetimeoffset", false),
        new("BackfillCompletedAt", "datetimeoffset", true),
        new("PaymentHistoryCompletedAt", "datetimeoffset", true),
        new("BackfillDivergedLocal", "int", false),
        new("BackfillDivergedServer", "int", false),
        new("ReportedThreshold", "decimal(18,2)", true),
        new("ReportedFee", "decimal(18,2)", true),
    ];

    private static readonly string[] FeedTables = ["StreamSessions", "Orders", "Shipments"];

    private sealed record Seed(
        Guid LicenseId, Guid AccountId, Guid EndedSessionId, Guid LiveSessionId,
        Guid PendingShipmentId, Guid HeldShipmentId, Guid SampleOrderId);

    public static async Task RunAsync(string connectionString, ITestOutputHelper output)
    {
        await using var db = new LicenseDbContext(
            new DbContextOptionsBuilder<LicenseDbContext>().UseSqlServer(connectionString).Options);
        var migrator = db.GetService<IMigrator>();
        var (previous, target) = Migrations(db);

        // Hedeften BİR ÖNCEKİ noktaya kadar: S1 kolonları ve WpfReplicas yok.
        await migrator.MigrateAsync(previous);

        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();

        var before = await ColumnsAsync(conn, ExpectedNewColumns.Keys);
        (await ColumnsAsync(conn, ["WpfReplicas"]))["WpfReplicas"].Should().BeEmpty("göç öncesi tablo yok");
        AssertPreS1ListsMatchSchema(before);

        var seed = await SeedPreS1Async(conn);

        var stopwatch = Stopwatch.StartNew();
        await migrator.MigrateAsync(target);
        stopwatch.Stop();
        output.WriteLine(
            $"SalesSyncS1 göçü: {await ScalarAsync<string>(conn, "SELECT CAST(SERVERPROPERTY('Edition') AS nvarchar(128))")}, " +
            $"Orders={BulkOrderCount}, StreamSessions=2, Shipments=2 → {stopwatch.ElapsedMilliseconds} ms");

        // 1. Yalnız ekleyici: önceki kolonlar aynen duruyor, yeni kolonlar tam
        //    beklenen küme (tür ve boşluk kabulüyle).
        var after = await ColumnsAsync(conn, ExpectedNewColumns.Keys);
        foreach (var (table, expected) in ExpectedNewColumns)
        {
            var oldNames = before[table].Select(c => c.Name).ToHashSet();
            after[table].Where(c => oldNames.Contains(c.Name)).Should().BeEquivalentTo(before[table],
                $"{table}: S1 öncesi kolonlar değişmez (yalnız ekleyici göç)");
            after[table].Where(c => !oldNames.Contains(c.Name)).Should().BeEquivalentTo(expected,
                $"{table}: S1 kolonları spec §3.1 ile aynı");
        }
        (await ColumnsAsync(conn, ["WpfReplicas"]))["WpfReplicas"].Should().BeEquivalentTo(ExpectedWpfReplicaColumns);

        // 2. ChangeSeq: motor her var olan satıra kendi değerini verir — sıfır
        //    değil ve satır başına benzersiz (rowversion veritabanı genelinde
        //    tekildir, üç tablo arasında da çakışmaz).
        foreach (var table in FeedTables)
        {
            var (rows, distinct, nonPositive) = await ChangeSeqStatsAsync(conn, table);
            rows.Should().BeGreaterThan(0, $"{table} tohumlandı");
            nonPositive.Should().Be(0, $"{table}: rowversion motor tarafından atanır, sıfır kalmamalı");
            distinct.Should().Be(rows, $"{table}: rowversion satır başına benzersizdir");
        }
        (await ScalarAsync<int>(conn, """
            SELECT COUNT(*) - COUNT(DISTINCT s) FROM (
                SELECT ChangeSeq s FROM StreamSessions UNION ALL
                SELECT ChangeSeq FROM Orders UNION ALL
                SELECT ChangeSeq FROM Shipments) u
            """)).Should().Be(0, "rowversion veritabanı genelinde tekildir");

        // 3. Var olan satırlarda yeni nullable kolonlar NULL (geri doldurma
        //    göçte değil, onarım işinde — spec §4.9); NOT NULL olanlar DEFAULT'ta.
        foreach (var (table, columns) in ExpectedNewColumns)
            foreach (var column in columns.Where(c => c.Nullable))
                (await ScalarAsync<int>(conn, $"SELECT COUNT(*) FROM [{table}] WHERE [{column.Name}] IS NOT NULL"))
                    .Should().Be(0, $"{table}.{column.Name} göçte doldurulmaz");
        (await ScalarAsync<int>(conn, "SELECT COUNT(*) FROM Licenses WHERE SalesSyncMode <> 0")).Should().Be(0);
        (await ScalarAsync<int>(conn, "SELECT COUNT(*) FROM Shipments WHERE Version <> 0")).Should().Be(0);

        // 4. EF modeli şemayla aynı: varlıklar yeni kolonlarla okunabiliyor.
        var license = await db.Licenses.AsNoTracking().SingleAsync(l => l.Id == seed.LicenseId);
        license.SalesSyncMode.Should().Be(SalesSyncMode.Off);
        license.SalesSyncModeChangedAt.Should().BeNull();
        license.LastLegacySalesPushAt.Should().BeNull();

        var sessions = await db.StreamSessions.AsNoTracking().Where(s => s.LicenseId == seed.LicenseId).ToListAsync();
        sessions.Should().HaveCount(2).And.OnlyContain(s =>
            s.ChangeSeq > 0 && s.OwnerReplicaId == null && s.OwnerMachineName == null &&
            s.ForceEndedBy == null && s.ReleasedAt == null && s.UnsentLabelCount == null);

        var shipments = await db.Shipments.AsNoTracking().Where(s => s.LicenseId == seed.LicenseId).ToListAsync();
        shipments.Should().HaveCount(2).And.OnlyContain(s =>
            s.ChangeSeq > 0 && s.Version == 0 && s.AmountDerivedAt == null &&
            s.CommandedAt == null && s.OriginReplicaId == null);

        var order = await db.Orders.AsNoTracking().SingleAsync(o => o.Id == seed.SampleOrderId);
        order.ChangeSeq.Should().BeGreaterThan(0);
        order.ShipmentId.Should().BeNull();
        order.SessionReleasedAt.Should().BeNull();
        order.PaidPrice.Should().BeNull();

        // 5. İndeksler (spec §3.1): akış imleci üç tabloda; canlı/devrediliyor
        //    listesi ve dosya türetmesi filtreli.
        (await IndexAsync(conn, "StreamSessions", "IX_StreamSessions_LicenseId_ChangeSeq"))
            .Should().Be(new IndexInfo("LicenseId,ChangeSeq", null, false));
        (await IndexAsync(conn, "StreamSessions", "IX_StreamSessions_LicenseId_Unreleased"))
            .Should().Be(new IndexInfo("LicenseId", "([ReleasedAt] IS NULL)", false));
        (await IndexAsync(conn, "Orders", "IX_Orders_LicenseId_ChangeSeq"))
            .Should().Be(new IndexInfo("LicenseId,ChangeSeq", null, false));
        (await IndexAsync(conn, "Orders", "IX_Orders_LicenseId_ShipmentId"))
            .Should().Be(new IndexInfo("LicenseId,ShipmentId", "([ShipmentId] IS NOT NULL)", false));
        (await IndexAsync(conn, "Shipments", "IX_Shipments_LicenseId_ChangeSeq"))
            .Should().Be(new IndexInfo("LicenseId,ChangeSeq", null, false));
        (await IndexAsync(conn, "WpfReplicas", "PK_WpfReplicas"))
            .Should().Be(new IndexInfo("LicenseId,ReplicaId", null, true), "kopya satırı lisans başına (spec §3.1)");
        (await IndexAsync(conn, "WpfReplicas", "IX_WpfReplicas_LicenseId_LastSeenAt"))
            .Should().Be(new IndexInfo("LicenseId,LastSeenAt", null, false));
        (await ScalarAsync<string>(conn, """
            SELECT delete_referential_action_desc FROM sys.foreign_keys
            WHERE parent_object_id = OBJECT_ID('WpfReplicas') AND referenced_object_id = OBJECT_ID('Licenses')
            """)).Should().Be("CASCADE");

        // 6. WpfReplicas sayaçları DEFAULT 0: kolonları vermeyen INSERT'te 0.
        await ExecAsync(conn, """
            INSERT INTO WpfReplicas (LicenseId, ReplicaId, Protocol, FeatureLevel, FirstSeenAt, LastSeenAt)
            VALUES (@licenseId, @replicaId, 2, 1, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET())
            """, ("@licenseId", seed.LicenseId), ("@replicaId", Guid.NewGuid()));
        var replica = await db.WpfReplicas.AsNoTracking().SingleAsync(r => r.LicenseId == seed.LicenseId);
        replica.BackfillDivergedLocal.Should().Be(0);
        replica.BackfillDivergedServer.Should().Be(0);
        replica.SeenMode.Should().BeNull();

        // 7. Eski imaj yazmayı sürdürür (genişlet/daralt, spec §8.6): S1
        //    öncesi kolon listeleriyle INSERT ve UPDATE geçer, NOT NULL yeni
        //    kolonlar DEFAULT'a düşer ve her yazım ChangeSeq'i ilerletir — eski
        //    imajın geri alma penceresinde yazdığı satır da akışa girer (DM3).
        await AssertOldImageKeepsWritingAsync(conn, seed);
    }

    private static void AssertPreS1ListsMatchSchema(Dictionary<string, List<Col>> before)
    {
        Names(before["StreamSessions"]).Should().BeEquivalentTo(PreS1StreamSessionColumns);
        Names(before["Orders"]).Should().BeEquivalentTo(PreS1OrderColumns);
        Names(before["Shipments"]).Should().BeEquivalentTo(PreS1ShipmentColumns);
        Names(before["Licenses"]).Except(["RowVersion"]).Should().BeEquivalentTo(PreS1LicenseColumns);

        static IEnumerable<string> Names(IEnumerable<Col> columns) => columns.Select(c => c.Name);
    }

    private static async Task<Seed> SeedPreS1Async(SqlConnection conn)
    {
        var accountId = Guid.NewGuid();
        var licenseId = Guid.NewGuid();
        var endedSessionId = Guid.NewGuid();
        var liveSessionId = Guid.NewGuid();
        var pendingShipmentId = Guid.NewGuid();
        var heldShipmentId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        // Customers'ın NOT NULL kolonları; SkuCode "STD" göç zincirinde tohumlu.
        await ExecAsync(conn, """
            INSERT INTO Customers (Id, Email, Name, PasswordHash, AuthVersion, CreatedAt, Unsubscribed)
            VALUES (@id, @email, N'Göç Testi Yayıncı', @ozet, 0, @now, 0)
            """,
            ("@id", accountId), ("@email", $"yayinci-{Guid.NewGuid():N}@example.test"),
            ("@ozet", $"h-{Guid.NewGuid():N}"), ("@now", now));

        await InsertLicensePreS1Async(conn, licenseId, accountId, now);

        await ExecAsync(conn, $"""
            INSERT INTO StreamSessions ({string.Join(", ", PreS1StreamSessionColumns)})
            VALUES (@ended, @licenseId, N'Bitmiş yayın', @started, @now, N'tiktok', NULL, @now),
                   (@live, @licenseId, N'Canlı yayın', @now, NULL, N'tiktok,instagram', NULL, @now)
            """,
            ("@ended", endedSessionId), ("@live", liveSessionId), ("@licenseId", licenseId),
            ("@started", now.AddHours(-3)), ("@now", now));

        await ExecAsync(conn, $"""
            INSERT INTO Shipments ({string.Join(", ", PreS1ShipmentColumns)})
            VALUES (@pending, @licenseId, N'c1', 0, 250.00, @now, NULL, NULL, @now),
                   (@held, @licenseId, N'c2', 1, 120.00, @now, @now, NULL, @now)
            """,
            ("@pending", pendingShipmentId), ("@held", heldShipmentId), ("@licenseId", licenseId), ("@now", now));

        // Toplu Orders: küme tabanlı tek INSERT (satır satır gidiş-dönüş yok).
        // Metinler kişisel veri değil: sentetik kullanıcı adı ve ürün kodu.
        await ExecAsync(conn, $"""
            WITH n AS (
                SELECT TOP (@count) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS i
                FROM sys.all_objects a CROSS JOIN sys.all_objects b)
            INSERT INTO Orders ({string.Join(", ", PreS1OrderColumns)})
            SELECT NEWID(), @licenseId, CASE WHEN i % 2 = 0 THEN @ended ELSE @live END,
                   CONCAT(N'c', i % 500), N'tiktok', CONCAT(N'kullanici', i % 500), NULL,
                   CONCAT(N'A', i % 50), CONCAT(N'A', i % 50), 100.00 + (i % 7),
                   DATEADD(SECOND, i, @now), CASE WHEN i % 3 = 0 THEN @now END, NULL, NULL,
                   0, 0, 0, NULL, NULL, @now, 1
            FROM n
            """,
            ("@count", BulkOrderCount), ("@licenseId", licenseId),
            ("@ended", endedSessionId), ("@live", liveSessionId), ("@now", now));

        var sampleOrderId = await ScalarAsync<Guid>(conn, "SELECT TOP (1) Id FROM Orders ORDER BY AddedAt");

        return new Seed(licenseId, accountId, endedSessionId, liveSessionId,
            pendingShipmentId, heldShipmentId, sampleOrderId);
    }

    private static Task InsertLicensePreS1Async(SqlConnection conn, Guid licenseId, Guid accountId, DateTimeOffset now) =>
        ExecAsync(conn, $"""
            INSERT INTO Licenses ({string.Join(", ", PreS1LicenseColumns)})
            VALUES (@id, @licenseKey, @customerId, 'STD', 1, @now, @expiresAt,
                    NULL, NULL, NULL, NULL, NULL, NULL, NULL, 0)
            """,
            ("@id", licenseId), ("@licenseKey", $"LIC-{Guid.NewGuid():N}"), ("@customerId", accountId),
            ("@now", now), ("@expiresAt", now.AddYears(1)));

    private static async Task AssertOldImageKeepsWritingAsync(SqlConnection conn, Seed seed)
    {
        var now = DateTimeOffset.UtcNow;
        var dbtsBefore = await ScalarAsync<long>(conn, "SELECT CAST(@@DBTS AS bigint)");

        var newSessionId = Guid.NewGuid();
        await ExecAsync(conn, $"""
            INSERT INTO StreamSessions ({string.Join(", ", PreS1StreamSessionColumns)})
            VALUES (@id, @licenseId, N'Geri alma penceresinde açılan', @now, NULL, N'tiktok', NULL, @now)
            """, ("@id", newSessionId), ("@licenseId", seed.LicenseId), ("@now", now));
        (await ExecAsync(conn, "UPDATE StreamSessions SET EndedAt = @now, UpdatedAt = @now WHERE Id = @id",
            ("@id", seed.LiveSessionId), ("@now", now))).Should().Be(1);

        var newOrderId = Guid.NewGuid();
        await ExecAsync(conn, $"""
            INSERT INTO Orders ({string.Join(", ", PreS1OrderColumns)})
            VALUES (@id, @licenseId, @sessionId, N'c7', N'tiktok', N'kullanici7', NULL, N'B1', N'B1', 90.00,
                    @now, NULL, NULL, NULL, 0, 0, 0, NULL, NULL, @now, 1)
            """, ("@id", newOrderId), ("@licenseId", seed.LicenseId), ("@sessionId", newSessionId), ("@now", now));
        // Eski imajın eşzamanlılık jetonlu güncellemesi (Order.SyncVersion).
        (await ExecAsync(conn, """
            UPDATE Orders SET CancelledAt = @now, CancelReason = N'iptal', UpdatedAt = @now, SyncVersion = SyncVersion + 1
            WHERE Id = @id AND SyncVersion = 1
            """, ("@id", seed.SampleOrderId), ("@now", now))).Should().Be(1);

        var newShipmentId = Guid.NewGuid();
        await ExecAsync(conn, $"""
            INSERT INTO Shipments ({string.Join(", ", PreS1ShipmentColumns)})
            VALUES (@id, @licenseId, N'c7', 0, 90.00, @now, NULL, NULL, @now)
            """, ("@id", newShipmentId), ("@licenseId", seed.LicenseId), ("@now", now));
        (await ExecAsync(conn, "UPDATE Shipments SET Status = 3, ShippedAt = @now, UpdatedAt = @now WHERE Id = @id",
            ("@id", seed.PendingShipmentId), ("@now", now))).Should().Be(1);

        var newLicenseId = Guid.NewGuid();
        await InsertLicensePreS1Async(conn, newLicenseId, seed.AccountId, now);
        (await ExecAsync(conn, "UPDATE Licenses SET LastActivationAt = @now WHERE Id = @id",
            ("@id", seed.LicenseId), ("@now", now))).Should().Be(1);

        (await ScalarAsync<byte>(conn, "SELECT SalesSyncMode FROM Licenses WHERE Id = @id", ("@id", newLicenseId)))
            .Should().Be(0, "eski imajın açtığı lisans mod Off doğar (DEFAULT 0)");
        (await ScalarAsync<int>(conn, "SELECT Version FROM Shipments WHERE Id = @id", ("@id", newShipmentId)))
            .Should().Be(0, "eski imajın açtığı dosya sürüm 0 doğar (DEFAULT 0)");

        foreach (var (table, id) in new[]
                 {
                     ("StreamSessions", newSessionId), ("StreamSessions", seed.LiveSessionId),
                     ("Orders", newOrderId), ("Orders", seed.SampleOrderId),
                     ("Shipments", newShipmentId), ("Shipments", seed.PendingShipmentId),
                 })
        {
            (await ScalarAsync<long>(conn, $"SELECT CAST(ChangeSeq AS bigint) FROM [{table}] WHERE Id = @id", ("@id", id)))
                .Should().BeGreaterThan(dbtsBefore, $"{table} {id}: eski imajın yazımı ChangeSeq'i ilerletir, akış onu görür");
        }
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

    private static async Task<(int Rows, int Distinct, int NonPositive)> ChangeSeqStatsAsync(SqlConnection conn, string table)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT COUNT(*), COUNT(DISTINCT ChangeSeq), SUM(CASE WHEN CAST(ChangeSeq AS bigint) > 0 THEN 0 ELSE 1 END)
            FROM [{table}]
            """;
        await using var reader = await cmd.ExecuteReaderAsync();
        await reader.ReadAsync();
        return (reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2));
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
        cmd.CommandTimeout = 300;
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
