using System.Diagnostics;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Data.Migrations;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Domain.Bank;
using OrderDeck.LicenseServer.Services.CustomerSync;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;
using Xunit.Abstractions;

namespace OrderDeck.LicenseServer.Tests.Data;

/// <summary>
/// BankDecisionsS2a göçü (banka havalesi onayı S2a, spec 2026-10-10 §2, §14) gerçek SQL Server'da, DOLU tablolara karşı:
/// hedeften bir önceki göçe kadar kur → eski şemaya satır yaz → yalnız hedefi uygula. Deploy'un otomatik geri alması göçü
/// geri ALMAZ: S2a öncesi imajın kolon listeleriyle yazdığı satırlar yeni şemada çalışmalı. Geri doldurma yanlış müşteriye
/// yazmamalı ve yeniden koşulabilmeli (S2b göçü aynı metni koşar).
/// </summary>
[Collection(SqlServerCollection.Name)]
[Trait("Category", "Testcontainers")]
public sealed class BankDecisionsS2aMigrationTests
{
    private readonly SqlServerContainerFixture _sql;
    private readonly ITestOutputHelper _output;

    public BankDecisionsS2aMigrationTests(SqlServerContainerFixture sql, ITestOutputHelper output)
    {
        _sql = sql;
        _output = output;
    }

    [Fact]
    public async Task Goc_genisletir_geri_doldurur_eski_imaj_yazmayi_surdurur()
        => await BankDecisionsS2aMigrationScenario.RunAsync(await _sql.CreateDatabaseAsync(), _output);
}

/// <summary>Aynı senaryo prod'un sürümünde: SQL Server Express (ONLINE indeks işlemi yok).</summary>
[Collection(SqlServerExpressCollection.Name)]
[Trait("Category", "Testcontainers")]
public sealed class BankDecisionsS2aMigrationExpressTests
{
    private readonly SqlServerExpressContainerFixture _sql;
    private readonly ITestOutputHelper _output;

    public BankDecisionsS2aMigrationExpressTests(SqlServerExpressContainerFixture sql, ITestOutputHelper output)
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

        await BankDecisionsS2aMigrationScenario.RunAsync(connectionString, _output);
    }
}

/// <summary>Göçün işlemleri ve betiği — bağlantı açmaz, Docker'sız koşar.</summary>
public sealed class BankDecisionsS2aMigrationScriptTests
{
    private static readonly IReadOnlyList<MigrationOperation> Up = new BankDecisionsS2a().UpOperations;

    [Fact]
    public void Yalniz_genisletir()
    {
        Up.Should().NotContain(o => o is DropIndexOperation || o is DropColumnOperation || o is DropTableOperation
            || o is AlterColumnOperation || o is RenameColumnOperation || o is DeleteDataOperation);
        Up.OfType<CreateIndexOperation>().Should().Contain(i =>
            i.Name == "IX_CustomerIbanMemories_LicenseId_IbanHash_WpfCustomerId" && i.IsUnique);
        Up.OfType<CreateTableOperation>().Select(t => t.Name).Should().BeEquivalentTo("BankDecisionEvents", "BankIbanStates",
            "PaymentExpectationAllocations", "PaymentShipmentSteps", "PaymentShipmentStepOrders");
        Up.OfType<AddColumnOperation>().Where(c => !c.IsNullable).Should().OnlyContain(c => c.DefaultValue != null,
            "geri alınan imajın INSERT'i sütunu vermez");
    }

    [Fact]
    public void Geri_doldurma_sutundan_sonra_indeksten_once_ve_UpdatedAt_Status_yazmaz()
    {
        var ops = Up.ToList();
        var column = ops.FindIndex(o => o is AddColumnOperation { Table: "Payments", Name: "WpfCustomerId" });
        var sql = ops.FindIndex(o => o is SqlOperation s && s.Sql.Contains("UPDATE p"));
        var index = ops.FindIndex(o => o is CreateIndexOperation { Name: "IX_Payments_LicenseId_WpfCustomerId" });
        (column >= 0 && column < sql && sql < index).Should().BeTrue();
        ops.OfType<SqlOperation>().Should().HaveCount(2)
            .And.OnlyContain(s => !s.Sql.Contains("UpdatedAt") && !s.Sql.Contains("[Status]") && !s.Sql.Contains("DELETE"));
    }

    [Fact]
    public void Betikte_ONLINE_yok_varsayilanlar_var()
    {
        using var db = new LicenseDbContext(new DbContextOptionsBuilder<LicenseDbContext>()
            .UseSqlServer("Server=betik-uretimi;Database=yok").Options);
        var (previous, target) = BankDecisionsS2aMigrationScenario.Migrations(db);

        var script = db.GetService<IMigrator>().GenerateScript(previous, target);

        Regex.IsMatch(script, @"\bONLINE\b", RegexOptions.IgnoreCase).Should().BeFalse("prod Express");
        script.Should().Contain("[Source] nvarchar(16) NOT NULL DEFAULT N'Receipt'")
            .And.Contain("[Decision] nvarchar(24) NOT NULL DEFAULT N'None'")
            .And.Contain("[DecisionRevision] int NOT NULL DEFAULT 0")
            .And.Contain("[SupportedByIbanMemory] bit NOT NULL DEFAULT")
            .And.Contain("[BankQuestionsEnabled] bit NOT NULL DEFAULT")
            .And.Contain("[BankAutoApproveEnabled] bit NOT NULL DEFAULT");
    }
}

/// <summary>Senaryo testten ayrı: aynı adımlar Express'e karşı da koşar.</summary>
internal static class BankDecisionsS2aMigrationScenario
{
    /// <summary>Toplu dekont sayısı. Geri doldurma ve Payments indeksleri satır sayısıyla büyür; süre bu sayılarla günlüğe
    /// yazılır — ölçüm, iddia değil (emsal <see cref="PersonBalanceS1MigrationScenario.BulkTransactionCount"/>). Prod
    /// sayıları merge'den önce sahibin salt okunur sorgusundan gelir.</summary>
    public const int BulkPaymentCount = 30_000;

    private const int PaymentsPerBulkShopper = 3;
    private const int BulkShopperCount = BulkPaymentCount / PaymentsPerBulkShopper;

    private const string NarrowIbanIndex = "IX_CustomerIbanMemories_LicenseId_IbanHash";
    private const string WideIbanIndex = "IX_CustomerIbanMemories_LicenseId_IbanHash_WpfCustomerId";
    private const string OpenStepIndex = "UX_PaymentShipmentSteps_Open";

    private static readonly string[] NewTables =
    [
        "BankDecisionEvents", "BankIbanStates", "PaymentExpectationAllocations", "PaymentShipmentSteps",
        "PaymentShipmentStepOrders",
    ];

    // S2a öncesi imajın bu tablolara yazdığı TAM kolon listeleri. Senaryo bunları göç öncesi şemayla karşılaştırır: liste
    // eksik kalırsa "eski imaj yazabiliyor" iddiası boşa düşerdi. Bugünkü model bu üç tabloya eski şemada yazamaz. Göçten
    // sonra da yalnız gereken kolonlarıyla okunurlar: model sonraki göçlerin kolonlarını da taşır (emsal SalesSyncS1).
    internal static readonly string[] PreS2aPaymentColumns =
    [
        "Id", "LicenseId", "PayerName", "Amount", "PaidAt", "ReferansNo", "PdfHash", "Status", "CreatedAt", "UpdatedAt",
        "ApprovedAt", "ApprovedByCustomerId", "RejectedAt", "RejectedByCustomerId", "RejectReason", "ShipmentDirective",
        "ShopperId", "MediaObjectKey", "MediaContentType", "MetadataHash", "RecipientIban", "RecipientName", "FraudFlags",
        "ParserConfidence", "PdfPurgedAt",
    ];

    internal static readonly string[] PreS2aMatchColumns =
    [
        "Id", "LicenseId", "BankTransactionId", "ProposedWpfCustomerId", "Layer", "Confidence", "Evidence", "Status",
        "PaymentId", "ActualWpfCustomerId", "DecidedAt", "IbanConfirmedAt", "CreatedAt", "UpdatedAt",
    ];

    // RowVersion (rowversion) INSERT'te verilemez; motor üretir.
    internal static readonly string[] PreS2aLicenseColumns =
    [
        "Id", "LicenseKey", "CustomerId", "SkuCode", "ActivationSlots", "IssuedAt", "ExpiresAt", "RevokedAt", "RevokeReason",
        "LastActivationAt", "ShopperCode", "ShopperCodeUpdatedAt", "PaymentIban", "PaymentAccountHolder", "ShopperAppEnabled",
        "SalesSyncMode", "SalesSyncModeChangedAt", "PersonBalancePoolSince",
    ];

    /// <summary>Bir dekont ve göçün ona yazması gereken müşteri.</summary>
    private sealed record Backfilled(string Case, Guid PaymentId, Guid? Customer);

    /// <summary>Tohumlanacak bir bağ; <paramref name="Day"/> katılma sırası.</summary>
    private sealed record Bond(Guid License, Guid? Customer, int Day, bool Left = false);

    private sealed record Remembered(string Case, Guid Memory, Guid Customer);

    private sealed record Seed(
        Guid Account, Guid License, Guid Other,
        Guid P, Guid W, Guid T,
        IReadOnlyList<Backfilled> Payments, IReadOnlyList<Remembered> Memories, string HashA,
        Guid ShopperA, Guid GonePayment, Guid GoneBond, Guid UnprovenPayment, Guid UnprovenBond, Guid MovedPayment, Guid MovedBond,
        Guid MatchBefore, Guid TransactionAfter);

    private sealed record Stamp(int Status, DateTimeOffset UpdatedAt);

    private sealed record IndexInfo(string KeyColumns, string? Filter, bool IsUnique);

    private sealed record ForeignKeyInfo(string Parent, string Column, string Referenced, string OnDelete);

    public static (string Previous, string Target) Migrations(LicenseDbContext db)
    {
        var all = db.Database.GetMigrations().ToList();
        var target = all.Single(m => m.EndsWith("_BankDecisionsS2a", StringComparison.Ordinal));
        return (all[all.IndexOf(target) - 1], target);
    }

    public static async Task RunAsync(string connectionString, ITestOutputHelper output)
    {
        await using var db = NewDb(connectionString);
        var migrator = db.GetService<IMigrator>();
        var (previous, target) = Migrations(db);
        await migrator.MigrateAsync(previous);

        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();
        (await ColumnsAsync(conn, "Payments")).Should().BeEquivalentTo(PreS2aPaymentColumns);
        (await ColumnsAsync(conn, "PaymentMatches")).Should().BeEquivalentTo(PreS2aMatchColumns);
        (await ColumnsAsync(conn, "Licenses")).Except(["RowVersion"]).Should().BeEquivalentTo(PreS2aLicenseColumns);

        var seed = await SeedAsync(connectionString, conn);
        var bulkLicense = await InsertPreS2aLicenseAsync(conn, seed.Account);
        await SeedBulkAsync(conn, bulkLicense);
        var stampsBefore = await StampsAsync(conn, seed);
        var foreignKeysBefore = await ForeignKeysAsync(conn);

        var watch = Stopwatch.StartNew();
        await migrator.MigrateAsync(target);
        watch.Stop();
        output.WriteLine(
            $"BankDecisionsS2a göçü: {await ScalarAsync<string>(conn, "SELECT CAST(SERVERPROPERTY('Edition') AS nvarchar(128))")}, " +
            $"Payments={await CountAsync(conn, "Payments")}, ShopperBroadcasterLinks={await CountAsync(conn, "ShopperBroadcasterLinks")}, " +
            $"WpfCustomerProjections={await CountAsync(conn, "WpfCustomerProjections")} → {watch.ElapsedMilliseconds} ms");

        (await db.Database.GetAppliedMigrationsAsync()).Should().Contain(target);
        await AssertBackfillAsync(db, conn, seed, bulkLicense, stampsBefore);
        await AssertIbanMemoriesAsync(connectionString, db, conn, seed);
        await AssertRederivationAsync(db, conn, seed);
        await AssertNewTablesAsync(conn, seed, foreignKeysBefore);
        await AssertOldImageKeepsWritingAsync(db, conn, seed);
        await AssertOpenStepIndexAsync(connectionString, seed);
    }

    /// <summary>Geri doldurma: her dekont bağının asıl kaydına, kendi lisansında; damga ve durum yerinde.</summary>
    private static async Task AssertBackfillAsync(
        LicenseDbContext db, SqlConnection conn, Seed seed, Guid bulkLicense, Dictionary<Guid, Stamp> stampsBefore)
    {
        var customers = await db.Payments.AsNoTracking()
            .Where(p => p.LicenseId == seed.License || p.LicenseId == seed.Other)
            .Select(p => new { p.Id, p.WpfCustomerId }).ToDictionaryAsync(p => p.Id, p => p.WpfCustomerId);
        customers.Keys.Should().BeEquivalentTo(seed.Payments.Select(x => x.PaymentId));
        foreach (var expected in seed.Payments)
            customers[expected.PaymentId].Should().Be(expected.Customer, expected.Case);

        (await CountAsync(conn, "Payments WHERE Source <> N'Receipt' OR ApprovalSource IS NOT NULL OR ApprovedByOperatorId IS NOT NULL OR BankTransactionId IS NOT NULL"))
            .Should().Be(0, "var olan satırı varsayılan doldurur; onayın yolu bilinmiyor");
        stampsBefore.Values.Select(s => s.Status).Distinct().Should().HaveCount(3, "üç durum da tohumlandı");
        (await StampsAsync(conn, seed)).Should().Equal(stampsBefore,
            "UpdatedAt ilerlerse bütün dekont geçmişi masaüstüne yeniden iner");
        await ShouldMatchResolversAsync(db, seed.License, seed.Other);

        (await CountAsync(conn,
                "Payments p JOIN WpfCustomerProjections c ON c.Id = p.WpfCustomerId AND c.MergedIntoId IS NULL WHERE p.LicenseId = @license",
                ("@license", bulkLicense)))
            .Should().Be(BulkPaymentCount, "ölçüm boş koşmadı: her toplu dekont bir asıl kayda bağlandı");
    }

    /// <summary>Kopyada kalmış IBAN bağı asıl kayda geçer; silinmiş ya da başka lisansın kaydına geçmez. İki tekil de durur.</summary>
    private static async Task AssertIbanMemoriesAsync(string connectionString, LicenseDbContext db, SqlConnection conn, Seed seed)
    {
        var memories = await db.CustomerIbanMemories.AsNoTracking()
            .Where(m => m.LicenseId == seed.License).ToDictionaryAsync(m => m.Id, m => m.WpfCustomerId);
        memories.Keys.Should().BeEquivalentTo(seed.Memories.Select(x => x.Memory));
        foreach (var expected in seed.Memories)
            memories[expected.Memory].Should().Be(expected.Customer, expected.Case);

        (await IndexAsync(conn, "CustomerIbanMemories", NarrowIbanIndex))
            .Should().Be(new IndexInfo("LicenseId,IbanHash", null, true), "dar tekil S3b'ye kadar durur");
        (await IndexAsync(conn, "CustomerIbanMemories", WideIbanIndex))
            .Should().Be(new IndexInfo("LicenseId,IbanHash,WpfCustomerId", null, true));

        await using var rival = NewDb(connectionString);
        rival.CustomerIbanMemories.Add(Memory(seed.License, seed.W, seed.HashA));
        var error = await SqlErrorAsync(() => rival.SaveChangesAsync());
        error.Number.Should().Be(2601);
        error.Message.Should().Contain($"'{NarrowIbanIndex}'", "iki müşteriye eşzamanlı öğretmenin tek hakemi dar tekildir");
    }

    /// <summary>S2b'nin dayanağı: geri alma penceresinin kalıntısını (eski imaj sütunu bilmez) aynı metin kapatır.</summary>
    private static async Task AssertRederivationAsync(LicenseDbContext db, SqlConnection conn, Seed seed)
    {
        (await RederiveAsync(conn)).Should().Be(0, "göçten hemen sonra yeniden türetme hiçbir satıra dokunmaz");

        // Kalıntı elle kurulur: silinen kişinin bağı gider (1), devralmada T W'nin kopyası olur; kanıtı düşen bağ boşalır
        // (2), kanıtlı bağ W'ye geçer (3). Eski imaj dekontlara dokunmaz. (4) aynı pencerede gönderilen dekont boş doğar.
        await ExecAsync(conn, "DELETE FROM ShopperBroadcasterLinks WHERE Id = @id", ("@id", seed.GoneBond));
        await ExecAsync(conn, "UPDATE WpfCustomerProjections SET MergedIntoId = @w WHERE Id = @t", ("@w", seed.W), ("@t", seed.T));
        await ExecAsync(conn, "UPDATE ShopperBroadcasterLinks SET WpfCustomerId = NULL WHERE Id = @id", ("@id", seed.UnprovenBond));
        await ExecAsync(conn, "UPDATE ShopperBroadcasterLinks SET WpfCustomerId = @w WHERE Id = @id",
            ("@w", seed.W), ("@id", seed.MovedBond));
        var latePayment = await InsertPreS2aPaymentAsync(
            conn, seed.License, seed.ShopperA, PaymentStatus.Pending, DateTimeOffset.UtcNow);
        (await CustomersAsync(conn, seed.UnprovenPayment, seed.MovedPayment, latePayment))
            .Should().Equal([seed.T, seed.T, null], "eski imajın bıraktığı hâl: iki dekont kopyada, yenisi boş");
        var stamps = await StampsAsync(conn, seed);

        (await RederiveAsync(conn)).Should().Be(4);

        (await CustomersAsync(conn, seed.GonePayment, seed.UnprovenPayment, seed.MovedPayment, latePayment))
            .Should().Equal([null, null, seed.W, seed.P],
                "silinen bağ ⇒ boş; boşalan bağ ⇒ boş (kopyada kalsaydı W'ye çözülürdü); taşınan bağ ⇒ W; yeni dekont ⇒ P");
        (await RederiveAsync(conn)).Should().Be(0);
        (await StampsAsync(conn, seed)).Should().Equal(stamps);
        await ShouldMatchResolversAsync(db, seed.License, seed.Other);
    }

    private static async Task AssertNewTablesAsync(SqlConnection conn, Seed seed, List<ForeignKeyInfo> foreignKeysBefore)
    {
        foreach (var table in NewTables)
            (await CountAsync(conn, table)).Should().Be(0, $"{table} boş doğar");

        var foreignKeys = await ForeignKeysAsync(conn);
        foreignKeys.Should().Contain(foreignKeysBefore);
        foreignKeys.Except(foreignKeysBefore).Should().BeEquivalentTo(
            [
                new ForeignKeyInfo("BankDecisionEvents", "LicenseId", "Licenses", "CASCADE"),
                new ForeignKeyInfo("BankIbanStates", "LicenseId", "Licenses", "CASCADE"),
                new ForeignKeyInfo("PaymentExpectationAllocations", "PaymentId", "Payments", "CASCADE"),
                new ForeignKeyInfo("PaymentShipmentSteps", "PaymentId", "Payments", "CASCADE"),
                new ForeignKeyInfo("PaymentShipmentStepOrders", "StepId", "PaymentShipmentSteps", "CASCADE"),
            ],
            "yeni bağlar FK'sız Guid; tablo başına tek kaskad yolu");

        (await ScalarAsync<string>(conn,
                "SELECT COLLATION_NAME FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'PaymentShipmentSteps' AND COLUMN_NAME = 'CustomerKey'"))
            .Should().Be("Latin1_General_100_BIN2");

        var rawIban = await SqlErrorAsync(() => ExecAsync(conn, """
            INSERT INTO BankIbanStates (Id, LicenseId, IbanHash, Shared, Disputed, LastTransferAt, UpdatedAt)
            VALUES (NEWID(), @license, @hash, 0, 0, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET())
            """, ("@license", seed.License), ("@hash", "g" + NewHash()[1..])));
        rawIban.Number.Should().Be(547);
        rawIban.Message.Should().Contain("CK_BankIbanStates_IbanHash_Hex64");

        var unpaid = await SqlErrorAsync(() => ExecAsync(conn, """
            INSERT INTO PaymentExpectationAllocations (Id, LicenseId, PaymentId, ExpectationId, Kind, Amount, CreatedAt)
            VALUES (NEWID(), @license, NULL, NEWID(), N'Payment', 10, SYSDATETIMEOFFSET())
            """, ("@license", seed.License)));
        unpaid.Number.Should().Be(547);
        unpaid.Message.Should().Contain("CK_PaymentExpectationAllocations_Kind_Payment");
    }

    /// <summary>Otomatik geri alma: S2a öncesi imaj üç tabloya yazmayı sürdürür, vermediği sütun DB varsayılanına düşer.</summary>
    private static async Task AssertOldImageKeepsWritingAsync(LicenseDbContext db, SqlConnection conn, Seed seed)
    {
        (await CountAsync(conn, "PaymentMatches WHERE Id = @id AND Decision = N'None' AND DecisionRevision = 0 AND SupportedByIbanMemory = 0 AND MatcherVersion IS NULL",
            ("@id", seed.MatchBefore))).Should().Be(1, "göçten önceki öneri satırını da varsayılan doldurur");
        (await CountAsync(conn, "Licenses WHERE BankQuestionsEnabled <> 0 OR BankAutoApproveEnabled <> 0 OR BankQuestionsSince IS NOT NULL"))
            .Should().Be(0, "var olan lisans kapalı doğar");

        var licenseId = await InsertPreS2aLicenseAsync(conn, seed.Account);
        var paymentId = await InsertPreS2aPaymentAsync(
            conn, seed.License, shopperId: null, PaymentStatus.Pending, DateTimeOffset.UtcNow);
        var matchId = await InsertPreS2aMatchAsync(conn, seed.License, seed.TransactionAfter, DateTimeOffset.UtcNow);

        // Metin olarak da: boş dize EF'te 0. değer okunur, ama SQL süzgeci satırı bulamaz.
        (await CountAsync(conn, "Payments WHERE Id = @id AND Source = N'Receipt'", ("@id", paymentId))).Should().Be(1);
        (await CountAsync(conn, "PaymentMatches WHERE Id = @id AND Decision = N'None'", ("@id", matchId))).Should().Be(1);

        (await db.Payments.AsNoTracking().Where(p => p.Id == paymentId)
                .Select(p => new { p.Source, p.ApprovalSource, p.ApprovedByOperatorId, p.WpfCustomerId, p.BankTransactionId })
                .SingleAsync())
            .Should().Be(new
            {
                Source = PaymentSource.Receipt, ApprovalSource = (PaymentApprovalSource?)null, ApprovedByOperatorId = (Guid?)null,
                WpfCustomerId = (Guid?)null, BankTransactionId = (Guid?)null,
            });
        (await db.PaymentMatches.AsNoTracking().Where(m => m.Id == matchId)
                .Select(m => new { m.Decision, m.DecisionRevision, m.SupportedByIbanMemory, m.MatcherVersion }).SingleAsync())
            .Should().Be(new
            {
                Decision = PaymentMatchDecision.None, DecisionRevision = 0, SupportedByIbanMemory = false,
                MatcherVersion = (int?)null,
            });
        (await db.Licenses.AsNoTracking().Where(l => l.Id == licenseId)
                .Select(l => new { l.BankQuestionsEnabled, l.BankAutoApproveEnabled, l.BankQuestionsSince }).SingleAsync())
            .Should().Be(new
            {
                BankQuestionsEnabled = false, BankAutoApproveEnabled = false, BankQuestionsSince = (DateTimeOffset?)null,
            });

        (await ExecAsync(conn, """
            UPDATE Payments SET Status = 1, ApprovedAt = @now, ApprovedByCustomerId = @account, UpdatedAt = @now
            WHERE Id = @id AND Status = 0
            """, ("@now", DateTimeOffset.UtcNow), ("@account", seed.Account), ("@id", paymentId)))
            .Should().Be(1, "eski imajın jetonlu onayı");
    }

    /// <summary>Ödeme × müşteri başına tek açık adım; kapanan ve çözülen yeni adımı engellemez.</summary>
    private static async Task AssertOpenStepIndexAsync(string connectionString, Seed seed)
    {
        foreach (var settled in new[] { PaymentShipmentStepState.Closed, PaymentShipmentStepState.Reverting })
        {
            var first = Step(seed, $"{Guid.NewGuid():N}");
            await using (var db = NewDb(connectionString))
            {
                db.PaymentShipmentSteps.Add(first);
                await db.SaveChangesAsync();
            }

            await using (var db = NewDb(connectionString))
            {
                db.PaymentShipmentSteps.Add(Step(seed, first.CustomerKey));
                var error = await SqlErrorAsync(() => db.SaveChangesAsync());
                error.Number.Should().Be(2601);
                error.Message.Should().Contain($"'{OpenStepIndex}'");
            }

            await using (var db = NewDb(connectionString))
            {
                (await db.PaymentShipmentSteps.SingleAsync(s => s.Id == first.Id)).State = settled;
                await db.SaveChangesAsync();
                db.PaymentShipmentSteps.Add(Step(seed, first.CustomerKey));
                (await Record.ExceptionAsync(() => db.SaveChangesAsync())).Should().BeNull($"{settled} adım yenisini engellemez");
            }
        }
    }

    /// <summary>SQL'in sonucu üretim kuralınınkiyle aynı: <c>PaymentMatchReconciler.ResolveWpfCustomerAsync</c>'in (private)
    /// bağ seçimi ve <see cref="CustomerIdResolver"/>. Toplu tohumun lisansı hariç; <c>JoinedAt</c> eşitliği
    /// tohumlanmaz.</summary>
    private static async Task ShouldMatchResolversAsync(LicenseDbContext db, params Guid[] licenseIds)
    {
        foreach (var p in await db.Payments.AsNoTracking().Where(x => licenseIds.Contains(x.LicenseId))
                     .Select(x => new { x.LicenseId, x.ShopperId, x.WpfCustomerId }).ToListAsync())
        {
            var bond = await db.ShopperBroadcasterLinks.AsNoTracking()
                .Where(l => l.ShopperId == p.ShopperId && l.LicenseId == p.LicenseId && l.WpfCustomerId != null)
                .OrderByDescending(l => l.JoinedAt).Select(l => l.WpfCustomerId).FirstOrDefaultAsync();
            Guid? expected = bond is { } id ? await new CustomerIdResolver(db).CanonicalOfAsync(p.LicenseId, id, default) : null;
            p.WpfCustomerId.Should().Be(expected);
        }
    }

    /// <summary>Yeni sütunu olmayan tablolar (projeksiyon, shopper, bağ, hareket, IBAN hafızası) EF ile, üç eski tablo eski
    /// kolon listesiyle.</summary>
    private static async Task<Seed> SeedAsync(string connectionString, SqlConnection conn)
    {
        var account = await InsertAccountAsync(conn);
        var license = await InsertPreS2aLicenseAsync(conn, account);
        var other = await InsertPreS2aLicenseAsync(conn, account);
        var t0 = DateTimeOffset.UtcNow.AddDays(-30);

        await using var db = NewDb(connectionString);
        Guid Customer(Guid licenseId, Guid? mergedInto = null, bool purged = false)
        {
            var row = new WpfCustomerProjection
            {
                Id = Guid.NewGuid(), LicenseId = licenseId, Platform = "tiktok", Username = $"m{Guid.NewGuid():N}",
                MergedIntoId = mergedInto, UpdatedAt = t0,
            };
            if (purged) row.MarkPurged(t0);
            db.WpfCustomerProjections.Add(row);
            return row.Id;
        }

        Shopper Person(params Bond[] bonds)
        {
            var shopper = new Shopper
            {
                Id = Guid.NewGuid(), FullName = "Göç Testi", Phone = TestPhone.NewE164(), PasswordHash = $"h-{Guid.NewGuid():N}",
                Address = "-", CreatedAt = t0, UpdatedAt = t0,
            };
            db.Shoppers.Add(shopper);
            db.ShopperBroadcasterLinks.AddRange(bonds.Select(b => new ShopperBroadcasterLink
            {
                Id = Guid.NewGuid(), ShopperId = shopper.Id, LicenseId = b.License, Platform = "tiktok",
                Username = $"m{Guid.NewGuid():N}", WpfCustomerId = b.Customer, JoinedAt = t0.AddDays(b.Day),
                LeftAt = b.Left ? t0.AddDays(b.Day).AddHours(1) : null,
            }));
            return shopper;
        }

        var payments = new List<Backfilled>();
        async Task<Guid> Receipt(string name, Guid licenseId, Shopper? shopper, Guid? customer)
        {
            // Durumlar sırayla döner; damgalar ayrı ve geçmişte: göçün yazdığı her değişiklik görünür.
            var id = await InsertPreS2aPaymentAsync(
                conn, licenseId, shopper?.Id, (PaymentStatus)(payments.Count % 3), t0.AddMinutes(payments.Count));
            payments.Add(new Backfilled(name, id, customer));
            return id;
        }

        Guid BondOf(Shopper shopper) => db.ShopperBroadcasterLinks.Local.Single(l => l.ShopperId == shopper.Id).Id;

        // K3→K2→K1→P: üç adımlık zincir. K6→K4→F→G: F ve G başka lisansta, zincir F'de durur. K5→R: R silinmiş. T asıl
        // kayıttır; yeniden türetmede W'nin kopyası olur.
        Guid p = Customer(license), k1 = Customer(license, p), k2 = Customer(license, k1), k3 = Customer(license, k2);
        Guid q = Customer(license), w = Customer(license), t = Customer(license);
        Guid g = Customer(other), f = Customer(other, g), k4 = Customer(license, f), k6 = Customer(license, k4);
        Guid r = Customer(license, purged: true), k5 = Customer(license, r);
        Guid m = Customer(other), unknown = Guid.NewGuid();

        var a = Person(new Bond(license, q, 1, Left: true), new Bond(license, k2, 2));
        await Receipt("(a) eski ayrılmış bağ Q'ya, yeni bağ K2'ye", license, a, p);
        await Receipt("(b) yalnız ayrılmış bağ", license, Person(new Bond(license, q, 1, Left: true)), q);
        await Receipt("(c) yeni bağ müşterisiz, eskisi Q'ya", license,
            Person(new Bond(license, q, 1, Left: true), new Bond(license, null, 2)), q);
        await Receipt("(d) bağ yalnız başka lisansta", license, Person(new Bond(other, m, 1)), null);
        await Receipt("(e) shopper'sız dekont", license, null, null);
        await Receipt("(f) bağsız shopper", license, Person(), null);
        await Receipt("(g) üç adımlık zincir", license, Person(new Bond(license, k3, 1)), p);
        await Receipt("(h) projeksiyonu olmayan Id", license, Person(new Bond(license, unknown, 1)), unknown);
        var i = Person(new Bond(license, q, 1), new Bond(other, m, 2));
        await Receipt("(i) iki lisanslı shopper, birinci lisans", license, i, q);
        await Receipt("(i) iki lisanslı shopper, ikinci lisans", other, i, m);
        await Receipt("(j) zincir ikinci adımda başka lisansa çıkıyor", license, Person(new Bond(license, k4, 1)), f);
        await Receipt("(j) bağ doğrudan başka lisansın kopyasına", license, Person(new Bond(license, f, 1)), f);
        await Receipt("(j) zincir üçüncü adımda başka lisansa çıkıyor", license, Person(new Bond(license, k6, 1)), f);

        Shopper gone = Person(new Bond(license, q, 1)), unproven = Person(new Bond(license, t, 1)), moved = Person(new Bond(license, t, 1));
        var gonePayment = await Receipt("yeniden türetme (1)", license, gone, q);
        var unprovenPayment = await Receipt("yeniden türetme (2)", license, unproven, t);
        var movedPayment = await Receipt("yeniden türetme (3)", license, moved, t);

        var memories = new List<Remembered>();
        string Remember(string name, Guid customer, Guid expected)
        {
            var memory = Memory(license, customer, NewHash());
            db.CustomerIbanMemories.Add(memory);
            memories.Add(new Remembered(name, memory.Id, expected));
            return memory.IbanHash;
        }

        var hashA = Remember("A: kopyadaki bağ asıl kayda geçer", k2, p);
        Remember("B: asıl kayıttaki bağ yerinde", q, q);
        Remember("C: silinmiş asıl kayda taşınmaz", k5, k5);
        Remember("üç adımlık zincir", k3, p);
        Remember("başka lisansın kaydına taşınmaz", k4, k4);

        BankTransaction before = Transaction(license, 1, t0), after = Transaction(license, 2, t0);
        db.BankTransactions.AddRange(before, after);
        await db.SaveChangesAsync();
        var matchBefore = await InsertPreS2aMatchAsync(conn, license, before.Id, t0);

        return new Seed(account, license, other, p, w, t, payments, memories, hashA, a.Id,
            gonePayment, BondOf(gone), unprovenPayment, BondOf(unproven), movedPayment, BondOf(moved), matchBefore, after.Id);
    }

    /// <summary>
    /// Toplu satırlar ayrı bir lisansa, küme tabanlı INSERT'lerle (satır satır gidiş-dönüş yok). Hepsi tek komutta:
    /// parametreli komutta açılan geçici tablo komutla birlikte gider. Metinler kişisel veri değil: sentetik kullanıcı adı ve
    /// sıra numarası. Müşterilerin onda biri kopya; her kişinin bir etkin bağı, yarısının başka müşteriye eski bir ayrılmış
    /// bağı var.
    /// </summary>
    private static async Task SeedBulkAsync(SqlConnection conn, Guid licenseId)
    {
        var values = new Dictionary<string, string>
        {
            ["Id"] = "NEWID()", ["LicenseId"] = "@license", ["PayerName"] = "N'Toplu'", ["Amount"] = "100.00 + k.i % 7",
            ["PaidAt"] = "@now", ["ReferansNo"] = "CONCAT(N'r', k.i, N'-', n.x)", ["Status"] = "n.x", ["CreatedAt"] = "@now",
            ["UpdatedAt"] = "@now", ["ShipmentDirective"] = "0", ["ShopperId"] = "k.Shopper", ["FraudFlags"] = "N''",
            ["ParserConfidence"] = "N'Unknown'",
        };
        await ExecAsync(conn, $"""
            SELECT TOP (@count) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS i, NEWID() AS Customer, NEWID() AS Shopper
            INTO #kisi FROM sys.all_objects a CROSS JOIN sys.all_objects b;

            INSERT INTO WpfCustomerProjections
                (Id, LicenseId, Platform, Username, IdentityKey, MergedIntoId, UpdatedAt,
                 CreatedByShopper, IsBlacklisted, RecipientPaysActive, SmsConsent, WhatsAppConsent)
            SELECT k.Customer, @license, N'tiktok', CONCAT(N'kullanici', k.i), CONCAT(N'kullanici', k.i),
                   CASE WHEN k.i % 10 = 0 THEN o.Customer END, @now, 0, 0, 0, 0, 0
            FROM #kisi k LEFT JOIN #kisi o ON o.i = k.i - 1;

            INSERT INTO Shoppers
                (Id, FullName, Phone, PasswordHash, Address, AuthVersion, CreatedAt, UpdatedAt,
                 NotificationsEnabledBroadcast, NotificationsEnabledOrders, NotificationsEnabledPayments, SmsConsent)
            SELECT k.Shopper, N'Toplu', CONCAT(N'toplu', k.i), N'-', N'-', 0, @now, @now, 0, 0, 0, 0 FROM #kisi k;

            INSERT INTO ShopperBroadcasterLinks (Id, ShopperId, LicenseId, Platform, Username, WpfCustomerId, JoinedAt, LeftAt)
            SELECT NEWID(), k.Shopper, @license, N'tiktok', CONCAT(N'kullanici', k.i), k.Customer, @now, NULL FROM #kisi k
            UNION ALL
            SELECT NEWID(), k.Shopper, @license, N'tiktok', CONCAT(N'kullanici', k.i), o.Customer, DATEADD(DAY, -1, @now), @now
            FROM #kisi k JOIN #kisi o ON o.i = k.i + 1 WHERE k.i % 2 = 0;

            INSERT INTO Payments ({string.Join(", ", PreS2aPaymentColumns)})
            SELECT {string.Join(", ", PreS2aPaymentColumns.Select(c => values.GetValueOrDefault(c, "NULL")))}
            FROM #kisi k CROSS JOIN (VALUES (0), (1), (2)) n(x);
            """,
            ("@count", BulkShopperCount), ("@license", licenseId), ("@now", DateTimeOffset.UtcNow.AddDays(-1)));
        (await CountAsync(conn, "Payments WHERE LicenseId = @license", ("@license", licenseId))).Should().Be(BulkPaymentCount);
    }

    private static LicenseDbContext NewDb(string connectionString) =>
        new(new DbContextOptionsBuilder<LicenseDbContext>().UseSqlServer(connectionString).Options);

    private static string NewHash() => $"{Guid.NewGuid():N}{Guid.NewGuid():N}";

    private static CustomerIbanMemory Memory(Guid licenseId, Guid customerId, string ibanHash) => new()
    {
        Id = Guid.NewGuid(), LicenseId = licenseId, WpfCustomerId = customerId, IbanHash = ibanHash, IbanMasked = "TR**",
        LearnedFrom = IbanMemorySource.HumanApproval, CreatedAt = DateTimeOffset.UtcNow,
    };

    private static BankTransaction Transaction(Guid licenseId, long obifinId, DateTimeOffset at) => new()
    {
        Id = Guid.NewGuid(), LicenseId = licenseId, ObifinId = obifinId, ObifinAccountId = 1, BankaKodu = "test",
        Direction = BankTransactionDirection.Incoming, Amount = 100m, OccurredAt = at, FetchedAt = at,
    };

    private static PaymentShipmentStep Step(Seed seed, string customerKey) => new()
    {
        Id = Guid.NewGuid(), PaymentId = seed.GonePayment, LicenseId = seed.License, CustomerKey = customerKey,
        State = PaymentShipmentStepState.Waiting, CreatedAt = DateTimeOffset.UtcNow, StateChangedAt = DateTimeOffset.UtcNow,
    };

    private static async Task<Guid> InsertAccountAsync(SqlConnection conn)
    {
        var id = Guid.NewGuid();
        await ExecAsync(conn, """
            INSERT INTO Customers (Id, Email, Name, PasswordHash, AuthVersion, CreatedAt, Unsubscribed)
            VALUES (@id, @email, N'Banka S2a Göç Testi', @ozet, 0, @now, 0)
            """,
            ("@id", id), ("@email", $"yayinci-{Guid.NewGuid():N}@example.test"), ("@ozet", $"h-{Guid.NewGuid():N}"),
            ("@now", DateTimeOffset.UtcNow));
        return id;
    }

    private static async Task<Guid> InsertPreS2aLicenseAsync(SqlConnection conn, Guid accountId)
    {
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await InsertAsync(conn, "Licenses", PreS2aLicenseColumns,
            ("Id", id), ("LicenseKey", $"s2a-{Guid.NewGuid():N}"), ("CustomerId", accountId), ("SkuCode", "STD"),
            ("ActivationSlots", 1), ("IssuedAt", now), ("ExpiresAt", now.AddYears(1)), ("ShopperAppEnabled", false),
            ("SalesSyncMode", (byte)0));
        return id;
    }

    private static async Task<Guid> InsertPreS2aPaymentAsync(
        SqlConnection conn, Guid licenseId, Guid? shopperId, PaymentStatus status, DateTimeOffset at)
    {
        var id = Guid.NewGuid();
        await InsertAsync(conn, "Payments", PreS2aPaymentColumns,
            ("Id", id), ("LicenseId", licenseId), ("PayerName", "Göç Testi"), ("Amount", 100m), ("PaidAt", at),
            ("ReferansNo", $"r-{Guid.NewGuid():N}"), ("Status", (int)status), ("CreatedAt", at), ("UpdatedAt", at),
            ("ShipmentDirective", 0), ("ShopperId", shopperId), ("FraudFlags", ""), ("ParserConfidence", "Unknown"));
        return id;
    }

    private static async Task<Guid> InsertPreS2aMatchAsync(
        SqlConnection conn, Guid licenseId, Guid transactionId, DateTimeOffset at)
    {
        var id = Guid.NewGuid();
        await InsertAsync(conn, "PaymentMatches", PreS2aMatchColumns,
            ("Id", id), ("LicenseId", licenseId), ("BankTransactionId", transactionId),
            ("Layer", nameof(PaymentMatchLayer.None)), ("Confidence", 0m), ("Status", nameof(PaymentMatchStatus.NoProposal)),
            ("CreatedAt", at), ("UpdatedAt", at));
        return id;
    }

    /// <summary>Listedeki HER kolonu veren INSERT; değeri verilmeyen kolon NULL.</summary>
    private static async Task InsertAsync(
        SqlConnection conn, string table, string[] columns, params (string Column, object? Value)[] values)
    {
        var given = values.Where(v => v.Value is not null).ToDictionary(v => v.Column, v => v.Value!);
        var affected = await ExecAsync(conn,
            $"INSERT INTO {table} ({string.Join(", ", columns)}) " +
            $"VALUES ({string.Join(", ", columns.Select(c => given.ContainsKey(c) ? "@" + c : "NULL"))})",
            given.Select(v => ("@" + v.Key, v.Value)).ToArray());
        affected.Should().Be(1);
    }

    private static Task<int> RederiveAsync(SqlConnection conn) => ExecAsync(conn, BankDecisionsS2a.BackfillPaymentCustomers);

    private static async Task<List<Guid?>> CustomersAsync(SqlConnection conn, params Guid[] paymentIds)
    {
        var customers = new List<Guid?>();
        foreach (var id in paymentIds)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT WpfCustomerId FROM Payments WHERE Id = @id";
            cmd.Parameters.AddWithValue("@id", id);
            customers.Add(await cmd.ExecuteScalarAsync() is Guid customer ? customer : null);
        }
        return customers;
    }

    private static async Task<Dictionary<Guid, Stamp>> StampsAsync(SqlConnection conn, Seed seed)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id, Status, UpdatedAt FROM Payments WHERE LicenseId IN (@license, @other)";
        cmd.Parameters.AddWithValue("@license", seed.License);
        cmd.Parameters.AddWithValue("@other", seed.Other);
        var stamps = new Dictionary<Guid, Stamp>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            stamps.Add(reader.GetGuid(0), new Stamp(reader.GetInt32(1), reader.GetDateTimeOffset(2)));
        return stamps;
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

    private static async Task<List<ForeignKeyInfo>> ForeignKeysAsync(SqlConnection conn)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT OBJECT_NAME(k.parent_object_id), c.name, OBJECT_NAME(k.referenced_object_id), k.delete_referential_action_desc
            FROM sys.foreign_keys k
            JOIN sys.foreign_key_columns kc ON kc.constraint_object_id = k.object_id
            JOIN sys.columns c ON c.object_id = kc.parent_object_id AND c.column_id = kc.parent_column_id
            """;
        var keys = new List<ForeignKeyInfo>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            keys.Add(new ForeignKeyInfo(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        return keys;
    }

    private static async Task<SqlException> SqlErrorAsync(Func<Task> act)
    {
        var thrown = await Record.ExceptionAsync(act);
        var error = (thrown is DbUpdateException update ? update.InnerException : thrown) as SqlException;
        error.Should().NotBeNull("veritabanı reddetmeli, dönen: {0}", thrown);
        return error!;
    }

    private static Task<int> CountAsync(SqlConnection conn, string from, params (string Name, object Value)[] parameters) =>
        ScalarAsync<int>(conn, $"SELECT COUNT(*) FROM {from}", parameters);

    private static async Task<int> ExecAsync(SqlConnection conn, string sql, params (string Name, object Value)[] parameters)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 300;
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        return await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<T> ScalarAsync<T>(SqlConnection conn, string sql, params (string Name, object Value)[] parameters)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        return (T)(await cmd.ExecuteScalarAsync())!;
    }
}
