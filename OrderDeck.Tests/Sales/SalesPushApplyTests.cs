using System;
using System.Collections.Generic;
using System.Linq;
using Dapper;
using FluentAssertions;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Sales;
using OrderDeck.Core.Sessions;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.Tests.Sales;

/// <summary>
/// v2 <c>orders/sync</c> yanıtının <c>ignored</c> ve <c>rejected</c> satırlarının yerel uygulaması
/// (<see cref="SalesPushApply"/>; spec 2026-10-13 §5.2, §4.2, §7.4, §6.1, DM8; W1-08): yok sayılan birimler — reddedilende
/// bütün birimler — <c>current</c>'tan kilit altında yazılır, Y5 dosya bloğu aynı işlemde dosya tutarını düzeltir,
/// müşteri toplamı türetilir, satır sürümüyle onaylanır (Revision değişmez) ve tek iz kaydı düşer. Gönderimden
/// sonra değişen satıra hiçbir şey yazılmaz (F05).
/// </summary>
public sealed class SalesPushApplyTests : IDisposable
{
    private const long Now = 1_760_005_000L;
    private const string CustomerId = "c1";

    private readonly InMemorySqlite _db = new();
    private readonly LabelRepository _labels;
    private readonly ShipmentRepository _shipments;
    private readonly CustomerRepository _customers;
    private readonly SalesPushApply _apply;
    private readonly string _sessionId = Guid.NewGuid().ToString("N");
    private long _addedAt = 1_760_000_100L;

    public SalesPushApplyTests()
    {
        new MigrationRunner(_db).Run();
        _labels = new LabelRepository(_db);
        _shipments = new ShipmentRepository(_db);
        _customers = new CustomerRepository(_db);
        _customers.Insert(new Customer(CustomerId, "tiktok", "ornek_alici", "Örnek Alıcı", null, 100, 100,
            false, null, null, 0, 0m, BlacklistedAt: null, Address: null, Phone: null));
        new SessionRepository(_db).Insert(
            new StreamSession(_sessionId, "Örnek yayın", 1_760_000_000L, 1_760_000_900L, new[] { "tiktok" }, null));
        _apply = new SalesPushApply(_db);
    }

    public void Dispose() => _db.Dispose();

    // ── kurulum ────────────────────────────────────────────────────────────

    private static string NewId() => Guid.NewGuid().ToString("N");

    /// <summary>Basılmış etiket; dosya verilirse ona bağlı. Gönderim kuyruğunda (SyncedAt NULL) doğar.</summary>
    private string AddLabel(decimal price = 100m, string? shipmentId = null, long? printedAt = 1_760_000_200L,
        string? batch = null, long? seq = null)
    {
        var id = NewId();
        _labels.Insert(new Label(id, _sessionId, CustomerId, "tiktok", "ornek_alici", "alıyorum", "A1", price,
            _addedAt++, printedAt, DisplayName: "Örnek Alıcı"));
        Sql("UPDATE Label SET ShipmentId = @shipmentId, PrintBatchId = @batch, PrintSeq = @seq WHERE Id = @id",
            new { id, shipmentId, batch, seq });
        return id;
    }

    /// <summary>Gönderilmiş sayılan (SyncedAt dolu) dosya: kuyruğa alma gözle görülsün.</summary>
    private Shipment AddShipment(decimal amount, ShipmentStatus status = ShipmentStatus.Pending)
    {
        var id = NewId();
        _shipments.Insert(new Shipment(id, CustomerId, status, 1000, null,
            status == ShipmentStatus.Shipped ? 1500 : null, amount));
        _shipments.MarkSynced(id, 2000, _shipments.GetById(id)!.Revision);
        return _shipments.GetById(id)!;
    }

    /// <summary>Özellikli kayıt: Dapper REAL kolonu decimal özelliğe çevirir (kurucu eşlemesi tip birebir ister).</summary>
    private sealed record LabelRow
    {
        public decimal Price { get; init; }
        public long? PriceCorrectedAt { get; init; }
        public decimal? PaidPrice { get; init; }
        public long? CancelledAt { get; init; }
        public string? CancelReason { get; init; }
        public long IsTentativeBackup { get; init; }
        public string? ShipmentId { get; init; }
        public long? PrintedAt { get; init; }
        public string? PrintBatchId { get; init; }
        public long? PrintSeq { get; init; }
        public long? SyncedAt { get; init; }
        public long? StockSyncedAt { get; init; }
        public long? ServerVersion { get; init; }
        public long Revision { get; init; }
        public string CustomerId { get; init; } = "";
        public string MessageText { get; init; } = "";
        public string? DisplayName { get; init; }
        public string? Code { get; init; }
    }

    private LabelRow Row(string id)
    {
        using var c = _db.Open();
        return c.QuerySingle<LabelRow>(
            @"SELECT Price, PriceCorrectedAt, PaidPrice, CancelledAt, CancelReason, IsTentativeBackup, ShipmentId,
                     PrintedAt, PrintBatchId, PrintSeq, SyncedAt, StockSyncedAt, ServerVersion, Revision, CustomerId,
                     MessageText, DisplayName, Code
              FROM Label WHERE Id = @id", new { id });
    }

    private sealed record ConflictRow(long At, string? LabelId, string Kind, string? Reason);

    private List<ConflictRow> Conflicts()
    {
        using var c = _db.Open();
        return c.Query<ConflictRow>("SELECT At, LabelId, Kind, Reason FROM SalesConflictLog ORDER BY Id").ToList();
    }

    private void Sql(string sql, object? args = null)
    {
        using var c = _db.Open();
        c.Execute(sql, args);
    }

    private Customer Customer() => _customers.GetById(CustomerId)!;

    /// <summary>Sunucunun satırı: yerelin birim alanlarının aynısı, üzerine <paramref name="change"/>.</summary>
    private LabelServerState ServerState(string id, int version = 5, Func<LabelServerState, LabelServerState>? change = null)
    {
        var r = Row(id);
        var state = new LabelServerState(version, r.Price, r.PriceCorrectedAt, r.PaidPrice, r.CancelledAt,
            r.CancelReason, r.IsTentativeBackup != 0, r.ShipmentId, r.PrintedAt, r.PrintBatchId, r.PrintSeq);
        return change is null ? state : change(state);
    }

    private LabelPushVerdict Verdict(string id, LabelPushVerdictKind kind, string reason, string[] units,
        LabelServerState? current, long? revision = null)
        => new(id, revision ?? Row(id).Revision, kind, units, reason, current);

    private LabelPushVerdict IgnoredPrice90(string id) => Verdict(id, LabelPushVerdictKind.Ignored, "unit-stamped",
        [SalesPushUnits.Price], ServerState(id, change: s => s with
        {
            Price = 90m, PriceCorrectedAt = 1_760_004_000L, PaidPrice = 100m,
        }));

    // ── 1. ignored fiyat: dosya, toplam, onay, iz ─────────────────────────

    [Fact]
    public void Ignored_fiyat_birimi_current_tan_yazilir_dosya_farki_toplamlar_onay_ve_tek_iz_ayni_islemde()
    {
        var f = AddShipment(300m);
        var id = AddLabel(100m, f.Id);
        Sql("UPDATE Customer SET TotalLabelsPrinted = 7, TotalAmount = 999 WHERE Id = @CustomerId", new { CustomerId });
        var revision = Row(id).Revision;

        var result = _apply.Apply(IgnoredPrice90(id), Now);

        result.Status.Should().Be(LabelPushApplyStatus.Applied);
        result.ChangedUnits.Should().Equal(SalesPushUnits.Price);
        result.PrintDiscard.Should().BeFalse();
        var row = Row(id);
        row.Price.Should().Be(90m);
        row.PriceCorrectedAt.Should().Be(1_760_004_000L);
        row.PaidPrice.Should().Be(100m);
        row.SyncedAt.Should().Be(Now);
        row.StockSyncedAt.Should().Be(Now);
        row.ServerVersion.Should().Be(5, "ServerVersion = current.Version");
        row.Revision.Should().Be(revision, "uygulama kuyruğa almaz (DM8)");
        _labels.HasUnsynced().Should().BeFalse("onaylanan satır yeniden gönderilmez");

        var file = _shipments.GetById(f.Id)!;
        file.CumulativeAmount.Should().Be(290m, "Y5 bloğu: dosya etiketin fiyat farkı kadar düşer");
        file.SyncedAt.Should().BeNull("mod Off'ta dosyanın tek yazıcısı bu kopya: değişen tutar gönderilmeli");
        file.Revision.Should().Be(f.Revision + 1);
        result.Shipments.Applied.Should().ContainKey(f.Id);

        Customer().TotalAmount.Should().Be(90m, "toplam etiketlerden türetilir (§6.1)");
        Customer().TotalLabelsPrinted.Should().Be(1);

        var log = Conflicts().Should().ContainSingle().Subject;
        log.Kind.Should().Be(SalesConflictLog.PushIgnored);
        log.LabelId.Should().Be(id);
        log.At.Should().Be(Now);
        log.Reason.Should().Be("unit-stamped; units=price");
    }

    [Fact]
    public void Ignored_yalniz_yok_sayilan_birimleri_yazar_oteki_birimler_yerel_kalir()
    {
        var id = AddLabel(100m, batch: "00000000000000010000000000000001", seq: 2);
        var current = ServerState(id, change: s => s with
        {
            Price = 90m, CancelledAt = 1_760_004_000L, CancelReason = "sunucuda", PrintBatchId = null, PrintSeq = null,
        });

        _apply.Apply(Verdict(id, LabelPushVerdictKind.Ignored, "unit-stamped", [SalesPushUnits.Cancel], current), Now);

        var row = Row(id);
        row.CancelledAt.Should().Be(1_760_004_000L);
        row.CancelReason.Should().Be("sunucuda");
        row.Price.Should().Be(100m, "fiyat yok sayılmadı: sunucu push'un değerini yazdı, yerel değer doğru");
        row.PrintBatchId.Should().Be("00000000000000010000000000000001");
        row.PrintSeq.Should().Be(2);
    }

    [Fact]
    public void Ignored_iptal_birimi_iptali_geri_alirsa_fiyat_dosyaya_geri_eklenir()
    {
        // Yerel iptal Y5 ile dosyadan düşmüştü (F = 200); sunucu iptali tutmadı (komutla geri alınmış).
        var f = AddShipment(200m);
        var id = AddLabel(100m, f.Id);
        Sql("UPDATE Label SET CancelledAt = 1760003000, CancelReason = 'vazgeçti' WHERE Id = @id", new { id });
        var current = ServerState(id, change: s => s with { CancelledAt = null, CancelReason = null });

        _apply.Apply(Verdict(id, LabelPushVerdictKind.Ignored, "unit-stamped", [SalesPushUnits.Cancel], current), Now);

        Row(id).CancelledAt.Should().BeNull();
        _shipments.GetById(f.Id)!.CumulativeAmount.Should().Be(300m);
        Customer().TotalAmount.Should().Be(100m);
    }

    // ── 2–3. kargo bağı: eski dosyadan düş, yeniye ekle; yerelde olmayan ve kargolanmış ──

    [Fact]
    public void Ignored_kargo_bagi_tasindiysa_eski_dosyadan_dusulur_yeniye_eklenir()
    {
        var f1 = AddShipment(300m);
        var f2 = AddShipment(50m);
        var id = AddLabel(100m, f1.Id);

        var result = _apply.Apply(Verdict(id, LabelPushVerdictKind.Ignored, "unit-stamped",
            [SalesPushUnits.Shipment], ServerState(id, change: s => s with { ShipmentId = f2.Id })), Now);

        result.ChangedUnits.Should().Equal(SalesPushUnits.Shipment);
        Row(id).ShipmentId.Should().Be(f2.Id);
        _shipments.GetById(f1.Id)!.CumulativeAmount.Should().Be(200m);
        _shipments.GetById(f2.Id)!.CumulativeAmount.Should().Be(150m);
        _shipments.GetById(f1.Id)!.SyncedAt.Should().BeNull();
        _shipments.GetById(f2.Id)!.SyncedAt.Should().BeNull();
    }

    [Fact]
    public void Kargo_bagi_ayni_dosyanin_baska_yazimiysa_bag_degismez_dosyaya_dokunulmaz()
    {
        var f = AddShipment(300m);
        var id = AddLabel(100m, f.Id);
        var dashed = Guid.ParseExact(f.Id, "N").ToString("D");

        var result = _apply.Apply(Verdict(id, LabelPushVerdictKind.Ignored, "unit-stamped",
            [SalesPushUnits.Shipment], ServerState(id, change: s => s with { ShipmentId = dashed })), Now);

        result.ChangedUnits.Should().BeEmpty();
        Row(id).ShipmentId.Should().Be(f.Id, "aynı GUID: yerel yazım korunur");
        _shipments.GetById(f.Id)!.Should().Be(f);
    }

    [Fact]
    public void Yeni_dosya_yerelde_yoksa_atlanir_istisna_yok_eski_dosyadan_yine_dusulur()
    {
        var f1 = AddShipment(300m);
        var id = AddLabel(100m, f1.Id);
        var elsewhere = NewId();

        var act = () => _apply.Apply(Verdict(id, LabelPushVerdictKind.Ignored, "unit-stamped",
            [SalesPushUnits.Shipment], ServerState(id, change: s => s with { ShipmentId = elsewhere })), Now);

        var result = act.Should().NotThrow().Subject;
        result.Status.Should().Be(LabelPushApplyStatus.Applied);
        result.Shipments.Missing.Should().Be(1);
        Row(id).ShipmentId.Should().Be(elsewhere, "bağ sunucunun değerini alır; tutar bloğu yalnız yereldeki dosyaya yazar");
        _shipments.GetById(f1.Id)!.CumulativeAmount.Should().Be(200m);
    }

    [Fact]
    public void Kargolanmis_dosyanin_tutarina_dokunulmaz()
    {
        var shipped = AddShipment(300m, ShipmentStatus.Shipped);
        var id = AddLabel(100m, shipped.Id);

        var result = _apply.Apply(IgnoredPrice90(id), Now);

        result.Status.Should().Be(LabelPushApplyStatus.Applied);
        result.Shipments.Shipped.Should().Equal(shipped.Id);
        Row(id).Price.Should().Be(90m);
        _shipments.GetById(shipped.Id)!.Should().Be(shipped, "kargolanmış dosyanın tutarı donmuştur (§7.2)");
    }

    [Fact]
    public void Gecici_yedegin_fiyati_dosyada_hic_yoktur()
    {
        var f = AddShipment(300m);
        var id = AddLabel(100m, f.Id);
        Sql("UPDATE Label SET IsTentativeBackup = 1 WHERE Id = @id", new { id });

        _apply.Apply(IgnoredPrice90(id), Now);

        Row(id).Price.Should().Be(90m);
        _shipments.GetById(f.Id)!.Should().Be(f);
    }

    // ── 4. uçuşta değişen satır ────────────────────────────────────────────

    [Fact]
    public void Gonderimden_sonra_degisen_satira_hicbir_sey_yazilmaz()
    {
        var f = AddShipment(300m);
        var id = AddLabel(100m, f.Id);
        var verdict = IgnoredPrice90(id);
        _labels.MarkCancelled(new[] { id }, 1_760_004_500L, "uçuşta");             // Revision + 1
        var before = Row(id);
        var fileBefore = _shipments.GetById(f.Id)!;

        var result = _apply.Apply(verdict, Now);

        result.Status.Should().Be(LabelPushApplyStatus.Stale);
        Row(id).Should().Be(before, "yeni değişiklik sonraki turda gider (F05)");
        _shipments.GetById(f.Id)!.Should().Be(fileBefore);
        Conflicts().Should().BeEmpty();
    }

    [Fact]
    public void Etiket_yerelde_yoksa_hicbir_sey_yazilmaz()
    {
        var result = _apply.Apply(new LabelPushVerdict(NewId(), 0, LabelPushVerdictKind.Ignored,
            [SalesPushUnits.Price], "unit-stamped", null), Now);

        result.Status.Should().Be(LabelPushApplyStatus.Missing);
        Conflicts().Should().BeEmpty();
    }

    // ── 5. baskı cancelled-elsewhere ───────────────────────────────────────

    [Fact]
    public void Baski_cancelled_elsewhere_ile_yok_sayildiysa_baski_birimi_currenti_izler_kagit_imha_isareti_doner()
    {
        var f = AddShipment(300m);
        var id = AddLabel(100m, f.Id, printedAt: 1_760_000_200L, batch: "00000000000000020000000000000002", seq: 1);
        var current = ServerState(id, change: s => s with
        {
            PrintedAt = null, PrintBatchId = null, PrintSeq = null,
            CancelledAt = 1_760_003_000L, CancelReason = "başka bilgisayarda",
        });

        var result = _apply.Apply(Verdict(id, LabelPushVerdictKind.Ignored, "cancelled-elsewhere",
            [SalesPushUnits.Cancel, SalesPushUnits.Print], current), Now);

        result.PrintDiscard.Should().BeTrue();
        result.ChangedUnits.Should().BeEquivalentTo(new[] { SalesPushUnits.Cancel, SalesPushUnits.Print });
        var row = Row(id);
        row.PrintedAt.Should().BeNull();
        row.PrintBatchId.Should().BeNull();
        row.PrintSeq.Should().BeNull();
        row.CancelledAt.Should().Be(1_760_003_000L);
        _shipments.GetById(f.Id)!.CumulativeAmount.Should().Be(200m, "iptal edilen etiket dosya tutarından düşer");
        Customer().TotalAmount.Should().Be(0m);
        Conflicts().Single().Reason.Should().Be("cancelled-elsewhere; units=cancel,print");
    }

    [Fact]
    public void Reddedilen_satirda_da_basilmamis_current_baskiyi_kagit_imha_sayar()
    {
        // Satırın nedeni ret nedenidir (session-released); baskının cancelled-elsewhere'i yalnız birimden ve
        // current'tan anlaşılır (K3: sunucuda PrintedAt boşken yok sayılan baskının tek nedeni odur).
        var id = AddLabel(100m, printedAt: 1_760_000_200L);
        var current = ServerState(id, change: s => s with
        {
            PrintedAt = null, CancelledAt = 1_760_003_000L, CancelReason = "başka bilgisayarda",
        });

        var result = _apply.Apply(Verdict(id, LabelPushVerdictKind.Rejected, "session-released",
            [SalesPushUnits.Price, SalesPushUnits.Print], current), Now);

        result.PrintDiscard.Should().BeTrue();
    }

    [Fact]
    public void Zaten_basilmis_sunucu_baskisi_kagit_imha_sayilmaz()
    {
        var id = AddLabel(100m, printedAt: 1_760_000_200L);
        var current = ServerState(id, change: s => s with { PrintedAt = 1_760_000_100L });

        var result = _apply.Apply(Verdict(id, LabelPushVerdictKind.Ignored, "already-printed",
            [SalesPushUnits.Print], current), Now);

        result.PrintDiscard.Should().BeFalse();
        Row(id).PrintedAt.Should().Be(1_760_000_100L, "baskı birimi sunucunun değerini alır");
    }

    // ── 6–7. rejected ──────────────────────────────────────────────────────

    [Fact]
    public void Rejected_butun_birimleri_currenttan_yazar_listelenmeyen_birimler_dahil()
    {
        var f1 = AddShipment(300m);
        var f2 = AddShipment(50m);
        var id = AddLabel(100m, f1.Id, batch: "00000000000000030000000000000003", seq: 4);
        Sql("UPDATE Label SET IsTentativeBackup = 1, CancelledAt = 1760003000, CancelReason = 'yerel' WHERE Id = @id",
            new { id });
        var current = new LabelServerState(9, 120m, 1_760_004_000L, 100m, null, null, false, f2.Id,
            1_760_000_150L, "00000000000000040000000000000004", 7);

        var result = _apply.Apply(Verdict(id, LabelPushVerdictKind.Rejected, "session-released",
            [SalesPushUnits.Price], current), Now);

        result.Status.Should().Be(LabelPushApplyStatus.Applied);
        result.ChangedUnits.Should().BeEquivalentTo(SalesPushUnits.All);
        var row = Row(id);
        row.Price.Should().Be(120m);
        row.PriceCorrectedAt.Should().Be(1_760_004_000L);
        row.PaidPrice.Should().Be(100m);
        row.CancelledAt.Should().BeNull();
        row.CancelReason.Should().BeNull();
        row.IsTentativeBackup.Should().Be(0);
        row.ShipmentId.Should().Be(f2.Id);
        row.PrintedAt.Should().Be(1_760_000_150L);
        row.PrintBatchId.Should().Be("00000000000000040000000000000004");
        row.PrintSeq.Should().Be(7);
        row.ServerVersion.Should().Be(9);
        row.SyncedAt.Should().Be(Now);
        // Önce payı yoktu (iptal + geçici yedek); sonra yeni dosyada 120.
        _shipments.GetById(f1.Id)!.CumulativeAmount.Should().Be(300m);
        _shipments.GetById(f2.Id)!.CumulativeAmount.Should().Be(170m);
        Customer().TotalAmount.Should().Be(120m);
        var log = Conflicts().Single();
        log.Kind.Should().Be(SalesConflictLog.PushRejected);
        log.Reason.Should().Be("session-released; units=price");
    }

    [Fact]
    public void Currentsiz_reddedilen_ekleme_son_durumdur_iz_ve_onay_birimler_degismez()
    {
        var f = AddShipment(300m);
        var id = AddLabel(100m, f.Id);
        var before = Row(id);

        var result = _apply.Apply(new LabelPushVerdict(id, before.Revision, LabelPushVerdictKind.Rejected,
            Array.Empty<string>(), "session-owned-elsewhere", null), Now);

        result.Status.Should().Be(LabelPushApplyStatus.Terminal);
        var row = Row(id);
        row.Should().Be(before with { SyncedAt = Now, StockSyncedAt = Now }, "birimlere ve sürüme dokunulmaz");
        _labels.HasUnsynced().Should().BeFalse("son durum: yeniden gönderilmez");
        _shipments.GetById(f.Id)!.Should().Be(f);
        var log = Conflicts().Single();
        log.Kind.Should().Be(SalesConflictLog.PushRejected);
        log.Reason.Should().Be("session-owned-elsewhere; units=");
    }

    // ── 8. mod 1 ───────────────────────────────────────────────────────────

    [Fact]
    public void Mod_1de_dosya_blogu_atlanir_etiket_yine_yazilir()
    {
        var f1 = AddShipment(300m);
        var f2 = AddShipment(50m);
        var id = AddLabel(100m, f1.Id);
        Sql("UPDATE SalesSyncState SET Mode = 1 WHERE Id = 1");
        var current = ServerState(id, change: s => s with { Price = 90m, ShipmentId = f2.Id });

        var result = _apply.Apply(Verdict(id, LabelPushVerdictKind.Ignored, "shipment-server-owned",
            [SalesPushUnits.Price, SalesPushUnits.Shipment], current), Now);

        result.Shipments.ServerOwned.Should().BeTrue();
        Row(id).Price.Should().Be(90m);
        Row(id).ShipmentId.Should().Be(f2.Id, "kilit altında: TR_Label_ShipmentLink susar");
        _shipments.GetById(f1.Id)!.Should().Be(f1, "mod On'da dosya tutarını sunucu türetir (K7)");
        _shipments.GetById(f2.Id)!.Should().Be(f2);
    }

    [Fact]
    public void Sunucu_otoritesindeki_oturumun_etiketine_kilit_altinda_yazilir()
    {
        var id = AddLabel(100m);
        Sql("UPDATE StreamSession SET Authority = 'server' WHERE Id = @_sessionId", new { _sessionId });

        var result = _apply.Apply(IgnoredPrice90(id), Now);

        result.Status.Should().Be(LabelPushApplyStatus.Applied);
        Row(id).Price.Should().Be(90m);
    }

    // ── 9. idempotent ──────────────────────────────────────────────────────

    [Fact]
    public void Ayni_yanit_iki_kez_uygulanirsa_toplam_ve_tutar_bir_kez_degisir()
    {
        var f = AddShipment(300m);
        var id = AddLabel(100m, f.Id);
        var verdict = IgnoredPrice90(id);

        _apply.Apply(verdict, Now).Status.Should().Be(LabelPushApplyStatus.Applied);
        var second = _apply.Apply(verdict, Now + 10);

        second.Status.Should().Be(LabelPushApplyStatus.AlreadyAcknowledged);
        _shipments.GetById(f.Id)!.CumulativeAmount.Should().Be(290m);
        Customer().TotalAmount.Should().Be(90m);
        Conflicts().Should().ContainSingle();
        Row(id).SyncedAt.Should().Be(Now);
    }

    [Fact]
    public void Fark_okunan_haldan_hesaplanir_ikinci_uygulama_sifir_fark_uretir()
    {
        // Onaylanmış satır aynı Revision'la yeniden kuyruğa girse (ör. onay kaybı) ve aynı yanıt yeniden gelse
        // de dosya ve toplam ikinci kez oynamaz: fark sabit bir sayaç değil, satırın okunan hâli ile current'ın
        // farkıdır.
        var f = AddShipment(300m);
        var id = AddLabel(100m, f.Id);
        var verdict = IgnoredPrice90(id);
        _apply.Apply(verdict, Now);
        Sql("UPDATE Label SET SyncedAt = NULL WHERE Id = @id", new { id });
        var fileAfterFirst = _shipments.GetById(f.Id)!;

        var again = _apply.Apply(verdict, Now + 10);

        again.Status.Should().Be(LabelPushApplyStatus.Applied);
        again.ChangedUnits.Should().BeEmpty();
        _shipments.GetById(f.Id)!.Should().Be(fileAfterFirst, "fark 0: dosyaya yazılmaz, kuyruğa alınmaz");
        Customer().TotalAmount.Should().Be(90m);
    }

    // ── oluşum alanları ve kişisel veri ────────────────────────────────────

    [Fact]
    public void Olusum_alanlarina_ve_CustomerIdye_dokunulmaz_iz_kisisel_veri_tasimaz()
    {
        var id = AddLabel(100m);
        var before = Row(id);

        _apply.Apply(Verdict(id, LabelPushVerdictKind.Rejected, "session-released", [SalesPushUnits.Price],
            ServerState(id, change: s => s with { Price = 90m })), Now);

        var row = Row(id);
        row.CustomerId.Should().Be(before.CustomerId);
        row.MessageText.Should().Be(before.MessageText);
        row.DisplayName.Should().Be(before.DisplayName);
        row.Code.Should().Be(before.Code);
        var reason = Conflicts().Single().Reason!;
        reason.Should().NotContain("Örnek").And.NotContain("ornek_alici").And.NotContain("alıyorum");
    }
}
