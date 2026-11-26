using System.Diagnostics;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Data.Migrations;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Domain.Bank;
using OrderDeck.LicenseServer.Services.Bank;
using OrderDeck.LicenseServer.Services.ShopperLinking;
using OrderDeck.LicenseServer.Services.ShopperPayments;
using OrderDeck.LicenseServer.Services.Shoppers;
using OrderDeck.LicenseServer.Services.Sync;
using OrderDeck.LicenseServer.Tests.Services.Bank;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;
using Xunit.Abstractions;
using S2a = OrderDeck.LicenseServer.Tests.Data.BankDecisionsS2aMigrationScenario;

namespace OrderDeck.LicenseServer.Tests.Data;

/// <summary>
/// BankPersonMatchingS2b göçü (banka havalesi onayı S2b; spec 2026-10-10 §2.3, §2.5) gerçek SQL Server'da, DOLU tablolara karşı:
/// hedeften bir önceki göçe (<c>_BankDecisionsS2a</c>) kadar kur → satır yaz (şema aynı, tohum EF ile) → yalnız hedefi uygula.
/// Göç bir kez koşar ve geri alınmaz: kopyada kalmış IBAN bağı onu gösteren eşleşme satırlarıyla BİRLİKTE asıl kayda gider (yarım
/// taşıma IBAN talebini, K-6'yı, dekont onayını ve silmeyi bozar; S2a planı Karar 5), dekontun müşterisi kanıt kuralıyla yeniden
/// türetilir. Yalnız Developer'da koşar (S2b planı Karar 15: DDL'siz DML; aynı biçimli türetme S2a'da Express'te ölçüldü). Değerler
/// üretilir.
/// </summary>
[Collection(SqlServerCollection.Name)]
[Trait("Category", "Testcontainers")]
public sealed class BankPersonMatchingS2bMigrationTests
{
    private readonly SqlServerContainerFixture _sql;
    private readonly ITestOutputHelper _output;

    public BankPersonMatchingS2bMigrationTests(SqlServerContainerFixture sql, ITestOutputHelper output)
    {
        _sql = sql;
        _output = output;
    }

    [Fact]
    public async Task Goc_bag_ve_karari_birlikte_tasir_dekont_musterisini_yeniden_turetir()
        => await BankPersonMatchingS2bMigrationScenario.RunAsync(await _sql.CreateDatabaseAsync(), _output);

    /// <summary>Göçten sonra gerçek hizmetler taşınan bağı ve kararı birlikte bulur: S2a planı Karar 5'in dört ölçümünün tersi.</summary>
    [Fact]
    public async Task Tasinan_bag_ve_karar_dort_yolda_bulunur()
        => await BankPersonMatchingS2bMigrationScenario.ServicesAsync(await _sql.CreateDatabaseAsync());
}

/// <summary>Göçün işlemleri — bağlantı açmaz, Docker'sız koşar.</summary>
public sealed class BankPersonMatchingS2bMigrationScriptTests
{
    private static readonly IReadOnlyList<MigrationOperation> Up = new BankPersonMatchingS2b().UpOperations;

    [Fact]
    public void Yalniz_veri_yazar_iki_deyim_jeton_ve_durum_yok()
    {
        Up.Should().HaveCount(2).And.AllBeOfType<SqlOperation>("şema değişmez: S2a imajı aynı şemada çalışır");
        Up.Cast<SqlOperation>().Select(s => s.Sql).Should()
            .Equal(BankPersonMatchingS2b.MoveCopyBonds, BankPersonMatchingS2b.RederivePaymentCustomers);
        Up.Cast<SqlOperation>().Should().OnlyContain(s => !Regex.IsMatch(s.Sql, @"UpdatedAt|\[Status\]|\bDELETE\b|\bDROP\b|\bONLINE\b"));
        BankPersonMatchingS2b.RederivePaymentCustomers.Should().Contain("[CreatedByShopper] = 1", "geçici kopyadan zincir izlenmez")
            .And.Contain("p.[Source] = N'Receipt'").And.Contain("ORDER BY x.[JoinedAt] DESC, x.[Id] DESC");
        new BankPersonMatchingS2b().DownOperations.Should().BeEmpty("göç geri alınmaz");
    }
}

/// <summary>Senaryo testten ayrı (desen <see cref="BankDecisionsS2aMigrationScenario"/>; bağlantı yardımcıları oradan).</summary>
internal static class BankPersonMatchingS2bMigrationScenario
{
    private const string NarrowIbanIndex = "IX_CustomerIbanMemories_LicenseId_IbanHash";
    private const string WideIbanIndex = "IX_CustomerIbanMemories_LicenseId_IbanHash_WpfCustomerId";

    /// <summary>Kopyada kalmış IBAN bağı (<paramref name="Copy"/>), onu öğreten karar (öneri = karar = kopya) ve yalnız önerisi
    /// kopya olan satır. <paramref name="MovesTo"/>: taşınabilir kopyanın asıl kaydı (S2b planı Karar 7); taşınmazsa null.</summary>
    private sealed record BondCase(string Name, Guid Copy, Guid? MovesTo, string Hash, Guid Memory, Guid TeachingMatch,
        Guid ProposalMatch);

    /// <summary>Bir dekont ve göçün ona yazması gereken müşteri. <paramref name="Bank"/>: banka satırı (bağdan türetilmez).</summary>
    private sealed record Rederived(string Case, Guid PaymentId, Guid LicenseId, Guid? ShopperId, Guid? Expected, bool Bank = false);

    private sealed record Seed(Guid License, Guid Other, IReadOnlyList<BondCase> Bonds, IReadOnlyList<Rederived> Payments,
        Guid ProvenPayment, Guid ProvenCanonical);

    private sealed record BondRow(Guid WpfCustomerId, string IbanHash, DateTimeOffset CreatedAt, DateTimeOffset? LastConfirmedAt,
        IbanMemorySource LearnedFrom, Guid? SourceBankTransactionId);

    private sealed record MatchRow(Guid? Proposed, Guid? Actual, PaymentMatchStatus Status, DateTimeOffset UpdatedAt,
        int? MatcherVersion, DateTimeOffset? DecidedAt, DateTimeOffset? IbanConfirmedAt, string? Evidence);

    private sealed record Stamp(PaymentStatus Status, DateTimeOffset UpdatedAt);

    /// <summary>Bir lisansta asıl kayıt P, kopyası K, K'da bağ ve onu öğreten karar.</summary>
    private sealed record Bonded(Guid License, Guid P, Guid K, string Hash, Guid Memory, Guid TeachingMatch);

    public static (string Previous, string Target) Migrations(LicenseDbContext db)
    {
        var all = db.Database.GetMigrations().ToList();
        var target = all.Single(m => m.EndsWith("_BankPersonMatchingS2b", StringComparison.Ordinal));
        return (all[all.IndexOf(target) - 1], target);
    }

    public static async Task RunAsync(string connectionString, ITestOutputHelper output)
    {
        await using var db = S2a.NewDb(connectionString);
        var migrator = db.GetService<IMigrator>();
        var (previous, target) = Migrations(db);
        previous.Should().EndWith("_BankDecisionsS2a", "göç S2a şemasında koşar");
        await migrator.MigrateAsync(previous);

        var seed = await SeedAsync(connectionString);
        var bondsBefore = await BondsAsync(connectionString, seed.License);
        var matchesBefore = await MatchesAsync(connectionString, seed.License);
        var stampsBefore = await StampsAsync(connectionString, seed);

        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();
        var watch = Stopwatch.StartNew();
        await migrator.MigrateAsync(target);
        watch.Stop();
        output.WriteLine(
            $"BankPersonMatchingS2b göçü: {await S2a.ScalarAsync<string>(conn, "SELECT CAST(SERVERPROPERTY('Edition') AS nvarchar(128))")}, " +
            $"CustomerIbanMemories={await S2a.CountAsync(conn, "CustomerIbanMemories")}, " +
            $"PaymentMatches={await S2a.CountAsync(conn, "PaymentMatches")}, Payments={await S2a.CountAsync(conn, "Payments")}, " +
            $"WpfCustomerProjections={await S2a.CountAsync(conn, "WpfCustomerProjections")} → {watch.ElapsedMilliseconds} ms");

        (await db.Database.GetAppliedMigrationsAsync()).Should().Contain(target);
        await AssertBondsAsync(connectionString, seed, bondsBefore, matchesBefore);
        await AssertRederivationAsync(connectionString, seed, stampsBefore);
        await AssertReaderAsync(connectionString, seed);
        await AssertOnceAsync(connectionString, conn);
    }

    /// <summary>Taşınır ve taşınmaz: taşınabilir kopyanın bağı ve onu gösteren satırlar AYNI eşlemeyle asıl kayda; başka hiçbir
    /// alan değişmez (bağın damgaları, kaynağı, öğreten havalesi; satırın durumu, jetonu, sürümü, karar anı, IBAN işareti, kanıtı).
    /// Taşınmayan kopyanın bağı ve satırları yerinde.</summary>
    private static async Task AssertBondsAsync(string connectionString, Seed seed, Dictionary<Guid, BondRow> bondsBefore,
        Dictionary<Guid, MatchRow> matchesBefore)
    {
        var bonds = await BondsAsync(connectionString, seed.License);
        var matches = await MatchesAsync(connectionString, seed.License);
        bonds.Keys.Should().BeEquivalentTo(bondsBefore.Keys, "göç bağ silmez, eklemez");
        matches.Keys.Should().BeEquivalentTo(matchesBefore.Keys);
        foreach (var c in seed.Bonds)
        {
            bondsBefore[c.Memory].WpfCustomerId.Should().Be(c.Copy, $"{c.Name}: tohum kopyada");
            var to = c.MovesTo ?? c.Copy;
            bonds[c.Memory].Should().Be(bondsBefore[c.Memory] with { WpfCustomerId = to }, $"{c.Name}: bağ");
            matches[c.TeachingMatch].Should().Be(matchesBefore[c.TeachingMatch] with { Proposed = to, Actual = to },
                $"{c.Name}: öğreten karar bağla birlikte");
            matches[c.ProposalMatch].Should().Be(matchesBefore[c.ProposalMatch] with { Proposed = to }, $"{c.Name}: yalnız öneri");
        }
    }

    /// <summary>Yeniden türetme (S2a senaryosunun (a)-(j) durumları ve (k)-(n)) ve C# çözücüsüyle eşlik: her dekontta
    /// <see cref="ShopperCustomerResolver.PaymentCustomerAsync"/> aynı müşteriyi verir; yalnız kanıtlı geçici kopyada ayrılır (C# asıl
    /// kayıt, SQL NULL; S2b planı Karar 9). Damga ve durum yerinde.</summary>
    private static async Task AssertRederivationAsync(string connectionString, Seed seed, Dictionary<Guid, Stamp> stampsBefore)
    {
        await using var db = S2a.NewDb(connectionString);
        var customers = await db.Payments.AsNoTracking().Where(p => p.LicenseId == seed.License || p.LicenseId == seed.Other)
            .ToDictionaryAsync(p => p.Id, p => p.WpfCustomerId);
        customers.Keys.Should().BeEquivalentTo(seed.Payments.Select(x => x.PaymentId));
        foreach (var expected in seed.Payments)
            customers[expected.PaymentId].Should().Be(expected.Expected, expected.Case);

        stampsBefore.Values.Select(s => s.Status).Distinct().Should().HaveCount(3, "üç durum da tohumlandı");
        (await StampsAsync(connectionString, seed)).Should().Equal(stampsBefore,
            "UpdatedAt payments/since imlecidir, Status jetondur: göç ikisini de yazmaz");

        foreach (var p in seed.Payments.Where(x => !x.Bank && x.ShopperId is not null))
        {
            var resolved = await ShopperCustomerResolver.PaymentCustomerAsync(db, p.LicenseId, p.ShopperId, CancellationToken.None);
            if (p.PaymentId == seed.ProvenPayment)
                (resolved, customers[p.PaymentId]).Should().Be(((Guid?)seed.ProvenCanonical, (Guid?)null),
                    "SQL telefonu normalleştiremez: kanıtlı geçici kopyada C# asıl kaydı verir, göç müşterisiz bırakır (Karar 9)");
            else
                customers[p.PaymentId].Should().Be(resolved, $"{p.Case}: C# çözücüsüyle aynı");
        }
    }

    /// <summary>Okuyucu eşliği: her durumun özetiyle yeni hareket. Taşınan bağ asıl kaydın IBAN önerisini verir; taşınmayan bağı
    /// kişi düzeyi okuyucu da saymaz (aynı kural, Karar 7).</summary>
    private static async Task AssertReaderAsync(string connectionString, Seed seed)
    {
        var probes = new List<(BondCase Case, BankTransaction Tx)>();
        await using (var db = S2a.NewDb(connectionString))
        {
            var obifinId = await db.BankTransactions.MaxAsync(t => t.ObifinId);
            var at = DateTimeOffset.UtcNow.AddDays(-1);
            foreach (var c in seed.Bonds)
            {
                var tx = new BankTransaction
                {
                    Id = Guid.NewGuid(), LicenseId = seed.License, ObifinId = ++obifinId, ObifinAccountId = 1, BankaKodu = "test",
                    Direction = BankTransactionDirection.Incoming, Amount = 500m, Currency = "TL", OccurredAt = at, FetchedAt = at,
                    Description = "HAVALE ODEME", CounterpartyIbanHash = c.Hash, CounterpartyIbanMasked = "TR**",
                    CounterpartyIbanHashValid = true,
                };
                db.BankTransactions.Add(tx);
                probes.Add((c, tx));
            }
            await db.SaveChangesAsync();
        }

        await using var matchDb = S2a.NewDb(connectionString);
        var matcher = NewMatcher(matchDb);
        foreach (var (c, tx) in probes)
        {
            var row = await matcher.MatchAsync(tx, CancellationToken.None);
            (row.Status, row.ProposedWpfCustomerId, row.Evidence).Should().Be(c.MovesTo is { } canonical
                ? (PaymentMatchStatus.Proposed, (Guid?)canonical, "iban-memory")
                : (PaymentMatchStatus.NoProposal, (Guid?)null, "no-signal"), c.Name);
        }
    }

    /// <summary>Bir kez: taşıma yeniden koşulunca hiçbir bağ ve satır değişmez (dönüş değeri iddia edilmez: <c>ExecuteNonQuery</c>
    /// <c>@CopyMap</c>'e INSERT'i de sayar, kopyalar durduğu için 0 olmaz); türetme 0 satır yazar. İki IBAN tekili de duruyor.</summary>
    private static async Task AssertOnceAsync(string connectionString, SqlConnection conn)
    {
        var bonds = await AllBondsAsync(connectionString);
        var matches = await AllMatchesAsync(connectionString);
        await S2a.ExecAsync(conn, BankPersonMatchingS2b.MoveCopyBonds);
        (await AllBondsAsync(connectionString)).Should().Equal(bonds, "taşınan bağ artık asıl kaydı gösterir");
        (await AllMatchesAsync(connectionString)).Should().Equal(matches, "satır ve jetonu değişmez");
        (await S2a.ExecAsync(conn, BankPersonMatchingS2b.RederivePaymentCustomers)).Should().Be(0);

        (await S2a.IndexAsync(conn, "CustomerIbanMemories", NarrowIbanIndex))
            .Should().Be(new S2a.IndexInfo("LicenseId,IbanHash", null, true), "dar tekil S3b'ye kadar durur");
        (await S2a.IndexAsync(conn, "CustomerIbanMemories", WideIbanIndex))
            .Should().Be(new S2a.IndexInfo("LicenseId,IbanHash,WpfCustomerId", null, true));
    }

    /// <summary>
    /// Göçten sonra gerçek hizmetler, her yol kendi lisansında: (1) bağlantısı K'da kalmış shopper'ın onaylı dekontu aynı özetli
    /// yeni havaleye bağlanır ve taşınan bağı tazeler (S2a'da çelişki dönerdi); (2) shopper silmesi bağı da kararı da bulur, asıl
    /// kaydı siler; (3) IBAN talebi taşınan kararın müşterisini ve işaretini boşaltır; (4) P'ye verilmiş başka elle eşlemenin
    /// kaldırılması bağı silmez, kalan doğrulama taşınan karardır. Taşınmayan geçici kopyada (B) dekont bağdaştırılmaz, silme
    /// yalnız kopyanın satırlarını bulur.
    /// </summary>
    public static async Task ServicesAsync(string connectionString)
    {
        var ct = CancellationToken.None;
        string target;
        await using (var db = S2a.NewDb(connectionString))
        {
            string previous;
            (previous, target) = Migrations(db);
            await db.GetService<IMigrator>().MigrateAsync(previous, ct);
        }

        var now = DateTimeOffset.UtcNow;
        var taught = now.AddDays(-40);
        var recent = now.AddDays(-1);
        var hasher = new BankHasher(Options.Create(new BankOptions { HashKey = $"k-{Guid.NewGuid():N}{Guid.NewGuid():N}" }));
        var iban = BankHasherTests.TestIban();

        Bonded one, three, four, unproven;
        Guid shopperOne, receiptOne, txOne, txFour, shopperB, receiptB, ownBondB, ownDecisionB;
        await using (var db = S2a.NewDb(connectionString))
        {
            var s = new Seeder(db, now.AddDays(-60));
            Bonded NewBonded(string hash, bool provisionalCopy = false)
            {
                var license = s.License();
                var canonical = s.Customer(license);
                var copy = s.Customer(license, canonical, provisionalCopy);
                var source = s.Tx(license, hash, taught);
                return new Bonded(license, canonical, copy, hash, s.Bond(license, copy, source), s.Decision(source, copy));
            }

            one = NewBonded(S2a.NewHash());
            var sOne = s.Shopper(verified: true);
            s.Link(sOne, one.License, one.K, 1);
            shopperOne = sOne.Id;
            receiptOne = s.Payment(one.License, sOne, one.P, PaymentSource.Receipt, PaymentStatus.Approved, recent, 250m);
            txOne = s.Tx(one.License, one.Hash, recent, 250m).Id;

            three = NewBonded(hasher.HashIban(iban)!);

            four = NewBonded(S2a.NewHash());
            txFour = s.Tx(four.License, four.Hash, now.AddDays(-3)).Id;

            // Geçici kopyanın asıl kaydının telefonu boş: shopper'ın doğrulanmış telefonu onu kanıtlamaz.
            unproven = NewBonded(S2a.NewHash(), provisionalCopy: true);
            var sB = s.Shopper(verified: true);
            s.Link(sB, unproven.License, unproven.K, 1);
            shopperB = sB.Id;
            receiptB = s.Payment(unproven.License, sB, null, PaymentSource.Receipt, PaymentStatus.Approved, recent, 333m);
            s.Tx(unproven.License, unproven.Hash, recent, 333m);
            var own = s.Tx(unproven.License, S2a.NewHash(), taught.AddDays(1));
            ownBondB = s.Bond(unproven.License, unproven.P, own);
            ownDecisionB = s.Decision(own, unproven.P);
            await db.SaveChangesAsync(ct);
        }

        await using (var db = S2a.NewDb(connectionString))
            await db.GetService<IMigrator>().MigrateAsync(target, ct);

        // (1) Dekont onayı: öneri asıl kayıt, karar onu doğrular, taşınan bağ tazelenir.
        await using (var db = S2a.NewDb(connectionString))
        {
            var tx = await db.BankTransactions.AsNoTracking().SingleAsync(t => t.Id == txOne, ct);
            var proposal = await NewMatcher(db).MatchAsync(tx, ct);
            (proposal.ProposedWpfCustomerId, proposal.Evidence).Should().Be(((Guid?)one.P, "iban-memory"));
        }
        await using (var db = S2a.NewDb(connectionString))
        {
            var receipt = await db.Payments.AsNoTracking().SingleAsync(p => p.Id == receiptOne, ct);
            await NewReconciler(db).ReconcileApprovalAsync(receipt, ct);
        }
        await using (var db = S2a.NewDb(connectionString))
        {
            var row = await db.PaymentMatches.AsNoTracking().SingleAsync(m => m.BankTransactionId == txOne, ct);
            (row.Status, row.ActualWpfCustomerId, row.PaymentId).Should().Be(
                (PaymentMatchStatus.ConfirmedByHuman, (Guid?)one.P, (Guid?)receiptOne));
            row.IbanConfirmedAt.Should().Be(recent, "karar taşınan bağı doğruladı (S2a'da çelişki dönerdi)");
            var bond = await db.CustomerIbanMemories.AsNoTracking().SingleAsync(m => m.Id == one.Memory, ct);
            (bond.WpfCustomerId, bond.LastConfirmedAt).Should().Be((one.P, (DateTimeOffset?)recent), "bağ tazelendi");
        }

        // (2) Shopper silmesi: bağ ve taşınan karar bulunur, bağlantının asıl kaydı silinir.
        await using (var db = S2a.NewDb(connectionString))
            (await NewPurge(db).PurgeAsync(shopperOne, ct)).Should().NotBeNull();
        await using (var db = S2a.NewDb(connectionString))
        {
            (await db.CustomerIbanMemories.AsNoTracking().CountAsync(m => m.LicenseId == one.License, ct)).Should().Be(0);
            var decision = await db.PaymentMatches.AsNoTracking().SingleAsync(m => m.Id == one.TeachingMatch, ct);
            (decision.ProposedWpfCustomerId, decision.ActualWpfCustomerId, decision.IbanConfirmedAt)
                .Should().Be(((Guid?)null, (Guid?)null, (DateTimeOffset?)null), "taşınan karar da bulundu");
            (await db.WpfCustomerProjections.AsNoTracking().SingleAsync(c => c.Id == one.P, ct)).PurgedAt.Should().NotBeNull();
        }

        // (3) IBAN talebiyle silme: taşınan kararın müşterisi ve işareti boşalır.
        await using (var db = S2a.NewDb(connectionString))
        {
            var service = new BankSubjectRequestService(db);
            var request = await service.CreateAsync(hasher, three.License, iban, BankSubjectRequestKind.Erasure,
                DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1)), "yonetici-1", ct);
            await service.VerifyAsync(request.Id, BankSubjectVerification.RegisteredContactAndBankDocument, "yonetici-1", ct);
            await service.EraseAsync(request.Id, "yonetici-1", ct);
        }
        await using (var db = S2a.NewDb(connectionString))
        {
            var decision = await db.PaymentMatches.AsNoTracking().SingleAsync(m => m.Id == three.TeachingMatch, ct);
            (decision.ActualWpfCustomerId, decision.IbanConfirmedAt).Should().Be(((Guid?)null, (DateTimeOffset?)null));
            (await db.CustomerIbanMemories.AsNoTracking().CountAsync(m => m.LicenseId == three.License, ct)).Should().Be(0);
        }

        // (4) K-6: P'ye elle eşleme bağı tazeler; kaldırılınca süre taşınan karara geri çekilir, bağ silinmez.
        await using (var db = S2a.NewDb(connectionString))
            (await NewReconciler(db).ManualMatchAsync(four.License, txFour, four.P, ct)).Iban
                .Should().Be(ManualMatchIbanOutcome.Refreshed, "bağ asıl kayıtta (S2a'da çelişki dönerdi)");
        await using (var db = S2a.NewDb(connectionString))
            await NewReconciler(db).UnmatchAsync(four.License, txFour, ct, "yonetici-1");
        await using (var db = S2a.NewDb(connectionString))
        {
            var bond = await db.CustomerIbanMemories.AsNoTracking().SingleAsync(m => m.Id == four.Memory, ct);
            (bond.WpfCustomerId, bond.LastConfirmedAt).Should().Be((four.P, (DateTimeOffset?)taught),
                "kalan doğrulama taşınan karardır");
            (await db.DataDestructionRecords.AsNoTracking().CountAsync(r => r.LicenseId == four.License, ct)).Should().Be(0);
        }

        // B: kanıtsız geçici kopya taşınmadı; dekont bağdaştırılmaz, silme yalnız kopyanın satırlarını bulur.
        await using (var db = S2a.NewDb(connectionString))
        {
            var receipt = await db.Payments.AsNoTracking().SingleAsync(p => p.Id == receiptB, ct);
            await NewReconciler(db).ReconcileApprovalAsync(receipt, ct);
        }
        await using (var db = S2a.NewDb(connectionString))
        {
            (await db.PaymentMatches.AsNoTracking().AnyAsync(m => m.PaymentId == receiptB, ct)).Should().BeFalse();
            (await db.PaymentMatchGaps.AsNoTracking().AnyAsync(g => g.PaymentId == receiptB, ct)).Should().BeFalse(
                "müşteri bilinmiyor: bugünkü 'bağ yok' yolu");
        }
        await using (var db = S2a.NewDb(connectionString))
            (await NewPurge(db).PurgeAsync(shopperB, ct)).Should().NotBeNull();
        await using (var db = S2a.NewDb(connectionString))
        {
            (await db.CustomerIbanMemories.AsNoTracking().Where(m => m.LicenseId == unproven.License).Select(m => m.Id)
                    .ToListAsync(ct))
                .Should().Equal([ownBondB], "kopyanın bağı silindi, asıl kaydınki yerinde");
            var copyDecision = await db.PaymentMatches.AsNoTracking().SingleAsync(m => m.Id == unproven.TeachingMatch, ct);
            (copyDecision.ProposedWpfCustomerId, copyDecision.ActualWpfCustomerId).Should().Be(((Guid?)null, (Guid?)null));
            var ownDecision = await db.PaymentMatches.AsNoTracking().SingleAsync(m => m.Id == ownDecisionB, ct);
            (ownDecision.ActualWpfCustomerId, ownDecision.IbanConfirmedAt)
                .Should().Be(((Guid?)unproven.P, (DateTimeOffset?)taught.AddDays(1)), "asıl kaydın kararı yerinde");
            (await db.WpfCustomerProjections.AsNoTracking().SingleAsync(c => c.Id == unproven.P, ct)).PurgedAt.Should().BeNull();
        }
    }

    /// <summary>Taşıma ve türetme durumları (sınıf özeti). Dekontların başlangıç değeri boş (S2a penceresinde gönderilmiş) ya da
    /// bayat dolu (eski imajın ya da S2a sabitinin yazdığı): türetme ikisini de yazar.</summary>
    private static async Task<Seed> SeedAsync(string connectionString)
    {
        var t0 = DateTimeOffset.UtcNow.AddDays(-60);
        await using var db = S2a.NewDb(connectionString);
        var s = new Seeder(db, t0);
        Guid license = s.License(), other = s.License();

        // IBAN bağları (Karar 7). A: K → P. E: K3 → K2 → K1 → P. B geçici kopya, C' silinmiş geçici kopya, D silinmiş asıl kaydın
        // kopyası, F başka lisansa yönelen kopya, G 4 adımlı zincir (sınırda kalan kopya), H geçici ara adımlı zincir.
        var p = s.Customer(license);
        var kA = s.Customer(license, p);
        var e1 = s.Customer(license, p);
        var e2 = s.Customer(license, e1);
        var e3 = s.Customer(license, e2);
        var kB = s.Customer(license, p, provisional: true);
        var kC = s.Customer(license, p, provisional: true, purged: true);
        var purgedCanonical = s.Customer(license, purged: true);
        var kD = s.Customer(license, purgedCanonical);
        var otherCanonical = s.Customer(other);
        var kF = s.Customer(license, otherCanonical);
        var w = s.Customer(license);
        var g1 = s.Customer(license, w);
        var g2 = s.Customer(license, g1);
        var g3 = s.Customer(license, g2);
        var g4 = s.Customer(license, g3);
        var h1 = s.Customer(license, p, provisional: true);
        var h2 = s.Customer(license, h1);

        var bonds = new List<BondCase>();
        void Bond(string name, Guid copy, Guid? movesTo)
        {
            var hash = S2a.NewHash();
            var teaching = s.Tx(license, hash, t0.AddDays(bonds.Count));
            var proposed = s.Tx(license, hash, t0.AddDays(bonds.Count).AddHours(6));
            var learnedFrom = bonds.Count % 2 == 0 ? IbanMemorySource.HumanApproval : IbanMemorySource.ManualMatch;
            bonds.Add(new BondCase(name, copy, movesTo, hash, s.Bond(license, copy, teaching, learnedFrom),
                s.Decision(teaching, copy), s.Proposal(proposed, copy)));
        }

        Bond("A", kA, p);
        Bond("E", e3, p);
        Bond("B", kB, null);
        Bond("C'", kC, null);
        Bond("D", kD, null);
        Bond("F", kF, null);
        Bond("G", g4, null);
        Bond("H", h2, null);

        // Dekontlar: S2a senaryosunun (a)-(j) durumları aynen, sonra S2b'nin (k)-(n) durumları.
        Guid rp = s.Customer(license), rk1 = s.Customer(license, rp), rk2 = s.Customer(license, rk1), rk3 = s.Customer(license, rk2);
        Guid q = s.Customer(license), stale = s.Customer(license);
        Guid g = s.Customer(other), f = s.Customer(other, g), k4 = s.Customer(license, f), k6 = s.Customer(license, k4);
        Guid m = s.Customer(other), unknown = Guid.NewGuid();

        var payments = new List<Rederived>();
        void Receipt(string name, Guid licenseId, Shopper? shopper, Guid? initial, Guid? expected)
        {
            var id = s.Payment(licenseId, shopper, initial, PaymentSource.Receipt, (PaymentStatus)(payments.Count % 3),
                t0.AddMinutes(payments.Count));
            payments.Add(new Rederived(name, id, licenseId, shopper?.Id, expected));
        }

        var sa = s.Shopper();
        s.Link(sa, license, q, 1, left: true);
        s.Link(sa, license, rk2, 2);
        Receipt("(a) eski ayrılmış bağ Q'ya, yeni bağ K2'ye", license, sa, null, rp);
        var sb = s.Shopper();
        s.Link(sb, license, q, 1, left: true);
        Receipt("(b) yalnız ayrılmış bağ", license, sb, stale, q);
        var sc = s.Shopper();
        s.Link(sc, license, q, 1, left: true);
        s.Link(sc, license, null, 2);
        Receipt("(c) yeni bağ müşterisiz, eskisi Q'ya", license, sc, null, q);
        var sd = s.Shopper();
        s.Link(sd, other, m, 1);
        Receipt("(d) bağ yalnız başka lisansta", license, sd, stale, null);
        Receipt("(e) shopper'sız dekont", license, null, stale, stale);
        Receipt("(f) bağsız shopper", license, s.Shopper(), stale, null);
        var sg = s.Shopper();
        s.Link(sg, license, rk3, 1);
        Receipt("(g) üç adımlık zincir", license, sg, rk3, rp);
        var sh = s.Shopper();
        s.Link(sh, license, unknown, 1);
        Receipt("(h) projeksiyonu olmayan Id", license, sh, null, unknown);
        var si = s.Shopper();
        s.Link(si, license, q, 1);
        s.Link(si, other, m, 2);
        Receipt("(i) iki lisanslı shopper, birinci lisans", license, si, stale, q);
        Receipt("(i) iki lisanslı shopper, ikinci lisans", other, si, null, m);
        var sj1 = s.Shopper();
        s.Link(sj1, license, k4, 1);
        Receipt("(j) zincir ikinci adımda başka lisansa çıkıyor", license, sj1, null, f);
        var sj2 = s.Shopper();
        s.Link(sj2, license, f, 1);
        Receipt("(j) bağ doğrudan başka lisansın kopyasına", license, sj2, stale, f);
        var sj3 = s.Shopper();
        s.Link(sj3, license, k6, 1);
        Receipt("(j) zincir üçüncü adımda başka lisansa çıkıyor", license, sj3, null, f);

        // (k) S2a sabiti kanıt aramadan asıl kayda yazmıştı; telefon kanıtlasa da SQL NULL'a çeker (Karar 9).
        var sk = s.Shopper(verified: true);
        Guid pk = s.Customer(license, phone: sk.Phone), kp = s.Customer(license, pk, provisional: true);
        s.Link(sk, license, kp, 1);
        Receipt("(k) bağlantı geçici kopyada, telefon kanıtlasa da", license, sk, pk, null);
        var provenPayment = payments[^1].PaymentId;
        var provisionalCanonical = s.Customer(license, provisional: true);
        var sl = s.Shopper();
        s.Link(sl, license, provisionalCanonical, 1);
        Receipt("(l) geçici asıl kayıt", license, sl, null, provisionalCanonical);
        // (m) kanıt tohumlanmaz: kanıtlı ara adımda C# da asıl kaydı verirdi.
        Guid pm = s.Customer(license), km1 = s.Customer(license, pm, provisional: true), km2 = s.Customer(license, km1);
        var sm = s.Shopper();
        s.Link(sm, license, km2, 1);
        Receipt("(m) geçici ara adım, telefon kanıtı yok", license, sm, pm, null);
        // (n) banka satırının müşterisi kararından gelir, bağdan türetilmez.
        var bank = s.Payment(license, sg, q, PaymentSource.Bank, PaymentStatus.Approved, t0.AddMinutes(payments.Count));
        payments.Add(new Rederived("(n) aynı shopper'ın banka satırı", bank, license, sg.Id, q, Bank: true));

        await db.SaveChangesAsync();
        return new Seed(license, other, bonds, payments, provenPayment, pk);
    }

    private static PaymentMatcher NewMatcher(LicenseDbContext db)
        => new(db, Options.Create(new BankOptions()), NullLogger<PaymentMatcher>.Instance);

    private static PaymentMatchReconciler NewReconciler(LicenseDbContext db)
        => new(db, NewMatcher(db), Options.Create(new BankOptions()), NullLogger<PaymentMatchReconciler>.Instance);

    private static ShopperPurgeService NewPurge(LicenseDbContext db)
        => new(db, new StubShopperPaymentStorage(), Options.Create(new SalesSyncOptions()),
            NullLogger<ShopperPurgeService>.Instance);

    private static async Task<Dictionary<Guid, BondRow>> BondsAsync(string connectionString, Guid licenseId)
    {
        await using var db = S2a.NewDb(connectionString);
        return await db.CustomerIbanMemories.AsNoTracking().Where(b => b.LicenseId == licenseId)
            .ToDictionaryAsync(b => b.Id, b => new BondRow(b.WpfCustomerId, b.IbanHash, b.CreatedAt, b.LastConfirmedAt,
                b.LearnedFrom, b.SourceBankTransactionId));
    }

    private static async Task<Dictionary<Guid, MatchRow>> MatchesAsync(string connectionString, Guid licenseId)
    {
        await using var db = S2a.NewDb(connectionString);
        return await db.PaymentMatches.AsNoTracking().Where(m => m.LicenseId == licenseId)
            .ToDictionaryAsync(m => m.Id, m => new MatchRow(m.ProposedWpfCustomerId, m.ActualWpfCustomerId, m.Status, m.UpdatedAt,
                m.MatcherVersion, m.DecidedAt, m.IbanConfirmedAt, m.Evidence));
    }

    private static async Task<Dictionary<Guid, Guid>> AllBondsAsync(string connectionString)
    {
        await using var db = S2a.NewDb(connectionString);
        return await db.CustomerIbanMemories.AsNoTracking().ToDictionaryAsync(b => b.Id, b => b.WpfCustomerId);
    }

    private static async Task<Dictionary<Guid, (Guid?, Guid?, DateTimeOffset)>> AllMatchesAsync(string connectionString)
    {
        await using var db = S2a.NewDb(connectionString);
        return await db.PaymentMatches.AsNoTracking()
            .ToDictionaryAsync(m => m.Id, m => (m.ProposedWpfCustomerId, m.ActualWpfCustomerId, m.UpdatedAt));
    }

    private static async Task<Dictionary<Guid, Stamp>> StampsAsync(string connectionString, Seed seed)
    {
        await using var db = S2a.NewDb(connectionString);
        return await db.Payments.AsNoTracking().Where(p => p.LicenseId == seed.License || p.LicenseId == seed.Other)
            .ToDictionaryAsync(p => p.Id, p => new Stamp(p.Status, p.UpdatedAt));
    }

    /// <summary>EF tohumu: şema S2a'nınkiyle aynı (S2b şemayı değiştirmez), değerler üretilir. Satırlar izlenir, kaydı çağıran
    /// yapar.</summary>
    private sealed class Seeder
    {
        private readonly LicenseDbContext _db;
        private readonly DateTimeOffset _t0;
        private readonly Guid _account;
        private long _obifinId;

        public Seeder(LicenseDbContext db, DateTimeOffset t0)
        {
            _db = db;
            _t0 = t0;
            var account = new Customer
            {
                Id = Guid.NewGuid(), Email = $"yayinci-{Guid.NewGuid():N}@example.test", Name = "Banka S2b Göç Testi",
                PasswordHash = $"h-{Guid.NewGuid():N}", CreatedAt = t0,
            };
            db.Customers.Add(account);
            _account = account.Id;
        }

        public Guid License()
        {
            var license = new License
            {
                Id = Guid.NewGuid(), CustomerId = _account, LicenseKey = $"s2b-{Guid.NewGuid():N}", SkuCode = "STD",
                ActivationSlots = 1, IssuedAt = _t0, ExpiresAt = _t0.AddYears(2),
            };
            _db.Licenses.Add(license);
            return license.Id;
        }

        /// <param name="provisional">Shopper'ın açtığı geçici kayıt (<c>CreatedByShopper</c>).</param>
        /// <param name="purged">KVKK silmesiyle silinmiş (<c>PurgedAt</c> dolu).</param>
        public Guid Customer(Guid licenseId, Guid? mergedInto = null, bool provisional = false, bool purged = false,
            string? phone = null)
        {
            var row = new WpfCustomerProjection
            {
                Id = Guid.NewGuid(), LicenseId = licenseId, Platform = "tiktok", Username = $"m{Guid.NewGuid():N}",
                MergedIntoId = mergedInto, CreatedByShopper = provisional, PurgedAt = purged ? _t0 : null, Phone = phone,
                UpdatedAt = _t0,
            };
            _db.WpfCustomerProjections.Add(row);
            return row.Id;
        }

        public Shopper Shopper(bool verified = false)
        {
            var shopper = new Shopper
            {
                Id = Guid.NewGuid(), FullName = "Göç Testi", Phone = TestPhone.NewE164(), PhoneVerifiedAt = verified ? _t0 : null,
                PasswordHash = $"h-{Guid.NewGuid():N}", Address = "-", CreatedAt = _t0, UpdatedAt = _t0,
            };
            _db.Shoppers.Add(shopper);
            return shopper;
        }

        /// <param name="day">Katılma sırası (<c>JoinedAt</c>); eşitlik tohumlanmaz.</param>
        public void Link(Shopper shopper, Guid licenseId, Guid? customer, int day, bool left = false)
            => _db.ShopperBroadcasterLinks.Add(new ShopperBroadcasterLink
            {
                Id = Guid.NewGuid(), ShopperId = shopper.Id, LicenseId = licenseId, Platform = "tiktok",
                Username = $"m{Guid.NewGuid():N}", WpfCustomerId = customer, JoinedAt = _t0.AddDays(day),
                LeftAt = left ? _t0.AddDays(day).AddHours(1) : null,
            });

        /// <summary>Gelen TL havalesi, özeti geçerli IBAN'dan (öğretebilir); açıklamada kullanıcı adı ya da ad yok.</summary>
        public BankTransaction Tx(Guid licenseId, string hash, DateTimeOffset at, decimal? amount = null)
        {
            _obifinId++;
            var tx = new BankTransaction
            {
                Id = Guid.NewGuid(), LicenseId = licenseId, ObifinId = _obifinId, ObifinAccountId = 1, BankaKodu = "test",
                Direction = BankTransactionDirection.Incoming, Amount = amount ?? 100m + _obifinId, Currency = "TL",
                OccurredAt = at, FetchedAt = at, Description = "HAVALE ODEME", CounterpartyIbanHash = hash,
                CounterpartyIbanMasked = "TR**", CounterpartyIbanHashValid = true,
            };
            _db.BankTransactions.Add(tx);
            return tx;
        }

        /// <summary>IBAN bağı; <paramref name="source"/> onu öğreten havale, son doğrulaması da odur.</summary>
        public Guid Bond(Guid licenseId, Guid customer, BankTransaction source,
            IbanMemorySource from = IbanMemorySource.HumanApproval)
        {
            var memory = new CustomerIbanMemory
            {
                Id = Guid.NewGuid(), LicenseId = licenseId, WpfCustomerId = customer, IbanHash = source.CounterpartyIbanHash!,
                IbanMasked = "TR**", LearnedFrom = from, SourceBankTransactionId = source.Id,
                CreatedAt = source.OccurredAt.AddHours(1), LastConfirmedAt = source.OccurredAt,
            };
            _db.CustomerIbanMemories.Add(memory);
            return memory.Id;
        }

        /// <summary>Bağı öğreten karar: öneri = karar = <paramref name="customer"/>, IBAN'ı doğrulamış (<c>IbanConfirmedAt</c>). Bağ
        /// ve karar aynı SaveChanges'te aynı müşteriye yazılır (<c>LearnIbanAsync</c>); birleştirmeyle yarışan öğrenme ikisini
        /// kopyada bırakır.</summary>
        public Guid Decision(BankTransaction tx, Guid customer)
        {
            var match = new PaymentMatch
            {
                Id = Guid.NewGuid(), LicenseId = tx.LicenseId, BankTransactionId = tx.Id, ProposedWpfCustomerId = customer,
                ActualWpfCustomerId = customer, Layer = PaymentMatchLayer.IbanMemory, Confidence = PaymentMatcher.IbanConfidence,
                Evidence = "iban-memory", Status = PaymentMatchStatus.ConfirmedByHuman, DecidedAt = tx.OccurredAt.AddHours(2),
                IbanConfirmedAt = tx.OccurredAt, CreatedAt = tx.OccurredAt.AddHours(1), UpdatedAt = tx.OccurredAt.AddHours(2),
            };
            _db.PaymentMatches.Add(match);
            return match.Id;
        }

        /// <summary>Kararsız öneri: yalnız <c>ProposedWpfCustomerId</c>.</summary>
        public Guid Proposal(BankTransaction tx, Guid customer)
        {
            var match = new PaymentMatch
            {
                Id = Guid.NewGuid(), LicenseId = tx.LicenseId, BankTransactionId = tx.Id, ProposedWpfCustomerId = customer,
                Layer = PaymentMatchLayer.IbanMemory, Confidence = PaymentMatcher.IbanConfidence, Evidence = "iban-memory",
                Status = PaymentMatchStatus.Proposed, CreatedAt = tx.OccurredAt.AddHours(1), UpdatedAt = tx.OccurredAt.AddHours(1),
            };
            _db.PaymentMatches.Add(match);
            return match.Id;
        }

        public Guid Payment(Guid licenseId, Shopper? shopper, Guid? customer, PaymentSource source, PaymentStatus status,
            DateTimeOffset at, decimal amount = 100m)
        {
            var bank = source == PaymentSource.Bank;
            var payment = new Payment
            {
                Id = Guid.NewGuid(), LicenseId = licenseId, ShopperId = shopper?.Id, PayerName = bank ? "" : "Göç Testi",
                Amount = amount, PaidAt = at, ReferansNo = bank ? $"bank:{Guid.NewGuid():N}:1" : $"r-{Guid.NewGuid():N}",
                Status = status, Source = source, WpfCustomerId = customer, CreatedAt = at, UpdatedAt = at,
            };
            _db.Payments.Add(payment);
            return payment.Id;
        }
    }
}
