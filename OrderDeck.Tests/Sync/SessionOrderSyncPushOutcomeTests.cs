using Dapper;
using FluentAssertions;
using OrderDeck.App.Services.Sync;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Sales;
using OrderDeck.Core.Sessions;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Core.Time;
using OrderDeck.Licensing.Api;
using OrderDeck.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.Tests.Sync;

/// <summary>
/// v2 <c>orders/sync</c> yanıtının işlenmesi uçtan uca (spec 2026-10-13 §5.2, §4.2, §7.4, §6.1, §9; W1-08):
/// <c>ignored</c> ve <c>rejected</c> satırlarda sunucunun değeri (<c>current</c>) uygulanır, mod Off'ta Y5 dosya
/// bloğu dosya tutarını düzeltir, müşteri toplamı türetilir, satır sürümüyle onaylanır; tur
/// <see cref="SessionOrderSyncService.PushOutcome"/> ile sayıları, reddedilen kimlikleri ve imha edilecek kâğıtları
/// bildirir. Sunucu <see cref="FakeSalesServer"/> (<see cref="FakeSalesServer.OrderVerdict"/>).
/// </summary>
public sealed class SessionOrderSyncPushOutcomeTests : IDisposable
{
    private sealed class License(string key) : ICurrentLicenseProvider
    {
        public string? CurrentLicenseKey { get; set; } = key;
    }

    private sealed class Clock : IClock
    {
        public long Now = 1_760_005_000L;
        public long UnixNow() => Now;
    }

    // Kişisel veri denetimi (§9): bu metinlerin hiçbiri günlüğe düşmemeli.
    private const string DisplayName = "Örnek Alıcı";
    private const string Username = "ornek_alici";
    private const string Message = "bunu alıyorum lütfen";

    private readonly FakeSalesServer _server = new();
    private readonly InMemorySqlite _db = new();
    private readonly License _license;
    private readonly Clock _clock = new();
    private readonly LabelRepository _labels;
    private readonly ShipmentRepository _shipments;
    private readonly CustomerRepository _customers;
    private readonly CapturingLogger<SessionOrderSyncService> _log = new();
    private readonly SessionOrderSyncService _svc;
    private readonly List<SalesPushOutcome> _outcomes = new();
    private readonly string _customerId = Guid.NewGuid().ToString("N");
    private readonly string _sessionId = Guid.NewGuid().ToString("N");
    private long _addedAt = 1_760_000_100L;

    public SessionOrderSyncPushOutcomeTests()
    {
        new MigrationRunner(_db).Run();
        // Y5'in tabanı açılışta türetilmiş say (ShipmentAmountDerivation): damga boşken dosya bloğu yazmaz.
        Sql("UPDATE SalesSyncState SET ShipmentsDerivedAt = 900 WHERE Id = 1");
        var sessions = new SessionRepository(_db);
        _labels = new LabelRepository(_db);
        _shipments = new ShipmentRepository(_db);
        _customers = new CustomerRepository(_db);
        _license = new License(_server.Lisans);
        _customers.Insert(new Customer(_customerId, "tiktok", Username, DisplayName,
            null, 1, 1, false, null, null, 0, 0m, null, null, null));
        sessions.Insert(new StreamSession(_sessionId, "Örnek yayın", 1_760_000_000L, null, new[] { "tiktok" }, null));
        _svc = new SessionOrderSyncService(
            new LicenseApiClient(
                new HttpClient(_server, disposeHandler: false) { BaseAddress = new Uri("https://test.local") },
                new LicenseTokenStore()),
            sessions, _labels, _license, TestSalesEnvelope.For(_db, _license), new HandoverGate(),
            new SalesPushApply(_db), _clock, _log);
        _svc.PushOutcome += (_, outcome) => { lock (_outcomes) _outcomes.Add(outcome); };
    }

    public void Dispose()
    {
        _db.Dispose();
        _server.Dispose();
    }

    // ── kurulum ────────────────────────────────────────────────────────────

    private string AddLabel(decimal price = 100m, string? shipmentId = null, long? printedAt = 1_760_000_200L,
        string? batch = null, long? seq = null)
    {
        var id = Guid.NewGuid().ToString("N");
        _labels.Insert(new Label(id, _sessionId, _customerId, "tiktok", Username, $"{Message} {id[..6]}", "A1", price,
            _addedAt++, printedAt, DisplayName: DisplayName));
        Sql("UPDATE Label SET ShipmentId = @shipmentId, PrintBatchId = @batch, PrintSeq = @seq WHERE Id = @id",
            new { id, shipmentId, batch, seq });
        return id;
    }

    private Shipment AddShipment(decimal amount, ShipmentStatus status = ShipmentStatus.Pending)
    {
        var id = Guid.NewGuid().ToString("N");
        _shipments.Insert(new Shipment(id, _customerId, status, 1000, null,
            status == ShipmentStatus.Shipped ? 1500 : null, amount));
        _shipments.MarkSynced(id, 2000, _shipments.GetById(id)!.Revision);
        return _shipments.GetById(id)!;
    }

    private sealed record LabelRow
    {
        public decimal Price { get; init; }
        public long? CancelledAt { get; init; }
        public string? CancelReason { get; init; }
        public string? ShipmentId { get; init; }
        public long? PrintedAt { get; init; }
        public string? PrintBatchId { get; init; }
        public long? PrintSeq { get; init; }
        public long? SyncedAt { get; init; }
        public long? ServerVersion { get; init; }
        public long Revision { get; init; }
        public string CustomerId { get; init; } = "";
        public string SessionId { get; init; } = "";
        public string? DisplayName { get; init; }
        public string MessageText { get; init; } = "";
        public string? Code { get; init; }
    }

    private LabelRow Row(string id)
    {
        using var c = _db.Open();
        return c.QuerySingle<LabelRow>(
            @"SELECT Price, CancelledAt, CancelReason, ShipmentId, PrintedAt, PrintBatchId, PrintSeq, SyncedAt,
                     ServerVersion, Revision, CustomerId, SessionId, DisplayName, MessageText, Code
              FROM Label WHERE Id = @id", new { id });
    }

    private sealed record ConflictRow(string? LabelId, string Kind, string? Reason);

    private List<ConflictRow> Conflicts()
    {
        using var c = _db.Open();
        return c.Query<ConflictRow>("SELECT LabelId, Kind, Reason FROM SalesConflictLog ORDER BY Id").ToList();
    }

    private void Sql(string sql, object? args = null)
    {
        using var c = _db.Open();
        c.Execute(sql, args);
    }

    private void Verdict(string id, FakeSalesServer.OrderVerdict verdict) => _server.OrderVerdicts[Guid.Parse(id)] = verdict;

    private static DateTimeOffset At(long unix) => DateTimeOffset.FromUnixTimeSeconds(unix);

    // ── 1. ignored fiyat ───────────────────────────────────────────────────

    [Fact]
    public async Task Ignored_fiyat_birimi_current_tan_yazilir_dosya_toplam_onay_ve_tek_iz()
    {
        var f = AddShipment(300m);
        var id = AddLabel(100m, f.Id);
        Sql("UPDATE Customer SET TotalLabelsPrinted = 7, TotalAmount = 999 WHERE Id = @id", new { id = _customerId });
        var revision = Row(id).Revision;
        Verdict(id, new("ignored", "unit-stamped", ["price"],
            Current: c => c with { Price = 90m, PriceCorrectedAt = At(1_760_004_000L), PaidPrice = 100m }));

        await _svc.SyncOnceAsync();

        _server.Faults.Should().BeEmpty();
        var row = Row(id);
        row.Price.Should().Be(90m);
        row.SyncedAt.Should().Be(_clock.Now);
        row.Revision.Should().Be(revision, "DM8: uygulama kuyruğa almaz");
        row.ServerVersion.Should().Be(_server.Order(Guid.Parse(id))!.Version, "ServerVersion = current.Version");
        _labels.HasUnsynced().Should().BeFalse();

        var file = _shipments.GetById(f.Id)!;
        file.CumulativeAmount.Should().Be(290m);
        file.SyncedAt.Should().BeNull("dosya değişikliği shipments/sync ile gitmeli");
        file.Revision.Should().Be(f.Revision + 1);

        var customer = _customers.GetById(_customerId)!;
        customer.TotalAmount.Should().Be(90m, "toplam etiketlerden türetilir (§6.1)");
        customer.TotalLabelsPrinted.Should().Be(1);

        var log = Conflicts().Should().ContainSingle().Subject;
        log.Should().Be(new ConflictRow(id, SalesConflictLog.PushIgnored, "unit-stamped; units=price"));

        var outcome = _outcomes.Should().ContainSingle().Subject;
        outcome.Ignored.Should().Be(1);
        outcome.Rejected.Should().BeEmpty();
        outcome.PrintDiscardLabelIds.Should().BeEmpty();
        outcome.NeedsAttention.Should().BeFalse("yok sayılan fiyat operatörden eylem istemez");
    }

    // ── 2–3. kargo bağı ve dosya durumları ─────────────────────────────────

    [Fact]
    public async Task Ignored_kargo_bagi_tasindiysa_eski_dosyadan_dusulur_yeniye_eklenir()
    {
        var f1 = AddShipment(300m);
        var f2 = AddShipment(50m);
        var id = AddLabel(100m, f1.Id);
        Verdict(id, new("ignored", "unit-stamped", ["shipment"],
            Current: c => c with { ShipmentId = Guid.ParseExact(f2.Id, "N") }));

        await _svc.SyncOnceAsync();

        Row(id).ShipmentId.Should().Be(f2.Id);
        _shipments.GetById(f1.Id)!.CumulativeAmount.Should().Be(200m);
        _shipments.GetById(f2.Id)!.CumulativeAmount.Should().Be(150m);
    }

    [Fact]
    public async Task Yerelde_olmayan_dosya_atlanir_istisna_yok_kargolanmis_dosyaya_dokunulmaz()
    {
        var f1 = AddShipment(300m);
        var shipped = AddShipment(400m, ShipmentStatus.Shipped);
        var moving = AddLabel(100m, f1.Id);
        var frozen = AddLabel(200m, shipped.Id);
        var elsewhere = Guid.NewGuid();
        Verdict(moving, new("ignored", "unit-stamped", ["shipment"], Current: c => c with { ShipmentId = elsewhere }));
        Verdict(frozen, new("ignored", "unit-stamped", ["price"], Current: c => c with { Price = 150m }));

        var act = () => _svc.SyncOnceAsync();

        await act.Should().NotThrowAsync();
        Row(moving).ShipmentId.Should().Be(elsewhere.ToString("N"));
        Row(moving).SyncedAt.Should().NotBeNull();
        Row(frozen).Price.Should().Be(150m);
        Row(frozen).SyncedAt.Should().NotBeNull();
        _shipments.GetById(f1.Id)!.CumulativeAmount.Should().Be(200m);
        _shipments.GetById(shipped.Id)!.Should().Be(shipped, "kargolanmış dosyanın tutarı donmuştur");
    }

    // ── 4. uçuşta değişen satır ────────────────────────────────────────────

    [Fact]
    public async Task Ucusta_degisen_satira_hicbir_sey_uygulanmaz_yeni_degisiklik_sonraki_turda_gider()
    {
        var f = AddShipment(300m);
        var id = AddLabel(100m, f.Id);
        Verdict(id, new("ignored", "unit-stamped", ["price"], Current: c => c with { Price = 90m }));
        var edited = false;
        _server.DuringPush = endpoint =>
        {
            if (endpoint != FakeSalesServer.OrdersSync || edited) return;
            edited = true;
            _labels.MarkCancelled(new[] { id }, 1_760_004_500L, "uçuşta");
        };

        await _svc.SyncOnceAsync();

        var afterFirst = Row(id);
        afterFirst.Price.Should().Be(100m, "uçuştaki düzenlemenin üstüne sunucunun değeri yazılmaz (F05)");
        afterFirst.SyncedAt.Should().BeNull();
        _shipments.GetById(f.Id)!.Should().Be(f);
        Conflicts().Should().BeEmpty();
        _outcomes.Should().BeEmpty("yalnız uygulanan satırlar bildirilir");

        _server.OrderVerdicts.Clear();
        await _svc.SyncOnceAsync();

        var calls = _server.Calls(FakeSalesServer.OrdersSync);
        calls.Should().HaveCount(2);
        calls[1].Orders.Orders!.Single().CancelledAt.Should().Be(At(1_760_004_500L), "yeni değişiklik gider");
        Row(id).SyncedAt.Should().NotBeNull();
    }

    // ── 5. baskı cancelled-elsewhere ───────────────────────────────────────

    [Fact]
    public async Task Baski_cancelled_elsewhere_baski_birimi_currenti_izler_outcome_kagit_kimligini_tasir()
    {
        var id = AddLabel(100m, printedAt: 1_760_000_200L, batch: "00000000000000050000000000000005", seq: 3);
        Verdict(id, new("ignored", "cancelled-elsewhere", ["cancel", "print"], Current: c => c with
        {
            PrintedAt = null, PrintBatchId = null, PrintSeq = null,
            CancelledAt = At(1_760_003_000L), CancelReason = "başka bilgisayarda", CancelSetAt = At(1_760_003_000L),
        }));

        await _svc.SyncOnceAsync();

        var row = Row(id);
        row.PrintedAt.Should().BeNull();
        row.PrintBatchId.Should().BeNull();
        row.PrintSeq.Should().BeNull();
        row.CancelledAt.Should().Be(1_760_003_000L);
        var outcome = _outcomes.Should().ContainSingle().Subject;
        outcome.PrintDiscardLabelIds.Should().Equal(id);
        outcome.Ignored.Should().Be(1);
        outcome.NeedsAttention.Should().BeTrue();
    }

    // ── 6–7. rejected ──────────────────────────────────────────────────────

    [Fact]
    public async Task Rejected_butun_birimler_currenti_izler_olusum_alanlari_ve_CustomerId_yazilmaz()
    {
        var id = AddLabel(100m);
        _labels.MarkCancelled(new[] { id }, 1_760_003_500L, "yerel");
        var before = Row(id);
        Verdict(id, new("rejected", "session-released", ["cancel"], Current: c => c with
        {
            CancelledAt = null, CancelReason = null, Price = 120m,
            CustomerId = Guid.NewGuid().ToString("N"), DisplayName = "Başka Biri", MessageText = "başka metin",
            Code = "Z9",
        }));

        await _svc.SyncOnceAsync();

        var row = Row(id);
        row.CancelledAt.Should().BeNull();
        row.Price.Should().Be(120m, "reddedilen push'ta listelenmeyen birim de current'tan yazılır");
        row.SyncedAt.Should().Be(_clock.Now);
        row.CustomerId.Should().Be(before.CustomerId);
        row.SessionId.Should().Be(before.SessionId);
        row.DisplayName.Should().Be(before.DisplayName);
        row.MessageText.Should().Be(before.MessageText);
        row.Code.Should().Be(before.Code);
        Conflicts().Single().Kind.Should().Be(SalesConflictLog.PushRejected);
        var outcome = _outcomes.Should().ContainSingle().Subject;
        outcome.Rejected.Should().Equal(new RejectedLabelPush(id, "session-released"));
        outcome.NeedsAttention.Should().BeTrue();
    }

    [Fact]
    public async Task Currentsiz_reddedilen_ekleme_son_durumdur_yeniden_gonderilmez_uyari_tasir()
    {
        var id = AddLabel();
        Verdict(id, new("rejected", "session-owned-elsewhere", [], WithoutCurrent: true));

        await _svc.SyncOnceAsync();

        Row(id).SyncedAt.Should().Be(_clock.Now);
        Row(id).ServerVersion.Should().BeNull("sunucuda satır yok");
        _labels.HasUnsynced().Should().BeFalse();
        _outcomes.Should().ContainSingle().Which.Rejected
            .Should().Equal(new RejectedLabelPush(id, "session-owned-elsewhere"));
        Conflicts().Single().Should().Be(new ConflictRow(id, SalesConflictLog.PushRejected,
            "session-owned-elsewhere; units="));

        await _svc.SyncOnceAsync();
        _server.Calls(FakeSalesServer.OrdersSync).Should().ContainSingle("son durum: yeniden gönderilmez");
    }

    // ── 8. mod 1 ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Mod_1de_dosya_blogu_atlanir()
    {
        var f = AddShipment(300m);
        var id = AddLabel(100m, f.Id);
        Sql("UPDATE SalesSyncState SET Mode = 1 WHERE Id = 1");
        Verdict(id, new("ignored", "unit-stamped", ["price"], Current: c => c with { Price = 90m }));

        await _svc.SyncOnceAsync();

        _server.Faults.Should().BeEmpty();
        Row(id).Price.Should().Be(90m);
        _shipments.GetById(f.Id)!.Should().Be(f, "mod On'da dosyayı sunucu türetir (K7, §5.1-a)");
    }

    // ── 9. idempotent ──────────────────────────────────────────────────────

    [Fact]
    public async Task Ayni_yanit_yeniden_gelirse_toplam_ve_dosya_tutari_bir_kez_degisir()
    {
        var f = AddShipment(300m);
        var id = AddLabel(100m, f.Id);
        Verdict(id, new("ignored", "unit-stamped", ["price"], Current: c => c with { Price = 90m }));
        await _svc.SyncOnceAsync();
        var fileAfterFirst = _shipments.GetById(f.Id)!;
        fileAfterFirst.CumulativeAmount.Should().Be(290m);

        // Aynı satır aynı Revision'la yeniden kuyruğa girer (onay kaybı); sunucu aynı yanıtı yeniden verir.
        Sql("UPDATE Label SET SyncedAt = NULL WHERE Id = @id", new { id });
        await _svc.SyncOnceAsync();

        _server.Calls(FakeSalesServer.OrdersSync).Should().HaveCount(2);
        _shipments.GetById(f.Id)!.Should().Be(fileAfterFirst, "fark okunan hâlden: ikinci uygulama sıfır fark");
        _customers.GetById(_customerId)!.TotalAmount.Should().Be(90m);
        Row(id).Price.Should().Be(90m);
    }

    // ── 10. günlük ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Gunlukte_kisisel_veri_yok_yalniz_sayilar()
    {
        var f = AddShipment(300m);
        var ignored = AddLabel(100m, f.Id);
        var rejected = AddLabel(150m);
        var terminal = AddLabel(175m);
        var discard = AddLabel(200m, printedAt: 1_760_000_300L);
        Verdict(ignored, new("ignored", "unit-stamped", ["price"], Current: c => c with { Price = 90m }));
        Verdict(rejected, new("rejected", "shipment-shipped", ["price"], Current: c => c with { Price = 140m }));
        Verdict(terminal, new("rejected", "session-owned-elsewhere", [], WithoutCurrent: true));
        Verdict(discard, new("ignored", "cancelled-elsewhere", ["print"], Current: c => c with
        {
            PrintedAt = null, CancelledAt = At(1_760_003_000L), CancelSetAt = At(1_760_003_000L),
        }));

        await _svc.SyncOnceAsync();

        _outcomes.Should().ContainSingle();
        _log.Entries.Should().NotBeEmpty();
        foreach (var entry in _log.Entries)
        {
            entry.Full.Should().NotContain(DisplayName).And.NotContain(Username).And.NotContain(Message);
        }
        _log.Warnings.Should().ContainSingle(w => w.Message.Contains("2 etiket yok sayıldı")
                                                  && w.Message.Contains("2 etiket reddedildi"));
        foreach (var c in Conflicts())
            c.Reason.Should().NotContain(DisplayName).And.NotContain(Username).And.NotContain(Message);
    }

    // ── olay ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Olay_isleyicisi_hata_atsa_da_tur_biter_satirlar_uygulanmis_kalir()
    {
        var id = AddLabel(100m);
        Verdict(id, new("rejected", "session-released", ["price"], Current: c => c with { Price = 120m }));
        _svc.PushOutcome += (_, _) => throw new InvalidOperationException("arayüz hatası");

        var act = () => _svc.SyncOnceAsync();

        await act.Should().NotThrowAsync();
        Row(id).Price.Should().Be(120m);
        Row(id).SyncedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Hepsi_uygulanan_turda_olay_yok()
    {
        AddLabel();

        await _svc.SyncOnceAsync();

        _outcomes.Should().BeEmpty();
        Conflicts().Should().BeEmpty();
    }
}
