using System.Diagnostics;
using System.Text;
using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Storage;
using OrderDeck.Shared.Sales;
using OrderDeck.Tests.TestHelpers;
using Xunit;
using Xunit.Abstractions;

namespace OrderDeck.Tests.Storage;

/// <summary>
/// Göç 049 gerçek boyutta ve önceki sürüme dönüşte (spec 2026-10-13 §3.2, §8.6, §10.3), desen
/// <see cref="Migration045RehearsalTests"/>: (1) üretilmiş prod boyu 048 veritabanı; (2) isteğe bağlı:
/// yayın bilgisayarı yedeğinin KOPYASI (ortam değişkeniyle, yalnız elle — kişisel veri repoya ya da
/// CI'ya girmez, çıktıda yalnız sayılar); (3) önceki sürümün (v0.9.10, şema 48) SQL'i şema 49 üstünde;
/// (4) Mode = 1 / Authority = 'server' durumunda eski SQL'in görünür biçimde düşmesi ve sıfırlama
/// betiğinden (<c>deploy/scripts/wpf-sales-sync-reset.sql</c>) sonra çalışması.
///
/// <para><b>Gerçek yedek provası:</b> uygulama KAPALIYKEN — veritabanı ve <c>-wal</c> dosyası ayrı ayrı
/// kopyalanır. Kopya <c>%TEMP%\od049-*.db</c> adıyla kişisel veri taşır ve test sonunda silinir; test süreci
/// çökerse <c>%TEMP%\od049-*.db*</c> elle denetlenip silinmeli.</para>
/// </summary>
public sealed class Migration049RehearsalTests(ITestOutputHelper output)
{
    internal const string RehearsalDbVariable = "ORDERDECK_049_REHEARSAL_DB";

    /// <summary>v0.9.10'un kaydettiği uygulama fonksiyonları (<c>git show v0.9.10:OrderDeck.Core/Storage/
    /// SqliteSearchFunctions.cs</c>).</summary>
    private static readonly string[] PreviousReleaseFunctions =
        ["od_search_key", "od_phone_key", "od_identity_key", "od_legacy_group_id", "od_payment_net"];

    /// <summary>Gerçek yedek provası yalnız <see cref="RehearsalDbVariable"/> verilince koşar; yoksa
    /// "atlandı" görünür (sessizce geçti sayılmaz).</summary>
    internal sealed class RehearsalFactAttribute : FactAttribute
    {
        public RehearsalFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(RehearsalDbVariable)))
                Skip = $"Elle prova: {RehearsalDbVariable} = yayın bilgisayarının orderdeck.db yedeği (şema 48)";
        }
    }

    private static string TempDb() => Path.Combine(Path.GetTempPath(), $"od049-{Guid.NewGuid():N}.db");

    private static void Drop(string path)
    {
        SqliteConnection.ClearAllPools();
        foreach (var f in new[] { path, path + "-wal", path + "-shm" })
            if (File.Exists(f)) File.Delete(f);
    }

    // ── (1) prod boyu ──────────────────────────────────────────────────

    [Fact]
    public void Prod_boyu_048_veritabani_49a_30_saniyede_veriyi_degistirmeden_gecer()
    {
        var path = TempDb();
        var factory = new SqliteConnectionFactory(path);
        try
        {
            new MigrationRunner(factory, EmbeddedMigrationScripts.UpTo(48)).Run();
            Seed048(factory, customers: 20_000, sessions: 5_000, labels: 200_000, shipments: 2_000);
            var before = Snapshot(factory);
            before.Should().Match<Counts>(c => c.Customers == 20_000 && c.Sessions == 5_000 && c.Labels == 200_000
                                               && c.Shipments == 2_000 && c.FkViolations == 0);

            var sw = Stopwatch.StartNew();
            new MigrationRunner(factory).Run();
            sw.Stop();
            output.WriteLine($"049 göçü: {before.Customers} müşteri, {before.Sessions} yayın, {before.Labels} etiket, " +
                             $"{before.Shipments} dosya, {sw.ElapsedMilliseconds} ms");

            AssertMigrated(factory, before);
            sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(30), "göç açılışta koşar — kullanıcı bekler");
        }
        finally { Drop(path); }
    }

    // ── (2) gerçek yedek ───────────────────────────────────────────────

    [RehearsalFact]
    public void Gercek_yedek_kopyasinda_prova()
    {
        // Yalnız elle, uygulama KAPALIYKEN: ORDERDECK_049_REHEARSAL_DB = yayın bilgisayarının orderdeck.db'si
        // ya da yedeği. Kaynak SQLite'la hiç açılmaz: dosya ve (varsa) -wal'ı ayrı ayrı geçici kopyaya alınır,
        // göç kopyada koşar, kopya silinir. Çıktıda yalnız sayılar ve süre.
        var source = Environment.GetEnvironmentVariable(RehearsalDbVariable)!;
        File.Exists(source).Should().BeTrue($"{RehearsalDbVariable} bir veritabanı dosyasını göstermeli");

        var path = TempDb();
        try
        {
            File.Copy(source, path);
            if (File.Exists(source + "-wal")) File.Copy(source + "-wal", path + "-wal");
            var factory = new SqliteConnectionFactory(path);
            using (var c = factory.Open())
                c.ExecuteScalar<int>("SELECT SchemaVersion FROM _meta WHERE Id = 1")
                    .Should().Be(48, "prova sahadaki son sürümün (v0.9.10, şema 48) veritabanını ister");
            var before = Snapshot(factory);

            var sw = Stopwatch.StartNew();
            new MigrationRunner(factory).Run();
            sw.Stop();

            AssertMigrated(factory, before);
            using var conn = factory.Open();
            output.WriteLine(
                $"049 provası: {before.Customers} müşteri, {before.Sessions} yayın ({before.OpenSessions} açık), " +
                $"{before.Labels} etiket ({before.UnsentLabels} gönderilmemiş), {before.Shipments} dosya " +
                $"({before.OpenShipments} açık), FK ihlali {before.FkViolations}, {sw.ElapsedMilliseconds} ms");
        }
        finally { Drop(path); }
    }

    // ── (3) önceki sürüm şema 49'da, mod 0 ─────────────────────────────

    [Fact]
    public void Onceki_surum_sema_49da_mod_0da_yayinin_butun_isini_yapar()
    {
        var path = TempDb();
        var factory = new SqliteConnectionFactory(path);
        try
        {
            new MigrationRunner(factory).Run();                                          // bu sürüm: şema 49
            new MigrationRunner(factory, EmbeddedMigrationScripts.UpTo(48)).Run();       // önceki sürümün koşucusu: no-op
            using (var c = factory.Open())
            {
                c.ExecuteScalar<int>("SELECT SchemaVersion FROM _meta WHERE Id = 1").Should().Be(49);
                c.ExecuteScalar<long>("SELECT Mode FROM SalesSyncState WHERE Id = 1").Should().Be(0);
                // Hiçbir şema nesnesi v0.9.10'un kaydetmediği bir uygulama fonksiyonunu çağırmaz.
                c.Query<string>("SELECT sql FROM sqlite_master WHERE sql IS NOT NULL")
                    .SelectMany(sql => System.Text.RegularExpressions.Regex.Matches(sql, @"\b(od_[a-z_]+)\s*\(",
                            System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                        .Select(m => m.Groups[1].Value.ToLowerInvariant()))
                    .Distinct()
                    .Should().BeSubsetOf(PreviousReleaseFunctions);
            }

            using var old = PreviousReleaseConnection(path);
            old.Execute(SeedCustomerSql, new { Id = "musteri", Username = "@eski.surum" });

            // Yayın aç (StartStream) ve etiket yaz.
            old.Execute(V0910.SessionInsert, new
            {
                Id = "eski-yayin", Title = (string?)null, StartedAt = 1000L, EndedAt = (long?)null,
                Platforms = "[\"instagram\"]", Notes = (string?)null, SyncedAt = (long?)null,
            }).Should().Be(1);
            ActiveSessions(old).Should().Equal(["eski-yayin"], "eski GetActive yeni oturumu görür (StartStream, kurtarma ekranı)");
            var sale = OldLabel(old, "satis", "eski-yayin", "musteri", 100m);
            var backup = OldLabel(old, "yedek", "eski-yayin", "musteri", 90m, tentative: true);

            // Bas (kâğıt sırası) ve ciroyu yaz.
            old.Execute(V0910.MarkPrintedBatch, new[]
            {
                new { printedAt = 1100L, batchId = "b-eski", seq = 1, id = sale },
                new { printedAt = 1100L, batchId = "b-eski", seq = 2, id = backup },
            }).Should().Be(2);
            old.Execute(V0910.IncrementLabelStats, new { id = "musteri", labelDelta = 1, amountDelta = 100m, lastSeenAt = 1100L }).Should().Be(1);

            // İptal ve geri alma, fiyat düzeltme, yedek onayı, gönderim onayı.
            old.Execute(V0910.MarkCancelled, new { cancelledAt = 1200L, reason = "customer", ids = new[] { sale } }).Should().Be(1);
            old.Execute(V0910.IncrementLabelStats, new { id = "musteri", labelDelta = -1, amountDelta = -100m, lastSeenAt = 1200L });
            old.Execute(V0910.Uncancel, new { ids = new[] { sale } }).Should().Be(1);
            old.Execute(V0910.IncrementLabelStats, new { id = "musteri", labelDelta = 1, amountDelta = 100m, lastSeenAt = 1200L });
            using (var tx = old.BeginTransaction())
            {
                old.Execute(V0910.UpdatePriceForCorrection, new
                {
                    id = sale, expectedPrice = 100m, newPrice = 120m, correctedAt = 1300L,
                    expectPrinted = 1, expectTentative = 0, paidFromBalance = 0,
                }, tx).Should().Be(1, "koşullu fiyat düzeltmesi şema 49'da da satırı bulur");
                old.Execute(V0910.IncrementLabelStats, new { id = "musteri", labelDelta = 0, amountDelta = 20m, lastSeenAt = 1300L }, tx);
                tx.Commit();
            }
            old.Execute(V0910.ConfirmTentativeBackups, new { ids = new[] { backup } }).Should().Be(1);
            old.Execute(V0910.IncrementLabelStats, new { id = "musteri", labelDelta = 1, amountDelta = 90m, lastSeenAt = 1300L });
            foreach (var id in new[] { sale, backup })
                old.Execute(V0910.LabelMarkSynced, new { id, syncedAt = 1400L, revision = Revision(old, id) }).Should().Be(1);

            // Yayını bitir.
            old.Execute(V0910.SessionEnd, new { id = "eski-yayin", endedAt = 1500L }).Should().Be(1);
            ActiveSessions(old).Should().BeEmpty("açık yayın kalmadı");

            // Dekontun kargo adımı: dosya aç, bağla, tutarı yaz.
            old.Execute(V0910.ShipmentInsert, new
            {
                Id = "eski-dosya", CustomerId = "musteri", Status = "Pending", CreatedAt = 1600L,
                HeldAt = (long?)null, ShippedAt = (long?)null, CumulativeAmount = 0d, SyncedAt = (long?)null,
            }).Should().Be(1);
            var synced = Revision(old, sale);
            old.Execute(V0910.AttachLabel, new { shipmentId = "eski-dosya", labelId = sale }).Should().Be(1);
            old.Execute(V0910.ShipmentUpdate, new
            {
                Id = "eski-dosya", Status = "Pending", HeldAt = (long?)null, ShippedAt = (long?)null, CumulativeAmount = 120d,
            }).Should().Be(1);

            using var conn = factory.Open();
            conn.QuerySingle<(string Origin, string Authority, long? ReleasedAt)>(
                    "SELECT Origin, Authority, ReleasedAt FROM StreamSession WHERE Id = 'eski-yayin'")
                .Should().Be(("local", "local", (long?)null), "eski sürümün eklediği oturum varsayılanla yerel doğar");
            conn.QuerySingle<(long? SyncedAt, long Revision, long? StockSyncedAt)>(
                    "SELECT SyncedAt, Revision, StockSyncedAt FROM Label WHERE Id = @sale", new { sale })
                .Should().Be(((long?)null, synced + 1, (long?)1400L),
                    "DAVRANIŞ DEĞİŞİKLİĞİ (049): eski sürümün AttachLabel'ı da etiketi kuyruğa alır — fazladan bir v1 gönderimi; " +
                    "sunucu v1'de kargo bağına dokunmaz (K4), stok damgası kalır");
            conn.ExecuteScalar<long>("SELECT COUNT(*) FROM Label WHERE ServerVersion IS NOT NULL").Should().Be(0);
            conn.ExecuteScalar<string?>("SELECT OriginReplicaId FROM Shipment WHERE Id = 'eski-dosya'")
                .Should().BeNull("eski ikili yazmaz; NULL bu veritabanının dosyası sayılır (049 başlığı)");
            conn.QuerySingle<(string Status, double Amount, long Revision)>(
                    "SELECT Status, CumulativeAmount, Revision FROM Shipment WHERE Id = 'eski-dosya'")
                .Should().Be(("Pending", 120d, 1L), "Update Revision'ı kendisi artırır; tetikleyici çift artış yapmaz");
            conn.QuerySingle<(long Labels, double Amount)>("SELECT TotalLabelsPrinted, TotalAmount FROM Customer WHERE Id = 'musteri'")
                .Should().Be((2L, 210d));
            conn.Query("PRAGMA foreign_key_check").Should().BeEmpty();
        }
        finally { Drop(path); }
    }

    // ── (4) Mode = 1 / Authority = 'server' ve sıfırlama betiği ─────────

    [Fact]
    public void Mod_1de_onceki_surum_gorunur_duser_sifirlama_betiginden_sonra_calisir()
    {
        var path = TempDb();
        var factory = new SqliteConnectionFactory(path);
        try
        {
            new MigrationRunner(factory).Run();
            var world = SeedModeOn(factory);
            using (var old = PreviousReleaseConnection(path))
            {
                ActiveSessions(old).Should().Equal(["kendi-canli"],
                    "başka bilgisayarın canlı yayını StreamSession'a girmez (RemoteLiveSession, M5): eski StartStream ve kurtarma ekranı yanılmaz");
                var cancel = () => old.Execute(V0910.MarkCancelled, new { cancelledAt = 5000L, reason = "customer", ids = new[] { world.OwnReleasedLabel } });
                cancel.Should().Throw<SqliteException>().WithMessage("*sales-server-authority*",
                    "kendi devredilmiş yayınında eski sürümün iptali görünür biçimde düşer");
                var insert = () => old.Execute(V0910.ShipmentInsert, NewOldShipment("eski-dosya"));
                insert.Should().Throw<SqliteException>().WithMessage("*sales-shipment-server-owned*",
                    "dekontun kargo adımı (dosya açma) düşer");
                var attach = () => old.Execute(V0910.AttachLabel, new { shipmentId = world.LocalShipment, labelId = world.OwnUnattachedLabel });
                attach.Should().Throw<SqliteException>().WithMessage("*sales-shipment-server-owned*",
                    "dekontun kargo adımı (bağlama) düşer");
            }

            var (blockers, plan) = RunResetScript(path);

            blockers.Should().BeEmpty();
            plan.Should().Be(new Plan(Mode: 1, RemoteSessions: 1, RemoteLabels: 3, RemoteLive: 1, SessionsBackToLocal: 1,
                KeptShipments: 1, DeletedShipments: 2, PendingCommands: 0));

            using (var c = factory.Open())
            {
                c.ExecuteScalar<long>("SELECT Mode FROM SalesSyncState WHERE Id = 1").Should().Be(0);
                c.ExecuteScalar<long>("SELECT COUNT(*) FROM StreamSession WHERE Origin = 'remote'").Should().Be(0);
                c.ExecuteScalar<long>("SELECT COUNT(*) FROM StreamSession WHERE Authority <> 'local'").Should().Be(0);
                c.ExecuteScalar<long>("SELECT COUNT(*) FROM RemoteLiveSession").Should().Be(0);
                c.Query<string>("SELECT Id FROM Label ORDER BY Id").Should().BeEquivalentTo(world.LocalLabels, "uzak etiketler gitti, yereller kaldı");
                LocalDigest(c).Should().Be(world.LocalDigestBefore, "yerel etiket ve oturum satırları (Authority hariç) değişmedi");
                c.Query<(string Id, string? Origin)>("SELECT Id, OriginReplicaId FROM Shipment ORDER BY Id")
                    .Should().BeEquivalentTo(new[]
                    {
                        (world.LocalShipment, (string?)world.ReplicaId),     // yalnız yerel etiket: kalır, sahibi bu kopya
                        (world.ShippedRemoteShipment, (string?)world.OtherReplica), // kargolanmış: dokunulmaz
                        (world.OwnEmptyShipment, (string?)world.ReplicaId),  // etiketsiz, bu kopyanın: kalır
                        (world.LegacyEmptyShipment, (string?)null),          // etiketsiz, eski ikilinin: kalır
                    }, "yalnız uzak etiketli ve başka kopyanın etiketsiz dosyası silindi");
                AssertTotalsDerived(c);
                c.Query("PRAGMA foreign_key_check").Should().BeEmpty("betik etiketleri oturumlardan önce siler — FK kapalıyken de öksüz yok");
            }

            using (var old = PreviousReleaseConnection(path))
            {
                old.Execute(V0910.MarkCancelled, new { cancelledAt = 5000L, reason = "customer", ids = new[] { world.OwnReleasedLabel } })
                    .Should().Be(1);
                old.Execute(V0910.ShipmentInsert, NewOldShipment("eski-dosya")).Should().Be(1);
                old.Execute(V0910.AttachLabel, new { shipmentId = world.LocalShipment, labelId = world.OwnUnattachedLabel }).Should().Be(1);
                old.Execute(V0910.ShipmentUpdate, new
                {
                    Id = world.LocalShipment, Status = "Held", HeldAt = (long?)5100L, ShippedAt = (long?)null, CumulativeAmount = 75d,
                }).Should().Be(1);
            }
        }
        finally { Drop(path); }
    }

    public static TheoryData<string, bool> Blockers => new()
    {
        { "bekleyen-satis-komutu", true }, { "bekleyen-satis-komutu", false },
        { "karisik-kargo-dosyasi", true }, { "karisik-kargo-dosyasi", false },
    };

    /// <param name="bail">true: Microsoft.Data.Sqlite ve <c>sqlite3 -bail</c> gibi ilk hatada durur.
    /// false: -bail'siz sqlite3 gibi her ifadeyi sırayla koşar, hatayı yazıp devam eder.</param>
    [Theory]
    [MemberData(nameof(Blockers))]
    public void Sifirlama_betigi_engelde_hicbir_satiri_degistirmez_ve_engeli_listeler(string blocker, bool bail)
    {
        var path = TempDb();
        var factory = new SqliteConnectionFactory(path);
        try
        {
            new MigrationRunner(factory).Run();
            var world = SeedModeOn(factory);
            string offending;
            using (var c = factory.Open())
            {
                if (blocker == "bekleyen-satis-komutu")
                {
                    offending = $"komut-{Guid.NewGuid():N}";
                    c.Execute("INSERT INTO PendingSalesCommand (CommandId, Kind, PayloadJson, CreatedAt) VALUES (@offending, 'cancel', '{}', 1)", new { offending });
                    c.Execute("INSERT INTO PendingSalesCommandLabel (LabelId, CommandId) VALUES (@label, @offending)", new { label = world.OwnReleasedLabel, offending });
                }
                else
                {
                    offending = world.LocalShipment;
                    using var scope = SyncApplyScope.Begin(new SqliteConnectionFactory(path));
                    scope.Execute("UPDATE Label SET ShipmentId = @offending WHERE Id = @remote", new { offending, remote = world.RemoteUnattachedLabel });
                    scope.Commit();
                }
            }
            var before = FullDigest(factory);

            if (bail)
            {
                var run = () => RunResetScript(path);
                run.Should().Throw<SqliteException>().WithMessage(blocker == "bekleyen-satis-komutu"
                    ? "*bekleyen_satis_komutu_var_betik_durdu*"
                    : "*karisik_kargo_dosyasi_var_betik_durdu*");
            }
            else
            {
                var errors = RunResetScriptContinuingOnError(path);
                errors.Should().ContainSingle().Which.Should().Contain("_betik_durdu");
            }

            FullDigest(factory).Should().Be(before, "kilit düştü: hiçbir satır değişmedi");
            ListBlockers(path).Should().Equal((blocker, offending));
        }
        finally { Drop(path); }
    }

    [Fact]
    public void Sifirlama_betigi_sifirlanmis_veritabaninda_hicbir_sey_degistirmez()
    {
        var path = TempDb();
        var factory = new SqliteConnectionFactory(path);
        try
        {
            new MigrationRunner(factory).Run();
            SeedModeOn(factory);
            RunResetScript(path);
            var before = FullDigest(factory);

            var (blockers, plan) = RunResetScript(path);

            blockers.Should().BeEmpty();
            plan.Should().Be(new Plan(0, 0, 0, 0, 0, KeptShipments: 1, DeletedShipments: 0, PendingCommands: 0),
                "yalnız yerel etiketli dosya 'kalır' sayılır; yazım yok");
            FullDigest(factory).Should().Be(before);
            RunResetScriptContinuingOnError(path).Should().BeEmpty("-bail'siz komut satırında da hatasız");
            FullDigest(factory).Should().Be(before);
        }
        finally { Drop(path); }
    }

    [Fact]
    public void Sifirlama_betigi_turetme_damgalarini_ve_geri_doldurma_imlecini_siler_Wsye_donuste_dosya_yeniden_turetilir()
    {
        // Ws → (destek eylemi) v0.9.10 → yeniden Ws, mod Off. Eski ikili Y5'in yerel yolunu bilmez: onun döneminde
        // iptal edilen bağlı etiket dosya tutarında kalır. Damga dolu kalsaydı Ws yeniden türetmez, sonraki geri alma
        // aynı tutarı ikinci kez eklerdi (§7.4); imleç tamamlandı kalsaydı eski ikilinin v1 gönderimleri kaynak
        // kopyasız kalırdı.
        var path = TempDb();
        var factory = new SqliteConnectionFactory(path);
        var license = $"lisans-{Guid.NewGuid():N}";
        try
        {
            new MigrationRunner(factory).Run();
            using (var c = factory.Open())
            {
                c.Execute(SeedCustomerSql, new { Id = "m1", Username = "@alici.bir" });
                c.Execute("INSERT INTO StreamSession (Id, StartedAt, EndedAt, SyncedAt) VALUES ('yayin', 100, 200, 210)");
                c.Execute("INSERT INTO Shipment (Id, CustomerId, Status, CreatedAt, CumulativeAmount) VALUES ('dosya', 'm1', 'Held', 300, 0)");
                c.Execute(@"INSERT INTO Label (Id, SessionId, CustomerId, Platform, Username, MessageText, Code, Price, AddedAt,
                                               PrintedAt, ShipmentId, SyncedAt, StockSyncedAt)
                            VALUES ('k1', 'yayin', 'm1', 'instagram', 'kullanici', 'A1', 'A1', 250, 110, 120, 'dosya', 220, 220),
                                   ('k2', 'yayin', 'm1', 'instagram', 'kullanici', 'A2', 'A2', 150, 111, 120, 'dosya', 220, 220)");
            }
            var log = new CapturingLogger<OrderDeck.App.AppHost>();
            var cursors = new OrderDeck.Core.Storage.Repositories.SyncCursorRepository(factory);

            // Ws'nin ilk açılışı: dosya türetilir, geri doldurma tamamlanır.
            OrderDeck.App.Services.ShipmentAmountsBoot.Run(factory, nowUnix: 9000, log).Should().NotBeNull();
            OrderDeck.App.Services.CustomerAggregatesBoot.Run(factory, nowUnix: 9000, log);
            cursors.Upsert("sales-backfill-out", license, seq: 4_000_000_000_000L, lastId: Guid.NewGuid());
            cursors.Upsert("customer-changes", license, seq: 42);
            ShipmentAmount(factory).Should().Be(400m);

            // Eski sürüm K2'yi iptal eder; dosyaya dokunmaz.
            using (var old = PreviousReleaseConnection(path))
                old.Execute(V0910.MarkCancelled, new { cancelledAt = 9100L, reason = "customer", ids = new[] { "k2" } })
                    .Should().Be(1);
            ShipmentAmount(factory).Should().Be(400m, "ön koşul: eski ikili Y5'i bilmez");

            RunResetScript(path).Blockers.Should().BeEmpty();

            using (var c = factory.Open())
                c.QuerySingle<(long? Shipments, long? Aggregates)>(
                        "SELECT ShipmentsDerivedAt, AggregatesDerivedAt FROM SalesSyncState WHERE Id = 1")
                    .Should().Be(((long?)null, (long?)null));
            cursors.Get("sales-backfill-out", license).Should().BeNull("geri doldurma Ws'de baştan koşar (idempotent)");
            cursors.Get("customer-changes", license)!.Seq.Should().Be(42, "öbür imleçlere dokunulmaz");

            // Yeniden Ws: dosya bağlardan yeniden türetilir.
            OrderDeck.App.Services.ShipmentAmountsBoot.Run(factory, nowUnix: 9500, log).Should().NotBeNull();
            ShipmentAmount(factory).Should().Be(250m, "eski sürümün iptali dosyadan düştü");
        }
        finally { Drop(path); }
    }

    private static decimal ShipmentAmount(IDbConnectionFactory factory)
    {
        using var c = factory.Open();
        return c.ExecuteScalar<decimal>("SELECT CumulativeAmount FROM Shipment WHERE Id = 'dosya'");
    }

    // ── tohumlar ───────────────────────────────────────────────────────

    private sealed record ModeOnWorld(
        string ReplicaId, string OtherReplica, string OwnReleasedLabel, string OwnUnattachedLabel, string RemoteUnattachedLabel,
        string LocalShipment, string ShippedRemoteShipment, string OwnEmptyShipment, string LegacyEmptyShipment,
        IReadOnlyList<string> LocalLabels, string LocalDigestBefore);

    /// <summary>Mod On'daki bir bilgisayar: kendi devredilmiş yayını (Authority 'server'), kendi canlı
    /// yayını, akıştan inmiş uzak yayın ve dosyalar, başka bilgisayarın canlı başlığı, Mode = 1. Müşteri
    /// toplamları bilerek kaymış (betik etiketlerden yeniden türetir).</summary>
    private static ModeOnWorld SeedModeOn(SqliteConnectionFactory factory)
    {
        using var c = factory.Open();
        var replica = c.ExecuteScalar<string>("SELECT ReplicaId FROM _meta WHERE Id = 1")!;
        var other = Guid.NewGuid().ToString("N");
        foreach (var (id, user) in new[] { ("m1", "@alici.bir"), ("m2", "@alici.iki"), ("m3", "@alici.uc") })
            c.Execute(SeedCustomerSql + "; UPDATE Customer SET TotalLabelsPrinted = 7, TotalAmount = 777 WHERE Id = @Id", new { Id = id, Username = user });

        c.Execute(@"INSERT INTO StreamSession (Id, StartedAt, EndedAt, SyncedAt, Origin, Authority, ReleasedAt) VALUES
                    ('kendi-bitmis', 100, 200, 210, 'local', 'local', 205),
                    ('kendi-canli', 300, NULL, NULL, 'local', 'local', NULL)");
        using (var scope = SyncApplyScope.Begin(factory))
        {
            scope.Execute(@"INSERT INTO StreamSession (Id, StartedAt, EndedAt, SyncedAt, Origin, Authority, ReleasedAt, OwnerMachineName)
                            VALUES ('uzak-bitmis', 150, 250, 260, 'remote', 'server', 255, 'PC-B')");
            scope.Commit();
        }
        void Label(string id, string session, string customer, decimal price, bool printed, string? shipment,
            bool cancelled = false, bool tentative = false)
        {
            using var scope = SyncApplyScope.Begin(factory);
            scope.Execute(@"INSERT INTO Label (Id, SessionId, CustomerId, Platform, Username, MessageText, Code, Price, AddedAt,
                                               PrintedAt, CancelledAt, IsTentativeBackup, ShipmentId, SyncedAt, StockSyncedAt)
                            VALUES (@id, @session, @customer, 'instagram', 'kullanici', 'A1', 'A1', @price, 110,
                                    @printedAt, @cancelledAt, @tentative, @shipment, 220, 220)",
                new
                {
                    id, session, customer, price = (double)price, printedAt = printed ? 120L : (long?)null,
                    cancelledAt = cancelled ? 130L : (long?)null, tentative = tentative ? 1 : 0, shipment,
                });
            scope.Commit();
        }
        Label("yerel-1", "kendi-bitmis", "m1", 50m, printed: true, shipment: "dosya-yerel");
        Label("yerel-2", "kendi-bitmis", "m1", 25m, printed: true, shipment: null);
        Label("yerel-3", "kendi-bitmis", "m2", 30m, printed: true, shipment: null, cancelled: true);
        Label("yerel-4", "kendi-canli", "m2", 40m, printed: false, shipment: null);
        Label("yerel-5", "kendi-canli", "m2", 45m, printed: true, shipment: null, tentative: true);
        Label("uzak-1", "uzak-bitmis", "m3", 60m, printed: true, shipment: "dosya-uzak");
        Label("uzak-2", "uzak-bitmis", "m1", 70m, printed: true, shipment: null);
        Label("uzak-3", "uzak-bitmis", "m3", 80m, printed: true, shipment: "dosya-uzak-gitti");

        using (var scope = SyncApplyScope.Begin(factory))
        {
            scope.Execute(@"INSERT INTO Shipment (Id, CustomerId, Status, CreatedAt, CumulativeAmount, OriginReplicaId) VALUES
                            ('dosya-yerel',      'm1', 'Pending', 300, 50, @other),   -- mod On'da başka PC açtı, etiketi bu PC'nin
                            ('dosya-uzak',       'm3', 'Held',    300, 60, @other),
                            ('dosya-uzak-gitti', 'm3', 'Shipped', 300, 80, @other),
                            ('bos-kendi',        'm2', 'Pending', 300, 0,  @replica),
                            ('bos-eski',         'm2', 'Pending', 300, 0,  NULL),
                            ('bos-baska',        'm3', 'Pending', 300, 0,  @other)", new { other, replica });
            scope.Execute(@"INSERT INTO RemoteLiveSession (Id, Title, StartedAt, OwnerMachineName, OwnerLastSeenAt, State, UnsentLabelCount)
                            VALUES ('uzak-canli', NULL, 400, 'PC-B', 410, 'live', NULL)");
            scope.Commit();
        }
        c.Execute("UPDATE StreamSession SET Authority = 'server' WHERE Id = 'kendi-bitmis'");
        c.Execute("UPDATE SalesSyncState SET Mode = 1 WHERE Id = 1");

        return new ModeOnWorld(replica, other, "yerel-2", "yerel-4", "uzak-2",
            "dosya-yerel", "dosya-uzak-gitti", "bos-kendi", "bos-eski",
            ["yerel-1", "yerel-2", "yerel-3", "yerel-4", "yerel-5"], LocalDigest(c));
    }

    /// <summary>Yerel etiketlerin ve yerel oturumların (Authority hariç) içeriği.</summary>
    private static string LocalDigest(System.Data.IDbConnection c)
    {
        var sb = new StringBuilder();
        foreach (var row in c.Query(@"SELECT l.* FROM Label l JOIN StreamSession s ON s.Id = l.SessionId
                                       WHERE s.Origin = 'local' ORDER BY l.Id"))
            sb.AppendLine(string.Join("|", ((IDictionary<string, object?>)row).Select(kv => $"{kv.Key}={kv.Value}")));
        foreach (var row in c.Query(@"SELECT Id, Title, StartedAt, EndedAt, Platforms, Notes, SyncedAt, Revision, Origin, ReleasedAt
                                       FROM StreamSession WHERE Origin = 'local' ORDER BY Id"))
            sb.AppendLine(string.Join("|", ((IDictionary<string, object?>)row).Select(kv => $"{kv.Key}={kv.Value}")));
        return sb.ToString();
    }

    private static void AssertTotalsDerived(System.Data.IDbConnection c)
    {
        var labels = c.Query<(string CustomerId, double Price, long? PrintedAt, long? CancelledAt, long Tentative, long Fee)>(
            "SELECT CustomerId, Price, PrintedAt, CancelledAt, IsTentativeBackup, IsShippingFee FROM Label").ToList();
        var expected = c.Query<string>("SELECT Id FROM Customer").ToDictionary(id => id, id =>
        {
            var contributions = labels.Where(l => l.CustomerId == id)
                .Select(l => LabelRules.Contribution(new LabelFacts("x", "A1", (decimal)l.Price, l.PrintedAt is not null,
                    l.CancelledAt is not null, l.Tentative == 1, l.Fee == 1, null)))
                .ToList();
            return (Count: (long)contributions.Sum(x => x.Count), Amount: contributions.Sum(x => x.Amount));
        });
        expected.Values.Should().Contain(v => v.Count > 0, "ön koşul: kalan etiketlerin katkısı var");
        foreach (var (id, labelsPrinted, amount) in c.Query<(string, long, double)>("SELECT Id, TotalLabelsPrinted, TotalAmount FROM Customer"))
        {
            labelsPrinted.Should().Be(expected[id].Count, $"{id}: Σ LabelRules.Contribution adedi");
            ((decimal)amount).Should().Be(expected[id].Amount, $"{id}: Σ LabelRules.Contribution tutarı");
        }
    }

    private const string SeedCustomerSql = @"
        INSERT INTO Customer (Id, Platform, Username, DisplayName, FirstSeenAt, LastSeenAt)
        VALUES (@Id, 'instagram', @Username, 'takma', 1, 1)";

    private static object NewOldShipment(string id) => new
    {
        Id = id, CustomerId = "m1", Status = "Pending", CreatedAt = 5000L,
        HeldAt = (long?)null, ShippedAt = (long?)null, CumulativeAmount = 0d, SyncedAt = (long?)null,
    };

    private static string OldLabel(SqliteConnection old, string id, string session, string customer, decimal price, bool tentative = false)
    {
        old.Execute(V0910.LabelInsert, new
        {
            Id = id, SessionId = session, CustomerId = customer, Platform = "instagram", Username = "@eski.surum",
            DisplayName = "takma", MessageText = "A1 aldım", Code = "A1", Price = price, AddedAt = 1050L, PrintedAt = (long?)null,
            IsBackupPromoted = tentative ? 1 : 0, ParentLabelId = (string?)null, IsTentativeBackup = tentative ? 1 : 0,
            IsShippingFee = 0, ShipmentId = (string?)null, SyncedAt = (long?)null, ProductId = (string?)null,
            ProductVariantId = (string?)null,
        }).Should().Be(1);
        return id;
    }

    /// <summary>Eski GetActive'in (birebir SQL) döndürdüğü oturum Id'leri.</summary>
    private static List<string> ActiveSessions(SqliteConnection old)
        => old.Query(V0910.SessionGetActive).Select(r => (string)((IDictionary<string, object>)r)["Id"]).ToList();

    private static long Revision(SqliteConnection c, string id)
        => c.ExecuteScalar<long>("SELECT Revision FROM Label WHERE Id = @id", new { id });

    /// <summary>Prod boyu 048 veritabanı, SQL ile üretilir (hızlı). Satış alanlarının bütün durumları var:
    /// basılmış/basılmamış, iptal, geçici yedek, kargo ücreti, dosyaya bağlı, gönderilmiş/gönderilmemiş,
    /// basış grubu. Kişisel veri yok: Id'ler ve adlar sayılardan.</summary>
    private static void Seed048(IDbConnectionFactory factory, int customers, int sessions, int labels, int shipments)
    {
        using var conn = factory.Open();
        using var tx = conn.BeginTransaction();
        conn.Execute(@"
            WITH RECURSIVE n(i) AS (SELECT 0 UNION ALL SELECT i + 1 FROM n WHERE i + 1 < @customers)
            INSERT INTO Customer (Id, Platform, Username, DisplayName, FirstSeenAt, LastSeenAt, TotalLabelsPrinted, TotalAmount)
            SELECT printf('c%05d', i), CASE i % 3 WHEN 0 THEN 'tiktok' WHEN 1 THEN 'instagram' ELSE 'youtube' END,
                   'kullanici' || i, 'Takma ' || i, 1700000000 + i, 1700000000 + i, i % 7, (i % 7) * 12.5
              FROM n", new { customers }, tx);
        conn.Execute(@"
            WITH RECURSIVE n(i) AS (SELECT 0 UNION ALL SELECT i + 1 FROM n WHERE i + 1 < @sessions)
            INSERT INTO StreamSession (Id, Title, StartedAt, EndedAt, Platforms, Notes, SyncedAt, Revision)
            SELECT printf('s%04d', i), CASE WHEN i % 10 = 0 THEN 'Yayın ' || i END, 1700000000 + i * 3600,
                   CASE WHEN i < @sessions - 1 THEN 1700000000 + i * 3600 + 3000 END, '[""instagram""]', NULL,
                   CASE WHEN i % 9 <> 0 THEN 1700000000 + i * 3600 + 3100 END, i % 4
              FROM n", new { sessions }, tx);
        conn.Execute(@"
            WITH RECURSIVE n(i) AS (SELECT 0 UNION ALL SELECT i + 1 FROM n WHERE i + 1 < @shipments)
            INSERT INTO Shipment (Id, CustomerId, Status, CreatedAt, HeldAt, ShippedAt, CumulativeAmount, SyncedAt, Revision)
            SELECT printf('f%04d', i), printf('c%05d', (i * 7) % @customers),
                   CASE i % 4 WHEN 0 THEN 'Pending' WHEN 1 THEN 'Held' WHEN 2 THEN 'RecipientPays' ELSE 'Shipped' END,
                   1700000000 + i, CASE WHEN i % 4 = 1 THEN 1700000100 + i END, CASE WHEN i % 4 = 3 THEN 1700000200 + i END,
                   (i % 50) * 10.25, CASE WHEN i % 5 <> 0 THEN 1700000300 + i END, i % 3
              FROM n", new { shipments, customers }, tx);
        conn.Execute(@"
            WITH RECURSIVE n(i) AS (SELECT 0 UNION ALL SELECT i + 1 FROM n WHERE i + 1 < @labels)
            INSERT INTO Label (Id, SessionId, CustomerId, Platform, Username, DisplayName, MessageText, Code, Price, AddedAt,
                               PrintedAt, CancelledAt, CancelReason, IsBackupPromoted, ParentLabelId, IsTentativeBackup,
                               IsShippingFee, ShipmentId, SyncedAt, StockSyncedAt, ProductId, ProductVariantId, Revision,
                               PrintBatchId, PrintSeq, PriceCorrectedAt, PaidPrice)
            SELECT printf('l%06d', i), printf('s%04d', i % @sessions), printf('c%05d', (i * 13) % @customers), 'instagram',
                   'kullanici' || ((i * 13) % @customers), 'Takma', 'K' || (i % 300) || ' aldım', 'K' || (i % 300),
                   (i % 40) * 12.5 + 0.5, 1700000000 + i,
                   CASE WHEN i % 4 <> 0 THEN 1700000500 + i END,
                   CASE WHEN i % 17 = 0 THEN 1700000600 + i END, CASE WHEN i % 17 = 0 THEN 'customer' END,
                   CASE WHEN i % 29 = 0 THEN 1 ELSE 0 END, CASE WHEN i % 29 = 0 THEN printf('l%06d', i - 1) END,
                   CASE WHEN i % 58 = 0 THEN 1 ELSE 0 END, CASE WHEN i % 101 = 0 THEN 1 ELSE 0 END,
                   CASE WHEN i % 25 = 1 THEN printf('f%04d', (i / 25) % @shipments) END,
                   CASE WHEN i % 3 <> 0 THEN 1700000700 + i END, CASE WHEN i % 3 <> 0 THEN 1700000700 + i END,
                   CASE WHEN i % 11 = 0 THEN lower(hex(randomblob(16))) END, NULL, i % 6,
                   CASE WHEN i % 4 <> 0 AND i > 150000 THEN printf('b%05d', i / 20) END,
                   CASE WHEN i % 4 <> 0 AND i > 150000 THEN i % 20 + 1 END,
                   CASE WHEN i % 97 = 0 THEN 1700000800 + i END, CASE WHEN i % 194 = 0 THEN (i % 40) * 12.5 END
              FROM n", new { labels, sessions, customers, shipments }, tx);
        tx.Commit();
    }

    // ── anlık görüntü ve denetim ───────────────────────────────────────

    private sealed record Counts(long Customers, long Sessions, long OpenSessions, long Labels, long UnsentLabels,
        long Shipments, long OpenShipments, long FkViolations, IReadOnlyDictionary<string, IReadOnlyList<string>> Columns,
        string Digest);

    private static Counts Snapshot(IDbConnectionFactory factory)
    {
        using var c = factory.Open();
        var columns = SchemaDigest.Columns(c);
        return new Counts(
            c.ExecuteScalar<long>("SELECT COUNT(*) FROM Customer"),
            c.ExecuteScalar<long>("SELECT COUNT(*) FROM StreamSession"),
            c.ExecuteScalar<long>("SELECT COUNT(*) FROM StreamSession WHERE EndedAt IS NULL"),
            c.ExecuteScalar<long>("SELECT COUNT(*) FROM Label"),
            c.ExecuteScalar<long>("SELECT COUNT(*) FROM Label WHERE SyncedAt IS NULL"),
            c.ExecuteScalar<long>("SELECT COUNT(*) FROM Shipment"),
            c.ExecuteScalar<long>("SELECT COUNT(*) FROM Shipment WHERE Status <> 'Shipped'"),
            c.Query("PRAGMA foreign_key_check").LongCount(),
            columns,
            SchemaDigest.Of(c, columns));
    }

    private static void AssertMigrated(IDbConnectionFactory factory, Counts before)
    {
        using var c = factory.Open();
        c.ExecuteScalar<int>("SELECT SchemaVersion FROM _meta WHERE Id = 1").Should().Be(49);
        SchemaDigest.Of(c, before.Columns).Should().Be(before.Digest,
            "yalnız ekleyici: göçten önce var olan her kolonun içeriği (SyncedAt, Revision, StockSyncedAt dahil) aynı");
        c.Query("PRAGMA foreign_key_check").LongCount().Should().Be(before.FkViolations, "yeni FK ihlali yok");
        c.ExecuteScalar<string>("PRAGMA integrity_check").Should().Be("ok");
        c.ExecuteScalar<string>("SELECT ReplicaId FROM _meta WHERE Id = 1").Should().MatchRegex("^[0-9a-f]{32}$");
        c.ExecuteScalar<long>("SELECT COUNT(*) FROM StreamSession WHERE Origin = 'local' AND Authority = 'local'")
            .Should().Be(before.Sessions, "bütün oturumlar bu veritabanında açıldı ve yerel");
        c.ExecuteScalar<long>("SELECT COUNT(*) FROM Shipment WHERE OriginReplicaId = (SELECT ReplicaId FROM _meta WHERE Id = 1)")
            .Should().Be(before.Shipments);
        c.Query<long>("SELECT Mode FROM SalesSyncState").Should().Equal(0L);
        c.ExecuteScalar<long>("SELECT COUNT(*) FROM SyncApplyGuard").Should().Be(0);
    }

    private static string FullDigest(IDbConnectionFactory factory)
    {
        using var c = factory.Open();
        return SchemaDigest.Of(c, SchemaDigest.Columns(c));
    }

    // ── sıfırlama betiği ───────────────────────────────────────────────

    private sealed record Plan(long Mode, long RemoteSessions, long RemoteLabels, long RemoteLive, long SessionsBackToLocal,
        long KeptShipments, long DeletedShipments, long PendingCommands);

    private static string ResetScript()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "OrderDeck.sln")))
            dir = dir.Parent;
        Assert.True(dir is not null, "OrderDeck.sln bulunamadı — depo kökü tespit edilemedi.");
        return File.ReadAllText(Path.Combine(dir!.FullName, "deploy", "scripts", "wpf-sales-sync-reset.sql"));
    }

    /// <summary>Komut satırının bağlantısı gibi: uygulama fonksiyonu kayıtlı değil, yabancı anahtar
    /// denetimi kapalı (sqlite3'ün varsayılanı), havuz yok (kapanınca açık işlem geri sarılır).</summary>
    private static SqliteConnection PlainConnection(string path)
    {
        var c = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = path, ForeignKeys = false, Pooling = false }.ToString());
        c.Open();
        return c;
    }

    /// <summary>Betiği tek komut olarak koşar (Microsoft.Data.Sqlite; ilk hatada durur, <c>sqlite3 -bail</c>
    /// gibi). İlk sonuç kümesi engeller, ikincisi plan.</summary>
    private static (IReadOnlyList<(string Blocker, string Id)> Blockers, Plan Plan) RunResetScript(string path)
    {
        using var c = PlainConnection(path);
        using var cmd = c.CreateCommand();
        cmd.CommandText = ResetScript();
        using var reader = cmd.ExecuteReader();
        var blockers = new List<(string, string)>();
        while (reader.Read()) blockers.Add((reader.GetString(0), reader.GetString(1)));
        reader.NextResult().Should().BeTrue("ikinci liste plan");
        reader.Read().Should().BeTrue();
        var plan = new Plan(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3),
            reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6), reader.GetInt64(7));
        while (reader.NextResult()) { }
        return (blockers, plan);
    }

    /// <summary>Yalnız betiğin ilk listesini (engeller) okur ve işlemi geri sarar.</summary>
    private static IReadOnlyList<(string Blocker, string Id)> ListBlockers(string path)
    {
        using var c = PlainConnection(path);
        using var cmd = c.CreateCommand();
        cmd.CommandText = ResetScript();
        var blockers = new List<(string, string)>();
        using (var reader = cmd.ExecuteReader())
            while (reader.Read()) blockers.Add((reader.GetString(0), reader.GetString(1)));
        return blockers;
    }

    /// <summary><c>sqlite3</c> -bail'siz: ifadeleri sırayla koşar, hatayı kaydedip devam eder.
    /// İfadeler <c>--</c> yorumları atılıp tırnak dışındaki <c>;</c>'den bölünür (betikte tetikleyici
    /// gövdesi yok).</summary>
    private static IReadOnlyList<string> RunResetScriptContinuingOnError(string path)
    {
        var errors = new List<string>();
        using var c = PlainConnection(path);
        foreach (var statement in SplitStatements(ResetScript()))
        {
            try
            {
                using var cmd = c.CreateCommand();
                cmd.CommandText = statement;
                using var reader = cmd.ExecuteReader();
                while (reader.NextResult()) { }
            }
            catch (SqliteException e)
            {
                errors.Add(e.Message);
            }
        }
        return errors;
    }

    private static IEnumerable<string> SplitStatements(string script)
    {
        var current = new StringBuilder();
        var inQuote = false;
        for (var i = 0; i < script.Length; i++)
        {
            var ch = script[i];
            if (!inQuote && ch == '-' && i + 1 < script.Length && script[i + 1] == '-')
            {
                while (i < script.Length && script[i] != '\n') i++;
                current.Append('\n');
                continue;
            }
            if (ch == '\'') inQuote = !inQuote;
            if (!inQuote && ch == ';')
            {
                if (current.ToString().Trim().Length > 0) yield return current.ToString();
                current.Clear();
                continue;
            }
            current.Append(ch);
        }
        if (current.ToString().Trim().Length > 0) yield return current.ToString();
    }

    // ── önceki sürüm ───────────────────────────────────────────────────

    /// <summary>v0.9.10'un bağlantısı: SqliteSearchFunctions.Register'ın kaydettiği beş fonksiyon,
    /// yabancı anahtar açık (SqliteConnectionFactory). 049'un bir nesnesi başka bir fonksiyon çağırsaydı bu
    /// bağlantıdaki yazım "no such function" ile düşerdi.</summary>
    private static SqliteConnection PreviousReleaseConnection(string path)
    {
        var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, ForeignKeys = true }.ToString());
        c.Open();
        c.CreateFunction<string?, string?, string?, string>("od_search_key",
            (u, d, f) => CustomerSearch.BuildSearchKey(u, d, f), isDeterministic: true);
        c.CreateFunction<string?, string>("od_phone_key", p => CustomerSearch.NormalizePhoneKey(p), isDeterministic: true);
        c.CreateFunction<string?, string?>("od_identity_key", u => CustomerIdentity.KeyOrNull(u), isDeterministic: true);
        c.CreateFunction<string?, string?>("od_legacy_group_id", a => a is null ? null : CustomerIdentity.LegacyGroupIdOf(a), isDeterministic: true);
        // Yalnız göç 047'nin ve PaymentJobSql'in ifadeleri çağırır (iç kod); hiçbir şema nesnesi çağırmaz
        // (bkz. sınamadaki altküme denetimi). Kayıtlı olsun diye yer tutucu.
        c.CreateFunction<string?, string?, string?>("od_payment_net", (_, _) => null, isDeterministic: true);
        return c;
    }

    /// <summary>v0.9.10'un yazım ve okuma SQL'i, BİREBİR (<c>git show v0.9.10:OrderDeck.Core/Storage/
    /// Repositories/{SessionRepository,LabelRepository,ShipmentRepository,CustomerRepository}.cs</c>;
    /// <c>CustomerIdSql.Resolve</c> açılmış hâliyle). W1'den önce son sürüm değişirse metinler o sürümden
    /// yenilenir.</summary>
    private static class V0910
    {
        private static string ResolveCustomerId(string parameter) => $@"COALESCE((
            WITH RECURSIVE hop(Id, Depth) AS (
                SELECT ToId, 1 FROM CustomerRedirect WHERE FromId = {parameter}
                UNION ALL
                SELECT r.ToId, hop.Depth + 1
                  FROM CustomerRedirect r JOIN hop ON r.FromId = hop.Id
                 WHERE hop.Depth < 8)
            SELECT Id FROM hop ORDER BY Depth DESC LIMIT 1), {parameter})";

        // SessionRepository
        public const string SessionInsert =
            @"INSERT INTO StreamSession(Id, Title, StartedAt, EndedAt, Platforms, Notes, SyncedAt)
              VALUES(@Id, @Title, @StartedAt, @EndedAt, @Platforms, @Notes, @SyncedAt)";

        public const string SessionEnd =
            "UPDATE StreamSession SET EndedAt=@endedAt, SyncedAt=NULL, Revision = Revision + 1 WHERE Id=@id";

        public const string SessionGetActive =
            "SELECT Id, Title, StartedAt, EndedAt, Platforms, Notes, SyncedAt, Revision " +
            "FROM StreamSession WHERE EndedAt IS NULL ORDER BY StartedAt DESC LIMIT 1";

        // LabelRepository
        public static readonly string LabelInsert =
            @"INSERT INTO Label
              (Id, SessionId, CustomerId, Platform, Username, DisplayName, MessageText, Code, Price, AddedAt, PrintedAt,
               IsBackupPromoted, ParentLabelId, IsTentativeBackup, IsShippingFee, ShipmentId, SyncedAt,
               StockSyncedAt, ProductId, ProductVariantId)
              VALUES
              (@Id, @SessionId, " + ResolveCustomerId("@CustomerId") + @", @Platform, @Username, @DisplayName,
               @MessageText, @Code, @Price, @AddedAt, @PrintedAt,
               @IsBackupPromoted, @ParentLabelId, @IsTentativeBackup, @IsShippingFee, @ShipmentId, @SyncedAt,
               @SyncedAt, @ProductId, @ProductVariantId)";

        public const string MarkPrintedBatch =
            @"UPDATE Label SET PrintedAt=@printedAt, PrintBatchId=@batchId, PrintSeq=@seq,
                     SyncedAt=NULL, Revision = Revision + 1
              WHERE Id=@id";

        public const string MarkCancelled =
            "UPDATE Label SET CancelledAt=@cancelledAt, CancelReason=@reason, SyncedAt=NULL, StockSyncedAt=NULL, Revision = Revision + 1 WHERE Id IN @ids";

        public const string Uncancel =
            "UPDATE Label SET CancelledAt=NULL, CancelReason=NULL, SyncedAt=NULL, StockSyncedAt=NULL, Revision = Revision + 1 WHERE Id IN @ids";

        public const string UpdatePriceForCorrection =
            @"UPDATE Label SET Price=@newPrice,
                     PriceCorrectedAt = CASE WHEN PrintedAt IS NOT NULL THEN @correctedAt ELSE PriceCorrectedAt END,
                     PaidPrice = CASE WHEN PaidPrice IS NULL AND (ShipmentId IS NOT NULL OR @paidFromBalance = 1)
                                      THEN Price ELSE PaidPrice END,
                     SyncedAt=NULL, Revision = Revision + 1
              WHERE Id=@id AND abs(Price - @expectedPrice) < 0.00001 AND CancelledAt IS NULL
                AND (PrintedAt IS NOT NULL) = @expectPrinted
                AND IsTentativeBackup = @expectTentative";

        public const string ConfirmTentativeBackups =
            "UPDATE Label SET IsTentativeBackup = 0, SyncedAt=NULL, StockSyncedAt=NULL, Revision = Revision + 1 WHERE Id IN @ids AND IsTentativeBackup = 1";

        public const string LabelMarkSynced =
            "UPDATE Label SET SyncedAt=@syncedAt, StockSyncedAt=@syncedAt WHERE Id=@id AND Revision=@revision";

        // ShipmentRepository
        public static readonly string ShipmentInsert =
            @"INSERT INTO Shipment
              (Id, CustomerId, Status, CreatedAt, HeldAt, ShippedAt, CumulativeAmount, SyncedAt)
              VALUES
              (@Id, " + ResolveCustomerId("@CustomerId") + @", @Status, @CreatedAt, @HeldAt, @ShippedAt,
               @CumulativeAmount, @SyncedAt)";

        public const string ShipmentUpdate =
            @"UPDATE Shipment SET
                Status=@Status, HeldAt=@HeldAt, ShippedAt=@ShippedAt,
                CumulativeAmount=@CumulativeAmount,
                SyncedAt=NULL, Revision = Revision + 1
              WHERE Id=@Id";

        public const string AttachLabel =
            "UPDATE Label SET ShipmentId=@shipmentId WHERE Id=@labelId";

        // CustomerRepository
        public static readonly string IncrementLabelStats =
            @"UPDATE Customer
              SET TotalLabelsPrinted = TotalLabelsPrinted + @labelDelta,
                  TotalAmount        = TotalAmount + @amountDelta,
                  LastSeenAt         = @lastSeenAt
              WHERE Id = " + ResolveCustomerId("@id");
    }
}
