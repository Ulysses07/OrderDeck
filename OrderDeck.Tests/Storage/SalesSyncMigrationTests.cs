using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using OrderDeck.Core.Chat;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Sales;
using OrderDeck.Core.Sessions;
using OrderDeck.Core.Settings;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Core.Time;
using OrderDeck.Tests.TestHelpers;
using Moq;
using Xunit;

namespace OrderDeck.Tests.Storage;

/// <summary>
/// Göç 049 (spec 2026-10-13 §3.2): kolonlar, tablolar ve dokuz tetikleyici. Mod Off'ta (bugün her
/// lisans) ABORT tetikleyicilerinin hiçbiri etkili olmaz ve canlı yollar bugünkü gibi yazar; tek fark
/// kuyruğa alma tetikleyicileridir. ABORT'lar yalnız kendi durumlarında (Mode = 1, Authority =
/// 'server') ve kilit (SyncApplyGuard) yokken düşer. Önceki sürümle uyum ve prod boyu prova
/// Migration049RehearsalTests'te; yazıcı envanteri SalesWriterInventoryTests'te.
/// </summary>
public sealed class SalesSyncMigrationTests : IDisposable
{
    private const string Session = "s1";
    private const string Customer = "c1";

    private readonly InMemorySqlite _db = new();
    private readonly LabelRepository _labels;
    private readonly ShipmentRepository _shipments;
    private readonly SessionRepository _sessions;

    public SalesSyncMigrationTests()
    {
        new MigrationRunner(_db).Run();
        _labels = new LabelRepository(_db);
        _shipments = new ShipmentRepository(_db);
        _sessions = new SessionRepository(_db);
        _sessions.Insert(new StreamSession(Session, null, 100, null, new[] { "instagram" }, null));
        InsertCustomer(Customer, "@alici");
    }

    public void Dispose() => _db.Dispose();

    // ── 1–2: taze veritabanı ───────────────────────────────────────────

    [Fact]
    public void Taze_veritabani_sema_49_ve_kopya_kimligi_32_kucuk_onaltilik()
    {
        using var c = _db.Open();
        c.ExecuteScalar<int>("SELECT SchemaVersion FROM _meta WHERE Id = 1").Should().Be(49);
        var replica = c.ExecuteScalar<string>("SELECT ReplicaId FROM _meta WHERE Id = 1");
        replica.Should().MatchRegex("^[0-9a-f]{32}$");
        Guid.TryParseExact(replica, "N", out _).Should().BeTrue("hello ve sunucu kimliği Guid olarak taşır");
        c.ExecuteScalar<string?>("SELECT PreviousReplicaId FROM _meta WHERE Id = 1")
            .Should().BeNull("yalnız geri yükleme yazar (§8.7)");

        using var other = new InMemorySqlite();
        new MigrationRunner(other).Run();
        using var o = other.Open();
        o.ExecuteScalar<string>("SELECT ReplicaId FROM _meta WHERE Id = 1")
            .Should().NotBe(replica, "her veritabanı kendi kimliğini üretir");
    }

    [Fact]
    public void Taze_veritabaninda_tek_durum_satiri_mod_0_ve_her_tablo_ile_kolon_var()
    {
        using var c = _db.Open();
        c.Query<(long Id, long Mode, long Scrub)>("SELECT Id, Mode, ScrubOrderText FROM SalesSyncState")
            .Should().Equal((1L, 0L, 0L));
        c.ExecuteScalar<long>(@"SELECT COUNT(*) FROM SalesSyncState
                                 WHERE COALESCE(ModeSeenAt, ShippingVersion, FreeShippingThreshold, ShippingFee,
                                                FirstCatchUpAt, AggregatesDerivedAt, ShipmentsDerivedAt,
                                                PaymentHistorySentAt) IS NOT NULL")
            .Should().Be(0, "bir kez yapılan işlerin damgaları boş başlar");

        var expected = new Dictionary<string, string[]>
        {
            ["_meta"] = ["ReplicaId", "PreviousReplicaId"],
            ["StreamSession"] = ["Origin", "Authority", "OwnerMachineName", "ReleasedAt"],
            ["Label"] = ["ServerVersion"],
            ["Shipment"] = ["ServerVersion", "OriginReplicaId"],
            ["RemoteLiveSession"] = ["Id", "Title", "StartedAt", "EndedAt", "OwnerMachineName", "OwnerLastSeenAt", "State", "UnsentLabelCount"],
            ["SalesSyncState"] = ["Id", "Mode", "ModeSeenAt", "ShippingVersion", "FreeShippingThreshold", "ShippingFee",
                                  "ScrubOrderText", "FirstCatchUpAt", "AggregatesDerivedAt", "ShipmentsDerivedAt", "PaymentHistorySentAt"],
            ["PendingPaymentRecord"] = ["RecordId", "Kind", "PayloadJson", "CreatedAt", "Attempts", "LastError"],
            ["PendingSalesCommand"] = ["CommandId", "Kind", "PayloadJson", "CreatedAt", "Attempts", "LastError"],
            ["PendingSalesCommandLabel"] = ["LabelId", "CommandId"],
            ["SalesFeedFailure"] = ["Kind", "ItemId", "ChangeSeq", "Attempts", "LastError", "FirstFailedAt", "SkippedAt"],
            ["SalesConflictLog"] = ["Id", "At", "LabelId", "Kind", "Reason"],
        };
        foreach (var (table, columns) in expected)
            c.Query<string>("SELECT name FROM pragma_table_info(@table)", new { table })
                .Should().Contain(columns, $"§3.2: {table}");

        c.Query<string>("SELECT name FROM pragma_table_info('PendingSalesCommandLabel') WHERE pk > 0 ORDER BY pk")
            .Should().Equal("LabelId", "CommandId");
        c.Query<string>("SELECT name FROM pragma_table_info('SalesFeedFailure') WHERE pk > 0 ORDER BY pk")
            .Should().Equal("Kind", "ItemId");
        c.ExecuteScalar<long>("SELECT COUNT(*) FROM pragma_table_info('SalesConflictLog') WHERE name = 'Id' AND pk = 1 AND type = 'INTEGER'")
            .Should().Be(1);

        // Önceki sürümün eklediği satır varsayılanla 'local' doğar.
        c.Query<(string Origin, string Authority)>("SELECT Origin, Authority FROM StreamSession WHERE Id = @Session", new { Session })
            .Should().Equal(("local", "local"));
        c.Query<string>(@"SELECT name FROM sqlite_master WHERE type = 'trigger'
                           AND tbl_name IN ('Label', 'Shipment', 'StreamSession') ORDER BY name")
            .Should().BeEquivalentTo(Triggers049, "§3.2 tablosunun dokuz tetikleyicisi; bu tablolarda başka tetikleyici yok");
    }

    private static readonly string[] Triggers049 =
    [
        "TR_Label_Enqueue", "TR_Label_ServerAuthority_U", "TR_Label_ServerAuthority_I", "TR_Label_ShipmentLink",
        "TR_Shipment_ServerOwned_I", "TR_Shipment_ServerOwned_U", "TR_Shipment_Enqueue",
        "TR_StreamSession_ServerAuthority", "TR_StreamSession_Enqueue",
    ];

    // ── 3: dolu 048 veritabanı ─────────────────────────────────────────

    [Fact]
    public void Dolu_048_veritabani_var_olan_veriyi_degistirmeden_49a_gecer()
    {
        using var db = new InMemorySqlite();
        new MigrationRunner(db, EmbeddedMigrationScripts.UpTo(48)).Run();
        using (var c = db.Open())
        {
            c.Execute(@"INSERT INTO Customer (Id, Platform, Username, FirstSeenAt, LastSeenAt, TotalLabelsPrinted, TotalAmount)
                        VALUES ('c1', 'instagram', '@a', 1, 1, 1, 100), ('c2', 'tiktok', '@b', 1, 1, 0, 0)");
            c.Execute(@"INSERT INTO StreamSession (Id, StartedAt, EndedAt, SyncedAt, Revision) VALUES
                        ('s-bitti', 100, 200, 210, 3), ('s-acik', 300, NULL, NULL, 0)");
            c.Execute(@"INSERT INTO Shipment (Id, CustomerId, Status, CreatedAt, CumulativeAmount, SyncedAt, Revision) VALUES
                        ('f-acik', 'c1', 'Held', 150, 100, 160, 2), ('f-gitti', 'c2', 'Shipped', 120, 40, NULL, 1)");
            c.Execute(@"INSERT INTO Label (Id, SessionId, CustomerId, Platform, Username, MessageText, Code, Price, AddedAt,
                                           PrintedAt, CancelledAt, IsTentativeBackup, IsShippingFee, ShipmentId, SyncedAt,
                                           StockSyncedAt, Revision, PrintBatchId, PrintSeq, PriceCorrectedAt, PaidPrice) VALUES
                        ('l1', 's-bitti', 'c1', 'instagram', '@a', 'A1 aldım', 'A1', 100, 110, 120, NULL, 0, 0, 'f-acik', 130, 130, 2, 'b1', 1, NULL, NULL),
                        ('l2', 's-bitti', 'c2', 'tiktok', '@b', 'B1', 'B1', 40, 111, NULL, 140, 0, 0, NULL, NULL, NULL, 1, NULL, NULL, NULL, NULL),
                        ('l3', 's-acik', 'c2', 'tiktok', '@b', 'yedek', 'A1', 90.5, 310, NULL, NULL, 1, 0, NULL, NULL, NULL, 0, NULL, NULL, 315, 95)");
        }
        IReadOnlyDictionary<string, IReadOnlyList<string>> columns;
        string before;
        using (var c = db.Open())
        {
            columns = SchemaDigest.Columns(c);
            before = SchemaDigest.Of(c, columns);
        }

        new MigrationRunner(db).Run();

        using var conn = db.Open();
        conn.ExecuteScalar<int>("SELECT SchemaVersion FROM _meta WHERE Id = 1").Should().Be(49);
        SchemaDigest.Of(conn, columns).Should().Be(before,
            "yalnız ekleyici: var olan kolonların içeriği (SyncedAt, Revision, StockSyncedAt dahil) değişmez — tohum yazımları tetikleyicilerden önce");
        conn.Query<(string Origin, string Authority)>("SELECT Origin, Authority FROM StreamSession")
            .Should().OnlyContain(r => r.Origin == "local" && r.Authority == "local");
        var replica = conn.ExecuteScalar<string>("SELECT ReplicaId FROM _meta WHERE Id = 1");
        conn.Query<string?>("SELECT OriginReplicaId FROM Shipment").Should().HaveCount(2).And.OnlyContain(r => r == replica,
            "049'dan önce dosya yalnız bu veritabanında açılabiliyordu");
        conn.ExecuteScalar<long>("SELECT COUNT(*) FROM Label WHERE ServerVersion IS NOT NULL").Should().Be(0);
        conn.ExecuteScalar<long>("SELECT COUNT(*) FROM Shipment WHERE ServerVersion IS NOT NULL").Should().Be(0);
        conn.Query("PRAGMA foreign_key_check").Should().BeEmpty();
        conn.ExecuteScalar<string>("PRAGMA integrity_check").Should().Be("ok");
    }

    // ── 4: TR_Label_Enqueue ───────────────────────────────────────────

    [Fact]
    public void AttachLabel_etiketi_tam_bir_kez_kuyruga_alir_stok_damgasina_dokunmaz()
    {
        var id = SyncedLabel();
        _shipments.Insert(NewShipment("f1"));
        var before = State(id);

        _shipments.AttachLabel("f1", id);

        var after = State(id);
        after.SyncedAt.Should().BeNull("049'a kadar kuyruğa almayan tek yol buydu (§3.2 notları)");
        after.Revision.Should().Be(before.Revision + 1);
        after.StockSyncedAt.Should().Be(before.StockSyncedAt, "kargo bağı stok-ilgili değil (030)");

        _labels.MarkSynced(id, 900, after.Revision);
        _shipments.AttachLabel("f1", id);
        State(id).Should().Be(((long?)900L, (long?)900L, after.Revision), "aynı dosyaya yeniden bağlamak değişiklik değil");
    }

    public static TheoryData<string, string, string> RevisionWriters => new()
    {
        // yazıcı, yazdığı kolon, aynı kolona ham (Revision'sız) değer
        { "MarkCancelled", "CancelledAt", "600" },
        { "Uncancel", "CancelledAt", "NULL" },
        { "MarkPrinted", "PrintedAt", "600" },
        { "MarkPrintedBatch", "PrintBatchId", "'b-ham'" },
        { "UpdatePriceForCorrection", "Price", "61" },
        { "UpdatePrice", "Price", "76" },
        { "ConfirmTentativeBackups", "IsTentativeBackup", "0" },
    };

    [Theory]
    [MemberData(nameof(RevisionWriters))]
    public void Revision_artiran_yazicilar_yine_tam_bir_artis_alir(string writer, string column, string raw)
    {
        bool tentative = writer == "ConfirmTentativeBackups", printed = writer == "UpdatePriceForCorrection",
             cancelled = writer == "Uncancel";

        // Kontrol: tetikleyici bu kolonu izliyor — aynı kolona Revision'sız ham yazım kuyruğa alınır.
        var control = SyncedLabel(tentative, printed, cancelled);
        var controlBefore = State(control);
        Exec($"UPDATE Label SET {column} = {raw} WHERE Id = @control", new { control });
        State(control).Revision.Should().Be(controlBefore.Revision + 1, $"ön koşul: TR_Label_Enqueue {column}'u izler");

        var id = SyncedLabel(tentative, printed, cancelled);
        var before = State(id);

        switch (writer)
        {
            case "MarkCancelled": _labels.MarkCancelled([id], 500, CancelReasonCodes.Customer); break;
            case "Uncancel": _labels.Uncancel([id]); break;
            case "MarkPrinted": _labels.MarkPrinted([id], 500); break;
            case "MarkPrintedBatch": _labels.MarkPrintedBatch([(id, 1)], 500, "batch-1"); break;
            case "UpdatePrice": _labels.UpdatePrice(id, 75m); break;
            case "ConfirmTentativeBackups": _labels.ConfirmTentativeBackups([id]); break;
            case "UpdatePriceForCorrection":
                using (var write = DbWrite.Begin(_db))
                {
                    _labels.UpdatePriceForCorrection(id, 50m, expectPrinted: true, expectTentative: false,
                        paidFromBalance: false, newPrice: 60m, correctedAt: 500, write).Should().Be(1);
                    write.Commit();
                }
                break;
            default: throw new ArgumentOutOfRangeException(nameof(writer));
        }

        var after = State(id);
        after.Revision.Should().Be(before.Revision + 1, "yazıcı Revision'ı kendisi artırdı; tetikleyici etkisiz, çift artış yok");
        after.SyncedAt.Should().BeNull();
    }

    [Fact]
    public void Ayni_degeri_yazmak_kuyruga_almaz()
    {
        var id = SyncedLabel();
        var before = State(id);

        Exec(@"UPDATE Label SET Price = Price, CancelledAt = CancelledAt, CancelReason = CancelReason, PrintedAt = PrintedAt,
                                PrintBatchId = PrintBatchId, PrintSeq = PrintSeq, IsTentativeBackup = IsTentativeBackup,
                                PriceCorrectedAt = PriceCorrectedAt, PaidPrice = PaidPrice, ShipmentId = ShipmentId,
                                ParentLabelId = ParentLabelId
               WHERE Id = @id", new { id });

        State(id).Should().Be(before, "NULL'a dayanıklı karşılaştırma: aynı değer düzenleme değil");

        Exec("UPDATE Label SET Price = Price + 1, CancelReason = CancelReason WHERE Id = @id", new { id });
        State(id).Revision.Should().Be(before.Revision + 1, "kontrol: gerçek değişiklik kuyruğa alınır");
    }

    [Fact]
    public void SyncApplyScope_icindeki_yazim_kuyruga_almaz()
    {
        var id = SyncedLabel();
        var before = State(id);

        using (var scope = SyncApplyScope.Begin(_db))
        {
            scope.Execute("UPDATE Label SET Price = 77, CancelledAt = 600, ShipmentId = 'f-sunucu', ServerVersion = 4 WHERE Id = @id", new { id });
            scope.Commit();
        }

        State(id).Should().Be(before, "akış ve yanıt uygulaması gönderim kuyruğuna dokunmaz (DM8)");
    }

    [Fact]
    public void MarkSynced_ve_sunucu_surumu_kuyruga_almaz_F05_karsilastir_yaz_bozulmaz()
    {
        var id = InsertLabel();
        var inFlight = _labels.GetById(id)!.Revision;

        // Push uçuştayken düzenleme (tetikleyici yolu: Revision'sız ham yazım).
        Exec("UPDATE Label SET Price = 88 WHERE Id = @id", new { id });
        _labels.MarkSynced(id, 700, inFlight);
        State(id).SyncedAt.Should().BeNull("uçuştaki onay düzenlemeyi ezemez — tetikleyici Revision'ı artırdı");

        var current = State(id).Revision;
        _labels.MarkSynced(id, 800, current);
        Exec("UPDATE Label SET ServerVersion = 9 WHERE Id = @id", new { id });
        State(id).Should().Be(((long?)800L, (long?)800L, current), "SyncedAt, StockSyncedAt ve ServerVersion hiçbir tetikleyici listesinde değil");
    }

    public static TheoryData<string, string, bool> EnqueueColumns => new()
    {
        // kolon, yeni değer (SQL), StockSyncedAt düşer mi
        { "Price", "99", false },
        { "CancelledAt", "600", true },
        { "CancelReason", "'customer'", false },
        { "PrintedAt", "600", false },
        { "PrintBatchId", "'b-2'", false },
        { "PrintSeq", "7", false },
        { "IsTentativeBackup", "1", true },
        { "PriceCorrectedAt", "600", false },
        { "PaidPrice", "45", false },
        { "ShipmentId", "'f-yerel'", false },
        { "ParentLabelId", "'ebeveyn'", false },
    };

    [Theory]
    [MemberData(nameof(EnqueueColumns))]
    public void Ham_birim_degisikligi_kuyruga_alinir_stok_damgasi_yalniz_stok_ilgili_alanda_duser(
        string column, string value, bool stockRelevant)
    {
        var id = SyncedLabel();
        var before = State(id);

        Exec($"UPDATE Label SET {column} = {value} WHERE Id = @id", new { id });

        var after = State(id);
        after.SyncedAt.Should().BeNull($"{column} değişti, yazan Revision'ı artırmadı");
        after.Revision.Should().Be(before.Revision + 1);
        if (stockRelevant) after.StockSyncedAt.Should().BeNull($"{column} stok-ilgili (030)");
        else after.StockSyncedAt.Should().Be(before.StockSyncedAt, $"{column} stok defterini değiştirmez");
    }

    [Fact]
    public void Liste_disi_kolon_kuyruga_almaz_yeniden_anahtarlama_dahil()
    {
        InsertCustomer("c2", "@baska");
        var id = SyncedLabel();
        var before = State(id);

        Exec("UPDATE Label SET CustomerId = 'c2' WHERE Id = @id", new { id });
        Exec("UPDATE Label SET DisplayName = 'yeni takma' WHERE Id = @id", new { id });

        State(id).Should().Be(before, "CustomerId ve oluşum alanları kuyruğa alma listesinde değil (§3.2)");

        Exec("UPDATE Label SET ShipmentId = 'f-yerel', CustomerId = @Customer WHERE Id = @id", new { id, Customer });
        State(id).Revision.Should().Be(before.Revision + 1, "kontrol: listedeki birim aynı ifadede kuyruğa alır");
    }

    // ── 5: TR_Shipment_Enqueue ve TR_StreamSession_Enqueue ──────────────

    public static TheoryData<string, string> ShipmentColumns => new()
    {
        { "Status", "'Held'" }, { "HeldAt", "600" }, { "ShippedAt", "600" }, { "CumulativeAmount", "321.5" },
    };

    [Theory]
    [MemberData(nameof(ShipmentColumns))]
    public void Mod_0_ham_dosya_degisikligi_kuyruga_alinir_kilit_altinda_susar(string column, string value)
    {
        _shipments.Insert(NewShipment("f1"));
        var revision = _shipments.GetById("f1")!.Revision;
        _shipments.MarkSynced("f1", 500, revision);

        Exec($"UPDATE Shipment SET {column} = {value} WHERE Id = 'f1'");

        var after = _shipments.GetById("f1")!;
        after.SyncedAt.Should().BeNull("Y5'in yerel yolu dahil, değişen dosya shipments/sync ile gider (§7.4)");
        after.Revision.Should().Be(revision + 1);

        _shipments.MarkSynced("f1", 700, after.Revision);
        using (var scope = SyncApplyScope.Begin(_db))
        {
            scope.Execute("UPDATE Shipment SET Status = 'RecipientPays', HeldAt = 1, ShippedAt = 2, CumulativeAmount = 5 WHERE Id = 'f1'");
            scope.Commit();
        }
        _shipments.GetById("f1")!.Should().Match<Shipment>(s => s.SyncedAt == 700 && s.Revision == after.Revision);

        Exec("UPDATE Shipment SET Status = Status, HeldAt = HeldAt, ShippedAt = ShippedAt, CumulativeAmount = CumulativeAmount WHERE Id = 'f1'");
        _shipments.GetById("f1")!.SyncedAt.Should().Be(700, "aynı değer düzenleme değil");
    }

    [Fact]
    public void ShipmentRepository_Update_yine_tam_bir_artis_alir()
    {
        // Kontrol: tetikleyici dört kolonu izliyor — Revision'sız ham yazım kuyruğa alınır.
        _shipments.Insert(NewShipment("f-kontrol"));
        var control = _shipments.GetById("f-kontrol")!.Revision;
        Exec("UPDATE Shipment SET Status = 'Held', HeldAt = 600, CumulativeAmount = 10 WHERE Id = 'f-kontrol'");
        _shipments.GetById("f-kontrol")!.Revision.Should().Be(control + 1, "ön koşul: TR_Shipment_Enqueue bu kolonları izler");

        _shipments.Insert(NewShipment("f1"));
        var s = _shipments.GetById("f1")!;

        _shipments.Update(s with { Status = ShipmentStatus.Held, HeldAt = 600, CumulativeAmount = 10m });

        _shipments.GetById("f1")!.Revision.Should().Be(s.Revision + 1, "Update Revision'ı kendisi artırır; çift artış yok");
    }

    [Fact]
    public void Mod_1de_ABORT_olmasa_da_dosya_kuyruga_alinmaz()
    {
        // Savunma: dosya mod 1'de sunucunundur. ABORT tetikleyicisi bu yazımı zaten düşürür; kuyruğa alma
        // koşulunun kendisi de mod 0 ister (yerel ayna gönderilmez).
        _shipments.Insert(NewShipment("f1"));
        var revision = _shipments.GetById("f1")!.Revision;
        _shipments.MarkSynced("f1", 500, revision);
        Exec("DROP TRIGGER TR_Shipment_ServerOwned_U");
        SetMode(1);

        Exec("UPDATE Shipment SET Status = 'Held' WHERE Id = 'f1'");

        _shipments.GetById("f1")!.Should().Match<Shipment>(s => s.SyncedAt == 500 && s.Revision == revision);
    }

    public static TheoryData<string, string> SessionColumns => new()
    {
        { "EndedAt", "900" }, { "Title", "'yeni başlık'" }, { "Platforms", "'[\"tiktok\"]'" }, { "Notes", "'not'" },
    };

    [Theory]
    [MemberData(nameof(SessionColumns))]
    public void Ham_oturum_degisikligi_kuyruga_alinir_kilit_altinda_susar(string column, string value)
    {
        var revision = _sessions.GetById(Session)!.Revision;
        _sessions.MarkSynced(Session, 500, revision);

        Exec($"UPDATE StreamSession SET {column} = {value} WHERE Id = @Session", new { Session });

        var after = _sessions.GetById(Session)!;
        after.SyncedAt.Should().BeNull();
        after.Revision.Should().Be(revision + 1);

        _sessions.MarkSynced(Session, 700, after.Revision);
        using (var scope = SyncApplyScope.Begin(_db))
        {
            scope.Execute("UPDATE StreamSession SET EndedAt = 1000, Title = 'akış', Notes = 'akış', ReleasedAt = 1001 WHERE Id = @Session", new { Session });
            scope.Commit();
        }
        _sessions.GetById(Session)!.Should().Match<StreamSession>(s => s.SyncedAt == 700 && s.Revision == after.Revision);
    }

    [Fact]
    public void SessionRepository_End_yine_tam_bir_artis_alir_devir_yazimlari_kuyruga_almaz()
    {
        var revision = _sessions.GetById(Session)!.Revision;
        _sessions.End(Session, 900);
        var ended = _sessions.GetById(Session)!;
        ended.Revision.Should().Be(revision + 1);

        _sessions.MarkSynced(Session, 950, ended.Revision);
        Exec("UPDATE StreamSession SET ReleasedAt = 960, OwnerMachineName = 'PC-A' WHERE Id = @Session", new { Session });
        _sessions.GetById(Session)!.Should().Match<StreamSession>(s => s.SyncedAt == 950 && s.Revision == ended.Revision,
            "ReleasedAt ve OwnerMachineName kuyruğa alma listesinde değil");
    }

    // ── 6: mod Off'ta canlı yollar kırılmaz ─────────────────────────────

    [Fact]
    public void Mod_Offta_ABORT_tetikleyicileri_etkisiz_canli_ve_yayin_sonrasi_yollar_yazar()
    {
        var clock = new Mock<IClock>();
        clock.Setup(c => c.UnixNow()).Returns(1000L);
        var customers = new CustomerRepository(_db);
        var svc = new LabelService(_labels, new CustomerService(customers, _sessions, _labels, clock.Object), _db, clock.Object, _shipments);
        var settings = new AppSettings();
        settings.Shipping.FreeShippingThreshold = 500m;
        settings.Shipping.ShippingFee = 60m;
        var shipping = new ShipmentService(_shipments, _labels, () => settings, () => 1000L);

        // Canlı yayın (§5.1): sohbetten etiket, yedek, baskı, FİYAT kutusu, iptal ve geri alma, yedek onayı.
        _sessions.Insert(new StreamSession("canli", null, 1000, null, new[] { "instagram" }, null));
        var a = svc.Add("canli", Msg("@a"), 100m, "A1");
        var b = svc.Add("canli", Msg("@b"), 50m, "B1");
        var backup = svc.AddBackup(a.Id, "instagram", "@c", "@c", "yedek");
        svc.MarkPrintedAndRecord([a.Id, b.Id, backup.Id]);
        svc.CorrectPrices([a.Id], 120m);
        svc.Cancel([b.Id], CancelReasonCodes.Customer);
        svc.Uncancel([b.Id]);
        svc.ConfirmBackup(backup.Id, 110m);
        _sessions.End("canli", 2000);

        // Mod Off'ta devir görüldü: yalnız ReleasedAt saklanır, Authority 'local' kalır (§2, §5.2) —
        // bitmiş kendi yayınında yerel yol çalışmaya devam eder (§10.3).
        Exec("UPDATE StreamSession SET ReleasedAt = 2001 WHERE Id = 'canli'");
        var buyer = customers.FindByPlatformAndUsername("instagram", "@a")!.Id;
        var file = shipping.GetOrCreateOpenShipment(buyer);
        shipping.AttachLabels(file.Id, [a.Id]);
        svc.CorrectPrices([a.Id], 130m);
        svc.Cancel([a.Id], CancelReasonCodes.Customer);
        svc.Uncancel([a.Id]);
        shipping.ApplyDecision(file.Id, ShipmentDecision.Hold);
        _sessions.Insert(new StreamSession("sonraki", null, 3000, null, new[] { "instagram" }, null));

        using var c = _db.Open();
        c.Query<(string Id, string Authority)>("SELECT Id, Authority FROM StreamSession ORDER BY Id")
            .Should().OnlyContain(r => r.Authority == "local");
        c.ExecuteScalar<decimal>("SELECT Price FROM Label WHERE Id = @Id", new { a.Id }).Should().Be(130m);
        c.ExecuteScalar<long?>("SELECT CancelledAt FROM Label WHERE Id = @Id", new { a.Id }).Should().BeNull();
        c.ExecuteScalar<string>("SELECT ShipmentId FROM Label WHERE Id = @Id", new { a.Id }).Should().Be(file.Id);
        _shipments.GetById(file.Id)!.Status.Should().Be(ShipmentStatus.Held);
        c.ExecuteScalar<long>("SELECT IsTentativeBackup FROM Label WHERE Id = @Id", new { backup.Id }).Should().Be(0);
        _sessions.GetActive()!.Id.Should().Be("sonraki");
    }

    // ── 7: ABORT yalnız kendi durumunda ─────────────────────────────────

    public static TheoryData<string, string> AuthorityColumns => new()
    {
        { "Price", "99" }, { "PriceCorrectedAt", "600" }, { "PaidPrice", "45" }, { "CancelledAt", "600" },
        { "CancelReason", "'customer'" }, { "IsTentativeBackup", "1" }, { "ShipmentId", "'f-yerel'" },
        { "PrintedAt", "600" }, { "PrintBatchId", "'b-2'" }, { "PrintSeq", "3" },
        { "SessionId", "'s-diger'" }, { "Platform", "'tiktok'" }, { "Username", "'@x'" }, { "DisplayName", "'x'" },
        { "MessageText", "'x'" }, { "Code", "'Z9'" }, { "AddedAt", "601" }, { "IsShippingFee", "1" },
        { "IsBackupPromoted", "1" }, { "ProductId", "'p'" }, { "ProductVariantId", "'v'" }, { "ParentLabelId", "'ebeveyn'" },
    };

    [Theory]
    [MemberData(nameof(AuthorityColumns))]
    public void Sunucu_otoritesindeki_oturumun_etiketine_birim_ya_da_olusum_yazimi_duser(string column, string value)
    {
        _sessions.Insert(new StreamSession("s-diger", null, 50, 60, new[] { "instagram" }, null));
        var id = SyncedLabel();
        SetAuthority(Session, "server");
        var before = Row(id);

        var act = () => Exec($"UPDATE Label SET {column} = {value} WHERE Id = @id", new { id });

        act.Should().Throw<SqliteException>().WithMessage("*sales-server-authority*");
        Row(id).Should().Be(before, "ifade geri alındı");

        using (var scope = SyncApplyScope.Begin(_db))
        {
            scope.Execute($"UPDATE Label SET {column} = {value} WHERE Id = @id", new { id });
            scope.Commit();
        }
        Row(id).Should().NotBe(before, "akış ve komut yanıtı kilit altında yazar");
    }

    [Fact]
    public void Sunucu_otoritesinde_yeniden_anahtarlama_onay_ve_surum_yazimi_gecer_ekleme_duser()
    {
        InsertCustomer("c2", "@baska");
        var id = InsertLabel();
        SetAuthority(Session, "server");

        Exec("UPDATE Label SET CustomerId = 'c2' WHERE Id = @id", new { id });
        _labels.MarkSynced(id, 700, _labels.GetById(id)!.Revision);
        Exec("UPDATE Label SET ServerVersion = 12 WHERE Id = @id", new { id });
        Exec("UPDATE Label SET Price = Price, Code = Code WHERE Id = @id", new { id });

        using (var c = _db.Open())
            c.QuerySingle<(string CustomerId, long? SyncedAt, long? ServerVersion)>(
                    "SELECT CustomerId, SyncedAt, ServerVersion FROM Label WHERE Id = @id", new { id })
                .Should().Be(("c2", (long?)700L, (long?)12L));

        var insert = () => InsertLabel();
        insert.Should().Throw<SqliteException>().WithMessage("*sales-server-authority*");

        string added;
        using (var scope = SyncApplyScope.Begin(_db))
        {
            added = Guid.NewGuid().ToString("N");
            scope.Execute(@"INSERT INTO Label (Id, SessionId, CustomerId, Platform, Username, MessageText, Price, AddedAt)
                            VALUES (@added, @Session, @Customer, 'instagram', '@alici', 'akış', 10, 1)", new { added, Session, Customer });
            scope.Commit();
        }
        _labels.GetById(added).Should().NotBeNull("komut ve akış eklemesi kilit altında");

        SetAuthority(Session, "local");
        InsertLabel();
    }

    [Fact]
    public void Mod_1de_kargo_bagi_ve_dosya_yazimi_duser_kilit_altinda_gecer_canli_birimler_yazar()
    {
        var id = SyncedLabel();
        _shipments.Insert(NewShipment("f1"));
        SetMode(1);

        var attach = () => _shipments.AttachLabel("f1", id);
        attach.Should().Throw<SqliteException>().WithMessage("*sales-shipment-server-owned*");
        var insert = () => _shipments.Insert(NewShipment("f2"));
        insert.Should().Throw<SqliteException>().WithMessage("*sales-shipment-server-owned*");
        foreach (var (column, value) in new[] { ("Status", "'Held'"), ("HeldAt", "600"), ("ShippedAt", "600"), ("CumulativeAmount", "10") })
        {
            var update = () => Exec($"UPDATE Shipment SET {column} = {value} WHERE Id = 'f1'");
            update.Should().Throw<SqliteException>().WithMessage("*sales-shipment-server-owned*", column);
        }

        // Canlı bilgisayarın birim yazımları mod 1'de de yerel (Authority 'local').
        _labels.MarkPrintedBatch([(id, 1)], 700, "b-canli");
        _labels.UpdatePrice(id, 70m);

        using (var scope = SyncApplyScope.Begin(_db))
        {
            scope.Execute("UPDATE Label SET ShipmentId = 'f1' WHERE Id = @id", new { id });
            scope.Execute(@"INSERT INTO Shipment (Id, CustomerId, Status, CreatedAt, CumulativeAmount)
                            VALUES ('f2', @Customer, 'Pending', 1, 0)", new { Customer });
            scope.Execute("UPDATE Shipment SET Status = 'Held', HeldAt = 600, ShippedAt = NULL, CumulativeAmount = 70 WHERE Id = 'f1'");
            scope.Commit();
        }
        _labels.GetById(id)!.ShipmentId.Should().Be("f1");
        _shipments.GetById("f2").Should().NotBeNull();
        _shipments.GetById("f1")!.Status.Should().Be(ShipmentStatus.Held);
    }

    public static TheoryData<string, string> SessionAuthorityColumns => new()
    {
        { "EndedAt", "900" }, { "Title", "'yeni'" }, { "Platforms", "'[]'" }, { "Notes", "'not'" },
    };

    [Theory]
    [MemberData(nameof(SessionAuthorityColumns))]
    public void Sunucu_otoritesindeki_oturum_yerel_yoldan_degismez(string column, string value)
    {
        _sessions.End(Session, 800);
        SetAuthority(Session, "server");

        var act = () => Exec($"UPDATE StreamSession SET {column} = {value} WHERE Id = @Session", new { Session });

        act.Should().Throw<SqliteException>().WithMessage("*sales-server-authority*");
        Exec("UPDATE StreamSession SET Authority = 'local' WHERE Id = @Session", new { Session });
        act.Should().NotThrow("otorite yerelse yazılır");
    }

    // ── 8: yalnız yerleşik SQL ─────────────────────────────────────────

    [Fact]
    public void Goc_049un_hicbir_sema_nesnesi_uygulama_fonksiyonu_cagirmaz()
    {
        using var c = _db.Open();
        var objects = c.Query<(string Name, string Sql)>(
            "SELECT name, sql FROM sqlite_master WHERE sql IS NOT NULL").ToList();
        var calls = objects
            .SelectMany(o => Regex.Matches(o.Sql, @"\b(od_[a-z_]+)\s*\(", RegexOptions.IgnoreCase)
                .Select(m => (o.Name, Function: m.Groups[1].Value.ToLowerInvariant())))
            .ToList();

        var tables049 = new[] { "RemoteLiveSession", "SalesSyncState", "PendingPaymentRecord", "PendingSalesCommand",
                                "PendingSalesCommandLabel", "SalesFeedFailure", "SalesConflictLog" };
        objects.Select(o => o.Name).Should().Contain(Triggers049).And.Contain(tables049);
        calls.Where(x => Triggers049.Contains(x.Name) || tables049.Contains(x.Name) || x.Name.StartsWith("IX_PendingSales", StringComparison.Ordinal))
            .Should().BeEmpty("049 yalnız yerleşik SQL kullanır: eski ikili (≤0.9.10) fonksiyonu kaydetmeseydi bu nesnelere dokunan her yazım düşerdi");
        calls.Select(x => x.Function).Distinct().Should().BeSubsetOf(new[] { "od_search_key", "od_phone_key" },
            "şemanın tamamında yalnız 035'in arama tetikleyicileri — v0.9.10'un da kaydettiği iki fonksiyon");
        objects.Where(o => Triggers049.Contains(o.Name))
            .Should().OnlyContain(o => o.Sql.Contains("NOT EXISTS (SELECT 1 FROM SyncApplyGuard)", StringComparison.Ordinal),
                "her tetikleyici akış uygulamasında susar (§3.2)");
    }

    // ── yardımcılar ─────────────────────────────────────────────────────

    private void Exec(string sql, object? param = null)
    {
        using var c = _db.Open();
        c.Execute(sql, param);
    }

    private void SetMode(int mode) => Exec("UPDATE SalesSyncState SET Mode = @mode WHERE Id = 1", new { mode });

    private void SetAuthority(string session, string authority)
        => Exec("UPDATE StreamSession SET Authority = @authority WHERE Id = @session", new { session, authority });

    private void InsertCustomer(string id, string username)
        => new CustomerRepository(_db).Insert(new Customer(id, "instagram", username, null, null, 100, 100,
            false, null, null, 0, 0m, BlacklistedAt: null, Address: null, Phone: null));

    private static ChatMessage Msg(string user) => new(
        Guid.NewGuid().ToString("N"), "instagram", null, user, user, null, $"{user} aldım", 1000, Array.Empty<string>());

    private static Shipment NewShipment(string id) =>
        new(id, Customer, ShipmentStatus.Pending, 500, HeldAt: null, ShippedAt: null, CumulativeAmount: 0m);

    private string InsertLabel(bool tentative = false, bool printed = false, bool cancelled = false)
    {
        var id = Guid.NewGuid().ToString("N");
        _labels.Insert(new Label(id, Session, Customer, "instagram", "@alici", "A1 aldım", "A1", 50m, 200,
            PrintedAt: printed ? 300 : null, IsTentativeBackup: tentative));
        // Insert iptal kolonlarını yazmaz (etiket iptalsiz doğar).
        if (cancelled) _labels.MarkCancelled([id], 310, CancelReasonCodes.Customer);
        return id;
    }

    /// <summary>Gönderilmiş (SyncedAt ve StockSyncedAt dolu) bir etiket.</summary>
    private string SyncedLabel(bool tentative = false, bool printed = false, bool cancelled = false)
    {
        var id = InsertLabel(tentative, printed, cancelled);
        _labels.MarkSynced(id, 400, _labels.GetById(id)!.Revision);
        State(id).SyncedAt.Should().Be(400, "ön koşul: etiket gönderilmiş");
        return id;
    }

    private (long? SyncedAt, long? StockSyncedAt, long Revision) State(string id)
    {
        using var c = _db.Open();
        return c.QuerySingle<(long?, long?, long)>("SELECT SyncedAt, StockSyncedAt, Revision FROM Label WHERE Id = @id", new { id });
    }

    private string Row(string id)
    {
        using var c = _db.Open();
        var row = (IDictionary<string, object?>)c.QuerySingle("SELECT * FROM Label WHERE Id = @id", new { id });
        return string.Join("|", row.Select(kv => $"{kv.Key}={kv.Value}"));
    }
}
