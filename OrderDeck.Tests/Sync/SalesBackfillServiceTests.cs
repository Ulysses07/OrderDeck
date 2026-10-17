using System.Text.Json;
using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OrderDeck.App.Services.Sync;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Sales;
using OrderDeck.Core.Sessions;
using OrderDeck.Core.Settings;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Core.Time;
using OrderDeck.Licensing;
using OrderDeck.Licensing.Api;
using OrderDeck.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.Tests.Sync;

/// <summary>
/// Ws'nin ilk açılışındaki geri doldurma (spec 2026-10-13 §8.2 adım 2–5, §4.3; W1-09): hello'yu bekler, kuyrukları
/// boşaltır, oturum/etiket/dosya bağlarını 200'lük partilerle ve turda en çok 10 istekle gönderir, onaylı satırların
/// <c>SyncedAt</c>/<c>Revision</c>'ına dokunmaz, kaçmış kapanışı devreder, F05 öncesi kayıp güncellemeyi (yerel
/// kazanır) bir kez yeniden kuyruğa alır ve push uygulanınca <c>backfillCompleted</c> gönderir; sunucu kazanırsa yalnız
/// iz; <c>foreign-license</c> son durum; 400 <c>unknown-session</c> imleci ilerletmez; kesilen tur kaldığı yerden
/// sürer. Sunucu <see cref="FakeSalesServer"/>.
/// </summary>
public sealed class SalesBackfillServiceTests : IDisposable
{
    private const long T0 = 1_755_000_000L;

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private sealed class License(string key) : ICurrentLicenseProvider
    {
        public string? CurrentLicenseKey { get; set; } = key;
    }

    private sealed class Hardware(string id) : IHardwareIdProvider
    {
        public string GetHardwareId() => id;
        public string? GetLegacyHardwareId() => null;
    }

    private sealed class Clock : IClock
    {
        public long UnixNow() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    }

    /// <summary>Bir bilgisayarın yığını (bir süreç): zarf, hello turu, gönderim, kargo ve geri doldurma. Aynı
    /// veritabanıyla yeni bir örnek "yeniden başlatma"dır (hello'nun başarısı bellekte, yeniden gerekir).</summary>
    private sealed class Pc
    {
        public License License { get; }
        public SalesSyncEnvelope Envelope { get; }
        public SalesSyncStateService State { get; }
        public SessionOrderSyncService Push { get; }
        public ShipmentSyncService Shipments { get; }
        public SalesBackfillService Backfill { get; }
        public CapturingLogger<SalesBackfillService> Log { get; } = new();
        public HandoverGate Gate { get; } = new();

        public Pc(FakeSalesServer server, IDbConnectionFactory db, string settingsPath, string fingerprint, string licenseKey)
        {
            License = new License(licenseKey);
            var identity = new ReplicaIdentityRepository(db);
            var state = new SalesSyncStateRepository(db);
            Envelope = new SalesSyncEnvelope(identity, state, new Hardware(fingerprint), License,
                NullLogger<SalesSyncEnvelope>.Instance);
            var api = new LicenseApiClient(
                new HttpClient(server, disposeHandler: false) { BaseAddress = new Uri("https://test.local") },
                new LicenseTokenStore());
            var clock = new Clock();
            State = new SalesSyncStateService(api, Envelope, identity, state, License, () => new AppSettings(),
                NullLogger<SalesSyncStateService>.Instance);
            Push = new SessionOrderSyncService(api, new SessionRepository(db), new LabelRepository(db), License,
                Envelope, Gate, new SalesPushApply(db), clock, NullLogger<SessionOrderSyncService>.Instance);
            var store = new SettingsStore(settingsPath);
            Shipments = new ShipmentSyncService(api, new ShipmentRepository(db), store, store.Load(), License, Envelope,
                clock, NullLogger<ShipmentSyncService>.Instance);
            Backfill = new SalesBackfillService(api, Envelope, State, Push, Shipments, new SalesBackfillRepository(db),
                new SyncCursorRepository(db), Gate, License, clock, Log);
        }

        public async Task HelloAsync() =>
            (await State.TourOnceAsync(CancellationToken.None)).Should().Be(SalesStateTourOutcome.Ok);

        public Task<SalesBackfillTourOutcome> TourAsync(CancellationToken ct = default) => Backfill.TourOnceAsync(ct);
    }

    private readonly FakeSalesServer _server = new();
    private readonly InMemorySqlite _db = new();
    private readonly string _settingsPath = Path.Combine(Path.GetTempPath(), $"od-backfill-{Guid.NewGuid():N}.json");
    private readonly string _fingerprint = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
    private readonly string _customerId = Guid.NewGuid().ToString("N");
    private readonly SessionRepository _sessions;
    private readonly LabelRepository _labels;
    private readonly SyncCursorRepository _cursors;
    private Pc? _current;
    private long _addedAt = T0 + 100;

    /// <summary>Veritabanı sahadaki sürümün şemasında (48) doğar; test yerel geçmişi orada kurar. Ws ilk
    /// dokunuşta açılır: göç 049 koşar (kopya kimliği tohumlanır) ve bilgisayarın yığını kurulur.</summary>
    public SalesBackfillServiceTests()
    {
        new MigrationRunner(_db, EmbeddedMigrationScripts.UpTo(48)).Run();
        _sessions = new SessionRepository(_db);
        _labels = new LabelRepository(_db);
        _cursors = new SyncCursorRepository(_db);
        new CustomerRepository(_db).Insert(new Customer(_customerId, "tiktok", "ornek_alici", "Örnek Alıcı",
            null, 1, 1, false, null, null, 0, 0m, null, null, null));
    }

    /// <summary>Ws: ilk dokunuşta 048 veritabanını 049'a yükseltir.</summary>
    private Pc Ws
    {
        get
        {
            if (_current is null)
            {
                new MigrationRunner(_db).Run();
                _current = NewPc();
            }
            return _current;
        }
    }

    /// <summary>Uygulamanın yeniden başlatılması: aynı veritabanı, yeni süreç (hello yeniden gerekir).</summary>
    private void Restart()
    {
        _ = Ws;
        _current = NewPc();
    }

    public void Dispose()
    {
        _db.Dispose();
        _server.Dispose();
        SqliteConnection.ClearAllPools();
        try { File.Delete(_settingsPath); } catch { /* en iyi çaba */ }
    }

    private Pc NewPc(string? licenseKey = null) => new(_server, _db, _settingsPath, _fingerprint, licenseKey ?? _server.Lisans);

    private Guid ReplicaId => new ReplicaIdentityRepository(_db).GetReplicaId()!.Value;

    // ── kurulum: 048 dönemi veritabanı ─────────────────────────────────────

    private static DateTimeOffset At(long unix) => DateTimeOffset.FromUnixTimeSeconds(unix);

    /// <summary>Yerel oturum; varsayılan olarak çoktan gönderilmiş (SyncedAt dolu).</summary>
    private string AddSession(long startedAt, long? endedAt, bool synced = true)
    {
        var id = Guid.NewGuid().ToString("N");
        _sessions.Insert(new StreamSession(id, "Örnek yayın", startedAt, endedAt, new[] { "tiktok" }, null,
            SyncedAt: synced ? startedAt + 10 : null));
        return id;
    }

    /// <summary>Yerel etiketler, tek işlemde ham INSERT (güncelleme tetikleyicisi çalışmaz): varsayılan olarak
    /// çoktan gönderilmiş (SyncedAt ve StockSyncedAt dolu).</summary>
    private List<string> AddLabels(string sessionId, int count, bool synced = true, decimal price = 100m,
        long? printedAt = null, long? cancelledAt = null, string? shipmentId = null)
    {
        var ids = new List<string>(count);
        using var c = _db.Open();
        using var tx = c.BeginTransaction();
        for (var i = 0; i < count; i++)
        {
            var id = Guid.NewGuid().ToString("N");
            var added = _addedAt++;
            c.Execute(
                @"INSERT INTO Label (Id, SessionId, CustomerId, Platform, Username, DisplayName, MessageText, Code, Price,
                                     AddedAt, PrintedAt, CancelledAt, CancelReason, ShipmentId, SyncedAt, StockSyncedAt)
                  VALUES (@id, @sessionId, @customerId, 'tiktok', 'ornek_alici', 'Örnek Alıcı', 'alıyorum', 'A1', @price,
                          @added, @printedAt, @cancelledAt, @reason, @shipmentId, @syncedAt, @syncedAt)",
                new
                {
                    id, sessionId, customerId = _customerId, price, added, printedAt, cancelledAt,
                    reason = cancelledAt is null ? null : "müşteri vazgeçti", shipmentId,
                    syncedAt = synced ? added + 5 : (long?)null,
                }, tx);
            ids.Add(id);
        }
        tx.Commit();
        return ids;
    }

    private string AddLabel(string sessionId, bool synced = true, decimal price = 100m, long? printedAt = null,
        long? cancelledAt = null, string? shipmentId = null)
        => AddLabels(sessionId, 1, synced, price, printedAt, cancelledAt, shipmentId)[0];

    /// <summary>Gönderilmiş kargo dosyası; ham INSERT (048 şemasında da geçerli, kaynak kopya boş).</summary>
    private string AddShipment(long createdAt)
    {
        var id = Guid.NewGuid().ToString("N");
        Exec(@"INSERT INTO Shipment (Id, CustomerId, Status, CreatedAt, HeldAt, ShippedAt, CumulativeAmount, SyncedAt)
               VALUES (@id, @customerId, 'Held', @createdAt, @heldAt, NULL, 300, @syncedAt)",
            new { id, customerId = _customerId, createdAt, heldAt = createdAt + 5, syncedAt = createdAt + 6 });
        return id;
    }

    /// <summary>Yerel etiketin v1 döneminde sunucuya gitmiş kopyası: v2 alanları (bağ, 048, ebeveyn) yok.</summary>
    private FakeSalesServer.OrderBody ServerCopyOf(string labelId)
    {
        _ = Ws;
        var row = new SalesBackfillRepository(_db).GetLabelsAfter(0, int.MaxValue).Single(r => r.Label.Id == labelId).Label;
        var item = SessionOrderSyncService.ToSyncOrderItem(row) with
        {
            BaseVersion = null, ParentOrderId = null, PrintBatchId = null, PrintSeq = null, PriceCorrectedAt = null,
            PaidPrice = null, ShipmentId = null,
        };
        return JsonSerializer.Deserialize<FakeSalesServer.OrderBody>(JsonSerializer.Serialize(item, Web), Web)!;
    }

    private void SeedServerCopies(IEnumerable<string> labelIds)
    {
        foreach (var id in labelIds) _server.SeedOrder(ServerCopyOf(id), version: 3);
    }

    private void SeedServerSession(string id, long startedAt, long? endedAt, long? releasedAt = null) =>
        _server.SeedSession(Guid.Parse(id), At(startedAt), endedAt is { } e ? At(e) : null,
            releasedAt: releasedAt is { } r ? At(r) : null, title: "Örnek yayın");

    private sealed record Snapshot(string Id, long? SyncedAt, long Revision);

    private IReadOnlyList<Snapshot> SnapshotLabels()
    {
        using var c = _db.Open();
        return c.Query<Snapshot>("SELECT Id, SyncedAt, Revision FROM Label ORDER BY Id").ToList();
    }

    private IReadOnlyList<Snapshot> SnapshotSessions()
    {
        using var c = _db.Open();
        return c.Query<Snapshot>("SELECT Id, SyncedAt, Revision FROM StreamSession ORDER BY Id").ToList();
    }

    private T Scalar<T>(string sql, object? args = null)
    {
        using var c = _db.Open();
        return c.ExecuteScalar<T>(sql, args)!;
    }

    private void Exec(string sql, object? args = null)
    {
        using var c = _db.Open();
        c.Execute(sql, args);
    }

    private SyncCursor? Cursor(string? licenseKey = null) => _cursors.Get(SalesBackfillCursor.Name, licenseKey ?? _server.Lisans);

    private IReadOnlyList<FakeSalesServer.HelloCall> CompletionHellos =>
        _server.Hellos.Where(h => h.Body.BackfillCompleted == true).ToList();

    private static HashSet<string> N(IEnumerable<Guid> ids) => ids.Select(g => g.ToString("N")).ToHashSet();

    // ── 1. 048 dönemi ilk açılış ───────────────────────────────────────────

    [Fact]
    public async Task Ilk_acilis_her_oturum_etiket_ve_bag_200lu_partilerle_gider_onayli_satirlar_degismez_kacmis_kapanis_devredilir()
    {
        var closed = AddSession(T0, T0 + 3600);
        var missedClose = AddSession(T0 + 86_400, T0 + 90_000);           // yerelde bitti, sunucuda açık kaldı
        var other = AddSession(T0 + 172_800, T0 + 180_000);
        var shipmentA = AddShipment(T0 + 200_000);
        var shipmentB = AddShipment(T0 + 200_100);
        var closedLabels = AddLabels(closed, 250);
        var missedLabels = AddLabels(missedClose, 150, printedAt: T0 + 89_000);
        var linkedA = AddLabels(other, 30, printedAt: T0 + 179_000, shipmentId: shipmentA);
        var linkedB = AddLabels(other, 20, printedAt: T0 + 179_500, shipmentId: shipmentB);
        var onlyLocal = AddLabels(other, 5);                              // sunucuya hiç varmamış (kayıp)
        var allLabels = closedLabels.Concat(missedLabels).Concat(linkedA).Concat(linkedB).Concat(onlyLocal).ToList();
        Scalar<long>("SELECT SchemaVersion FROM _meta WHERE Id = 1").Should().Be(48, "geçmiş v0.9.10'un veritabanında");

        SeedServerSession(closed, T0, T0 + 3600, releasedAt: T0 + 3700);
        SeedServerSession(missedClose, T0 + 86_400, endedAt: null);
        SeedServerSession(other, T0 + 172_800, T0 + 180_000, releasedAt: T0 + 180_100);
        SeedServerCopies(closedLabels.Concat(missedLabels).Concat(linkedA).Concat(linkedB));
        _server.SeedShipment(new FakeSalesServer.ShipmentBody(Guid.Parse(shipmentA), _customerId, "held", 300m,
            At(T0 + 200_000), At(T0 + 200_005), null));
        _server.SeedShipment(new FakeSalesServer.ShipmentBody(Guid.Parse(shipmentB), _customerId, "held", 300m,
            At(T0 + 200_100), At(T0 + 200_105), null));
        var labelsBefore = SnapshotLabels();
        var sessionsBefore = SnapshotSessions();

        await Ws.HelloAsync();
        (await Ws.TourAsync()).Should().Be(SalesBackfillTourOutcome.Completed);

        _server.Faults.Should().BeEmpty();
        var calls = _server.BackfillCalls.Select(c => c.Backfill).ToList();
        calls.Should().OnlyContain(b => (b.Sessions ?? new()).Count <= 200 && (b.Orders ?? new()).Count <= 200
            && (b.Shipments ?? new()).Count <= 200);
        calls.Select(b => b.Orders?.Count ?? 0).Where(n => n > 0).Should().Equal(200, 200, 55);
        calls.Should().OnlyContain(b => b.ReplicaId == ReplicaId && b.Protocol == 2 && b.SeenMode == 0 && b.CatalogAware);
        N(calls.SelectMany(b => b.Sessions ?? new()).Select(s => s.Id))
            .Should().BeEquivalentTo(new[] { closed, missedClose, other });
        N(calls.SelectMany(b => b.Orders ?? new()).Select(o => o.Id)).Should().BeEquivalentTo(allLabels);
        var links = calls.SelectMany(b => b.Shipments ?? new()).ToDictionary(l => l.Id.ToString("N"), l => N(l.LabelIds!));
        links.Should().HaveCount(2);
        links[shipmentA].Should().BeEquivalentTo(linkedA);
        links[shipmentB].Should().BeEquivalentTo(linkedB);

        SnapshotLabels().Should().Equal(labelsBefore, "geri doldurma onaylı satırın SyncedAt ve Revision'ına dokunmaz");
        SnapshotSessions().Should().Equal(sessionsBefore);

        // Kaçmış kapanış: sunucuda bitti ve devredildi; yerelde yalnız ReleasedAt, Authority local.
        var server = _server.Session(Guid.Parse(missedClose))!;
        server.EndedAt.Should().Be(At(T0 + 90_000));
        server.ReleasedAt.Should().NotBeNull();
        server.OwnerReplicaId.Should().Be(ReplicaId);
        var local = _sessions.GetById(missedClose)!;
        local.ReleasedAt.Should().Be(server.ReleasedAt!.Value.ToUnixTimeSeconds());
        local.Authority.Should().Be(StreamSessionAuthority.Local);

        // Kayıp etiketler bildirimsiz eklendi; bağlar ve dosyanın kaynağı doldu.
        N(_server.BackfillInserted).Should().BeEquivalentTo(onlyLocal);
        linkedA.Should().OnlyContain(id => _server.Order(Guid.Parse(id))!.Row.ShipmentId == Guid.Parse(shipmentA));
        _server.Shipment(Guid.Parse(shipmentA))!.OriginReplicaId.Should().Be(ReplicaId);
        Scalar<long>("SELECT COUNT(*) FROM Label WHERE ServerVersion IS NULL").Should().Be(0,
            "ayrışmayan her satır sunucunun sürümünü alır");

        CompletionHellos.Should().ContainSingle();
        _server.ReplicaRow(_server.LicenseId, ReplicaId)!.BackfillCompleted.Should().BeTrue();
        var cursor = Cursor()!;
        cursor.Seq.Should().Be(SalesBackfillCursor.Completed.Encode());
        cursor.LastId.Should().Be(ReplicaId);

        // Tamamlanmış geri doldurma bir daha istek atmaz.
        var requests = _server.BackfillCalls.Count + _server.Hellos.Count;
        (await Ws.TourAsync()).Should().Be(SalesBackfillTourOutcome.AlreadyCompleted);
        (_server.BackfillCalls.Count + _server.Hellos.Count).Should().Be(requests);

        // Günlükte kişisel veri yok.
        Ws.Log.Entries.Should().NotContain(e => e.Full.Contains("ornek_alici") || e.Full.Contains("Örnek")
            || e.Full.Contains(_server.Lisans));
    }

    // ── 2. F05 öncesi kayıp iptal ve baskı: yerel kazanır ──────────────────

    [Fact]
    public async Task Kayip_iptal_ve_baski_bir_kez_kuyruga_alinir_push_uygulaninca_backfillCompleted_gider()
    {
        var session = AddSession(T0, T0 + 3600);
        var lost = AddLabel(session, printedAt: T0 + 3000, cancelledAt: T0 + 3100);
        var fine = AddLabel(session);
        SeedServerSession(session, T0, T0 + 3600, releasedAt: T0 + 3700);
        var serverRow = ServerCopyOf(lost) with { PrintedAt = null, CancelledAt = null, CancelReason = null };
        _server.SeedOrder(serverRow, version: 4);
        SeedServerCopies(new[] { fine });
        var before = _labels.GetById(lost)!;

        await Ws.HelloAsync();
        (await Ws.TourAsync()).Should().Be(SalesBackfillTourOutcome.AwaitingRepair);

        var requeued = _labels.GetById(lost)!;
        requeued.SyncedAt.Should().BeNull("yerel kazanır: olağan kuyruğa yeniden alındı");
        requeued.Revision.Should().Be(before.Revision + 1);
        Scalar<long?>("SELECT StockSyncedAt FROM Label WHERE Id = @lost", new { lost })
            .Should().BeNull("iptal ayrıştı: sunucunun defteri onu görmedi");
        Scalar<long?>("SELECT ServerVersion FROM Label WHERE Id = @lost", new { lost }).Should().BeNull(
            "ayrışan satıra sürüm yazılmaz");
        Scalar<string>("SELECT Reason FROM SalesConflictLog WHERE LabelId = @lost AND Kind = 'backfill-requeued'",
            new { lost }).Should().Be("units=cancel,print");
        CompletionHellos.Should().BeEmpty("yeniden kuyruğa alınan satır henüz applied olmadı");

        // Baştan yeniden (imleç satırı silindi): yeniden kuyruğa alınan satır imleci tutar, ikinci kez kuyruğa
        // alınmaz.
        Exec("DELETE FROM SyncCursor WHERE Name = @name", new { name = SalesBackfillCursor.Name });
        (await Ws.TourAsync()).Should().Be(SalesBackfillTourOutcome.AwaitingPush);
        _labels.GetById(lost)!.Revision.Should().Be(before.Revision + 1, "bir kez kuyruğa alınır");
        Scalar<long>("SELECT COUNT(*) FROM SalesConflictLog WHERE LabelId = @lost", new { lost }).Should().Be(1);
        CompletionHellos.Should().BeEmpty();

        // Olağan v2 push'u götürür; sunucu iptali ve baskıyı alır.
        (await Ws.Push.SyncOnceAsync()).OrdersPushed.Should().Be(1);
        var order = _server.Order(Guid.Parse(lost))!;
        order.Row.CancelledAt.Should().Be(At(T0 + 3100));
        order.Row.PrintedAt.Should().Be(At(T0 + 3000));
        CompletionHellos.Should().BeEmpty("hello bir sonraki geri doldurma turunda gider, push'tan önce asla");

        (await Ws.TourAsync()).Should().Be(SalesBackfillTourOutcome.Completed);
        CompletionHellos.Should().ContainSingle();
        _server.Faults.Should().BeEmpty();
    }

    [Fact]
    public async Task Yeniden_kuyruga_alinan_satir_hic_uygulanmazsa_tamamlanma_bekler_ve_gunluk_soyler()
    {
        var session = AddSession(T0, T0 + 3600);
        var lost = AddLabel(session, cancelledAt: T0 + 3100);
        SeedServerSession(session, T0, T0 + 3600, releasedAt: T0 + 3700);
        _server.SeedOrder(ServerCopyOf(lost) with { CancelledAt = null, CancelReason = null }, version: 2);

        await Ws.HelloAsync();
        (await Ws.TourAsync()).Should().Be(SalesBackfillTourOutcome.AwaitingRepair);

        // Sunucu push'u yok sayar (mod Off'ta olmaz; ikiz zorlar): current uygulanır, satır onaylanır, iz düşer.
        _server.OrderVerdicts[Guid.Parse(lost)] = new FakeSalesServer.OrderVerdict("ignored", "unit-stamped", ["cancel"],
            Current: item => item with { CancelledAt = null, CancelReason = null });
        await Ws.Push.SyncOnceAsync();
        _labels.GetById(lost)!.SyncedAt.Should().NotBeNull();

        (await Ws.TourAsync()).Should().Be(SalesBackfillTourOutcome.AwaitingRepair);
        (await Ws.TourAsync()).Should().Be(SalesBackfillTourOutcome.AwaitingRepair);
        CompletionHellos.Should().BeEmpty();
        Ws.Log.Warnings.Should().ContainSingle(e => e.Message.Contains("uygulanmadı"),
            "durum değişmedikçe bir kez");
    }

    // ── 3. Sunucu kazanır (geri yüklenmiş kopya) ──────────────────────────

    [Fact]
    public async Task Geri_yukleme_kimligi_cozulemeyen_surecte_geri_doldurma_istek_gondermez()
    {
        // AppHost açılışta geri yükleme işaretini uygulayamadı: sunucu kopyayı özgün sanıp localWins = true dönerdi
        // (§4.3, M20). Kapı açılışta, hello'dan önce kurulur; burada hello'dan sonra kurulması geri doldurmanın kendi
        // denetimini sınar (hello'nun kapısına güvenmez).
        var session = AddSession(T0, T0 + 3600);
        AddLabel(session, printedAt: T0 + 3000);
        await Ws.HelloAsync();
        Ws.Envelope.HoldUntilRestart();

        (await Ws.TourAsync()).Should().Be(SalesBackfillTourOutcome.WaitingForHello);

        _server.BackfillCalls.Should().BeEmpty();
        _server.Calls(FakeSalesServer.OrdersSync).Should().BeEmpty("boşaltma da gitmez");
        CompletionHellos.Should().BeEmpty();
    }

    [Fact]
    public async Task Geri_yuklenmis_kopyada_sunucu_kazanir_yalniz_iz_tamamlanma_beklemez()
    {
        var session = AddSession(T0, T0 + 3600);
        var lost = AddLabel(session, printedAt: T0 + 3000, cancelledAt: T0 + 3100);
        SeedServerSession(session, T0, T0 + 3600, releasedAt: T0 + 3700);
        _server.SeedOrder(ServerCopyOf(lost) with { PrintedAt = null, CancelledAt = null, CancelReason = null });
        new ReplicaIdentityRepository(_db).Rotate();                      // geri yükleme: önceki kimlik bekliyor
        var before = _labels.GetById(lost)!;

        await Ws.HelloAsync();
        _server.ReplicaRow(_server.LicenseId, ReplicaId)!.PreviousReplicaId.Should().NotBeNull();
        (await Ws.TourAsync()).Should().Be(SalesBackfillTourOutcome.Completed);

        var after = _labels.GetById(lost)!;
        after.SyncedAt.Should().Be(before.SyncedAt, "sunucu kazanır: kuyruğa alınmaz");
        after.Revision.Should().Be(before.Revision);
        Scalar<string>("SELECT Kind || '|' || Reason FROM SalesConflictLog WHERE LabelId = @lost", new { lost })
            .Should().Be("backfill-diverged|units=cancel,print");
        Scalar<long?>("SELECT ServerVersion FROM Label WHERE Id = @lost", new { lost }).Should().BeNull();
        _server.Order(Guid.Parse(lost))!.Row.CancelledAt.Should().BeNull("sunucu değeri ezilmedi");
        CompletionHellos.Should().ContainSingle();

        // Aynı parti yeniden: iz tekrarlanmaz.
        Exec("DELETE FROM SyncCursor WHERE Name = @name", new { name = SalesBackfillCursor.Name });
        (await Ws.TourAsync()).Should().Be(SalesBackfillTourOutcome.Completed);
        Scalar<long>("SELECT COUNT(*) FROM SalesConflictLog WHERE LabelId = @lost", new { lost }).Should().Be(1);
    }

    // ── 4. Başka lisansın Id'si ────────────────────────────────────────────

    [Fact]
    public async Task Baska_lisansin_oturum_ve_siparisi_son_durum_imlec_ilerler_sayi_gunlukte_bir_daha_gitmez()
    {
        var foreignSession = AddSession(T0, T0 + 3600);
        var foreignSessionLabels = AddLabels(foreignSession, 3);
        var mine = AddSession(T0 + 86_400, T0 + 90_000);
        var foreignOrder = AddLabel(mine);
        var ok = AddLabel(mine);
        _server.ForeignSessions[Guid.Parse(foreignSession)] = 0;
        _server.ForeignOrders[Guid.Parse(foreignOrder)] = 0;
        SeedServerSession(mine, T0 + 86_400, T0 + 90_000, releasedAt: T0 + 90_100);

        await Ws.HelloAsync();
        (await Ws.TourAsync()).Should().Be(SalesBackfillTourOutcome.Completed);

        _server.Session(Guid.Parse(foreignSession)).Should().BeNull("başka lisansın satırı yazılmaz");
        foreignSessionLabels.Append(foreignOrder).Should().OnlyContain(id => _server.Order(Guid.Parse(id)) == null);
        _server.Order(Guid.Parse(ok)).Should().NotBeNull();
        _sessions.GetById(foreignSession)!.ReleasedAt.Should().BeNull();
        Scalar<long?>("SELECT ServerVersion FROM Label WHERE Id = @foreignOrder", new { foreignOrder }).Should().BeNull();
        Ws.Log.Entries.Should().Contain(e => e.Message.Contains("1 oturum başka lisansın"));
        Ws.Log.Entries.Should().Contain(e => e.Message.Contains("4 etiket başka lisansın"));
        Cursor()!.Seq.Should().Be(SalesBackfillCursor.Completed.Encode());

        var sent = _server.BackfillCalls.Count;
        (await Ws.TourAsync()).Should().Be(SalesBackfillTourOutcome.AlreadyCompleted);
        Restart();                                                     // yeniden başlatma
        await Ws.HelloAsync();
        (await Ws.TourAsync()).Should().Be(SalesBackfillTourOutcome.AlreadyCompleted);
        _server.BackfillCalls.Count.Should().Be(sent, "son durum: yeniden gönderilmez");
    }

    // ── 5. 400 unknown-session ─────────────────────────────────────────────

    [Fact]
    public async Task Bilinmeyen_oturum_partisi_imleci_ilerletmez_sonraki_turda_yeniden_denenir()
    {
        var session = AddSession(T0, T0 + 3600);
        var labels = AddLabels(session, 3);
        SeedServerSession(session, T0, T0 + 3600, releasedAt: T0 + 3700);
        // Oturum partisi işlenirken sıradaki istek (etiket partisi) 400 unknown-session alır.
        var armed = false;
        _server.DuringPush = endpoint =>
        {
            if (endpoint != FakeSalesServer.SalesBackfill || armed) return;
            armed = true;
            _server.EnqueueProblem(FakeSalesServer.Backfill, 400, "unknown-session");
        };

        await Ws.HelloAsync();
        (await Ws.TourAsync()).Should().Be(SalesBackfillTourOutcome.Failed);

        Cursor()!.Seq.Should().Be(new SalesBackfillCursor(SalesBackfillPhase.Labels, 0).Encode(),
            "parti düştü: imleç etiket aşamasının başında kalır");
        labels.Should().OnlyContain(id => _server.Order(Guid.Parse(id)) == null);
        Ws.Log.Warnings.Should().BeEmpty("bilinmeyen oturum beklenen bir geçiş hâlidir");

        (await Ws.TourAsync()).Should().Be(SalesBackfillTourOutcome.Completed);
        var calls = _server.BackfillCalls.Select(c => c.Backfill).Where(b => (b.Orders?.Count ?? 0) > 0).ToList();
        calls.Should().HaveCount(2);
        N(calls[1].Orders!.Select(o => o.Id)).Should().BeEquivalentTo(N(calls[0].Orders!.Select(o => o.Id)),
            "aynı parti yeniden gider");
        labels.Should().OnlyContain(id => _server.Order(Guid.Parse(id)) != null);
    }

    // ── 6. Kesilme, yeniden başlatma, tekrar ve kayıp imleç ─────────────────

    [Fact]
    public async Task Kesilen_tur_imlecten_surer_tekrarlanan_parti_ve_kayip_imlec_zararsiz()
    {
        var session = AddSession(T0, T0 + 3600);
        var labels = AddLabels(session, 450);
        SeedServerSession(session, T0, T0 + 3600, releasedAt: T0 + 3700);
        SeedServerCopies(labels.Take(300));

        // İkinci etiket partisi bekletilir; belirteç iptal edilir.
        var seen = 0;
        FakeSalesServer.PushHold? hold = null;
        _server.DuringPush = endpoint =>
        {
            if (endpoint != FakeSalesServer.SalesBackfill) return;
            if (++seen == 2) hold = _server.Hold(FakeSalesServer.SalesBackfill);
        };
        await Ws.HelloAsync();
        using var cts = new CancellationTokenSource();
        var tour = Ws.TourAsync(cts.Token);
        await WaitUntil(() => hold is not null);
        await hold!.Arrived.WaitAsync(TestPatience.Upper);
        await cts.CancelAsync();
        await FluentActions.Awaiting(() => tour).Should().ThrowAsync<OperationCanceledException>();

        var firstBatchLast = Scalar<long>("SELECT rowid FROM Label WHERE Id = @id", new { id = labels[199] });
        Cursor()!.Seq.Should().Be(new SalesBackfillCursor(SalesBackfillPhase.Labels, firstBatchLast).Encode());

        // Yeniden başlatma: hello, sonra imleçten devam.
        _server.DuringPush = null;
        hold.Release();
        Restart();
        await Ws.HelloAsync();
        var before = _server.BackfillCalls.Count;
        (await Ws.TourAsync()).Should().Be(SalesBackfillTourOutcome.Completed);
        var resumed = _server.BackfillCalls.Skip(before).Select(c => c.Backfill).ToList();
        resumed[0].Sessions.Should().BeNullOrEmpty("oturum aşaması geçilmişti");
        resumed[0].Orders![0].Id.Should().Be(Guid.Parse(labels[200]), "kaldığı partiden");
        labels.Should().OnlyContain(id => _server.Order(Guid.Parse(id)) != null);

        // Tekrarlanan parti ve kayıp imleç zararsız: sunucu ve yerel satırlar değişmez.
        var serverBefore = labels.Select(id => _server.Order(Guid.Parse(id))!.Version).ToList();
        var localBefore = SnapshotLabels();
        _cursors.Upsert(SalesBackfillCursor.Name, _server.Lisans,
            seq: new SalesBackfillCursor(SalesBackfillPhase.Labels, firstBatchLast).Encode(), lastId: ReplicaId);
        (await Ws.TourAsync()).Should().Be(SalesBackfillTourOutcome.Completed);
        Exec("DELETE FROM SyncCursor WHERE Name = @name", new { name = SalesBackfillCursor.Name });
        (await Ws.TourAsync()).Should().Be(SalesBackfillTourOutcome.Completed);
        _server.BackfillCalls.Skip(before).Select(c => c.Backfill).Count(b => (b.Sessions?.Count ?? 0) > 0)
            .Should().Be(1, "kayıp imleç baştan başlar");
        labels.Select(id => _server.Order(Guid.Parse(id))!.Version).Should().Equal(serverBefore);
        SnapshotLabels().Should().Equal(localBefore);
        Scalar<long>("SELECT COUNT(*) FROM SalesConflictLog").Should().Be(0);
        _server.Faults.Should().BeEmpty();
    }

    // ── 7. Tur bütçesi ve canlı yayın ──────────────────────────────────────

    [Fact]
    public async Task Turda_en_cok_10_istek_canli_yayindaki_etiket_geri_doldurma_surerken_5_sn_icinde_gider()
    {
        var session = AddSession(T0, T0 + 3600);
        AddLabels(session, 4_000);                                        // 20 etiket partisi
        SeedServerSession(session, T0, T0 + 3600, releasedAt: T0 + 3700);
        await Ws.HelloAsync();

        (await Ws.TourAsync()).Should().Be(SalesBackfillTourOutcome.InProgress);
        var first = _server.BackfillCalls.Count;
        first.Should().Be(SalesBackfillService.MaxRequestsPerTour - 1, "lisans çözümü + dokuz parti");

        (await Ws.TourAsync()).Should().Be(SalesBackfillTourOutcome.InProgress);
        (_server.BackfillCalls.Count - first).Should().Be(SalesBackfillService.MaxRequestsPerTour,
            "lisans önbellekte: on parti");
        _server.BackfillCalls.Should().OnlyContain(c => (c.Backfill.Orders ?? new()).Count <= 200);

        // Geri doldurma isteği uçarken canlı yayında yazılan etiket olağan push'la 5 sn içinde gider.
        var live = AddSession(T0 + 500_000, null, synced: false);
        var hold = _server.Hold(FakeSalesServer.SalesBackfill);
        var tour = Ws.TourAsync();
        await hold.Arrived.WaitAsync(TestPatience.Upper);
        var label = AddLabel(live, synced: false);
        using (var cts = new CancellationTokenSource(SessionOrderSyncHostedService.LiveInterval))
            await Ws.Push.SyncOnceAsync(liveCadence: true, cts.Token);
        _server.Order(Guid.Parse(label)).Should().NotBeNull("gönderim kapısı geri doldurma boyunca tutulmaz");
        _server.BackfillInserted.Should().NotContain(Guid.Parse(label));
        hold.Release();
        (await tour).Should().Be(SalesBackfillTourOutcome.Completed);
    }

    // ── 8. Dosya bağları, ayrışan satırın sürümü, Revision karşılaştırması ──

    [Fact]
    public async Task Her_dosya_bagli_etiketleriyle_gider_ayrisan_ve_ucusta_degisen_satira_surum_yazilmaz()
    {
        var session = AddSession(T0, T0 + 3600);
        var shipmentA = AddShipment(T0 + 4000);
        var shipmentB = AddShipment(T0 + 4100);
        var shipped = AddShipment(T0 + 4200);
        Exec("UPDATE Shipment SET Status = 'Shipped', ShippedAt = @at, SyncedAt = @at WHERE Id = @shipped",
            new { at = T0 + 4300, shipped });
        var a = AddLabels(session, 3, printedAt: T0 + 3000, shipmentId: shipmentA);
        var b = AddLabels(session, 2, printedAt: T0 + 3000, shipmentId: shipmentB);
        var c = AddLabels(session, 1, printedAt: T0 + 3000, shipmentId: shipped);
        var priced = AddLabel(session, price: 150m);                      // F05 öncesi kayıp fiyat
        var edited = AddLabel(session);                                   // istek uçarken değişir
        var empty = AddShipment(T0 + 4400);                               // bağsız dosya
        SeedServerSession(session, T0, T0 + 3600, releasedAt: T0 + 3700);
        SeedServerCopies(a.Concat(b).Concat(c).Append(edited));
        _server.SeedOrder(ServerCopyOf(priced) with { Price = 100m }, version: 5);
        _server.DuringPush = endpoint =>
        {
            if (endpoint == FakeSalesServer.SalesBackfill && _server.BackfillCalls.Last().Backfill.Orders is { Count: > 0 })
                Exec("UPDATE Label SET Price = 120 WHERE Id = @edited", new { edited });
        };

        await Ws.HelloAsync();
        (await Ws.TourAsync()).Should().Be(SalesBackfillTourOutcome.AwaitingRepair);

        var links = _server.BackfillCalls.SelectMany(x => x.Backfill.Shipments ?? new())
            .ToDictionary(l => l.Id.ToString("N"), l => N(l.LabelIds!));
        links.Keys.Should().BeEquivalentTo(new[] { shipmentA, shipmentB, shipped, empty });
        links[shipmentA].Should().BeEquivalentTo(a);
        links[shipmentB].Should().BeEquivalentTo(b);
        links[shipped].Should().BeEquivalentTo(c);
        links[empty].Should().BeEmpty();

        Scalar<long?>("SELECT ServerVersion FROM Label WHERE Id = @priced", new { priced })
            .Should().BeNull("ayrışan satır");
        Scalar<long?>("SELECT ServerVersion FROM Label WHERE Id = @edited", new { edited })
            .Should().BeNull("satır okunduğundan beri değişti (Revision)");
        a.Concat(b).Concat(c).Should().OnlyContain(id =>
            Scalar<long?>("SELECT ServerVersion FROM Label WHERE Id = @id", new { id }) == _server.Order(Guid.Parse(id))!.Version);
        _labels.GetById(priced)!.SyncedAt.Should().BeNull("fiyat ayrıştı, yerel kazanır");
        Scalar<long?>("SELECT StockSyncedAt FROM Label WHERE Id = @priced", new { priced })
            .Should().NotBeNull("yalnız fiyat ayrıştı: stok defteri etkilenmez");
    }

    // ── kaçmış kapanış, gönderilmemiş etiket ───────────────────────────────

    [Fact]
    public async Task Gonderilmemis_etiketi_olan_kacmis_kapanis_gonderim_kuyruguna_verilir_kuyruk_bosalinca_devreder()
    {
        var session = AddSession(T0, T0 + 3600);
        var pending = AddLabel(session, synced: false);
        SeedServerSession(session, T0, endedAt: null);
        _server.EnqueueStatus(FakeSalesServer.OrdersSync, 503);           // boşaltmada etiket gidemez

        await Ws.HelloAsync();
        var before = _sessions.GetById(session)!;
        (await Ws.TourAsync()).Should().Be(SalesBackfillTourOutcome.AwaitingPush, "gönderilmemiş etiket imleci tutar");

        var item = _server.BackfillCalls.Select(c => c.Backfill).SelectMany(x => x.Sessions ?? new()).Single();
        item.Release.Should().BeFalse("yayının gönderilmemiş etiketi var (M2)");
        item.UnsentLabelCount.Should().Be(1);
        _server.Session(Guid.Parse(session))!.EndedAt.Should().Be(At(T0 + 3600), "kaçmış kapanış doldu");
        _server.Session(Guid.Parse(session))!.ReleasedAt.Should().BeNull();
        var queued = _sessions.GetById(session)!;
        queued.SyncedAt.Should().BeNull("devri kapanış gönderimi yapacak");
        queued.Revision.Should().Be(before.Revision);

        await Ws.Push.SyncOnceAsync();
        _server.Order(Guid.Parse(pending)).Should().NotBeNull();
        _server.Session(Guid.Parse(session))!.ReleasedAt.Should().NotBeNull("kuyruk boşalınca Release = true");
        var released = _sessions.GetById(session)!;
        released.ReleasedAt.Should().NotBeNull();
        released.Authority.Should().Be(StreamSessionAuthority.Local, "mod Off");

        (await Ws.TourAsync()).Should().Be(SalesBackfillTourOutcome.Completed, "onaylanan etiketle sürer");
    }

    [Fact]
    public async Task Release_karari_devir_kapisi_altinda_kilit_yanit_uygulanana_kadar_tutulur()
    {
        var session = AddSession(T0, T0 + 3600);
        AddLabels(session, 1);
        SeedServerSession(session, T0, endedAt: null);
        await Ws.HelloAsync();

        // Kapanış gönderimi kilidi tutarken geri doldurma sayamaz ve göndermez.
        var lease = await Ws.Gate.EnterAsync(session);
        var tour = Ws.TourAsync();
        await Task.Delay(300);
        _server.BackfillCalls.Should().BeEmpty();

        var hold = _server.Hold(FakeSalesServer.SalesBackfill);
        lease.Dispose();
        await hold.Arrived.WaitAsync(TestPatience.Upper);
        Ws.Gate.TryEnter(session, out _).Should().BeFalse("kilit sayımdan yanıtın uygulanmasına kadar tutulur");
        hold.Release();

        (await tour).Should().Be(SalesBackfillTourOutcome.Completed);
        _server.BackfillCalls[0].Backfill.Sessions!.Single().Release.Should().BeTrue("kuyruk boş");
        _sessions.GetById(session)!.ReleasedAt.Should().NotBeNull();
        Ws.Gate.EntryCount.Should().Be(0, "kilit bırakıldı");
    }

    // ── gönderilmeyi bekleyen satır geri doldurmaya girmez ─────────────────

    [Fact]
    public async Task Gonderilmeyi_bekleyen_etiket_geri_doldurmaya_girmez_olagan_push_ekler()
    {
        var session = AddSession(T0, T0 + 3600);
        var synced = AddLabel(session);
        var early = AddLabel(session, synced: false);                     // açılışta kuyrukta bekliyor
        SeedServerSession(session, T0, T0 + 3600, releasedAt: T0 + 3700);
        SeedServerCopies(new[] { synced });
        string? live = null;
        string? fresh = null;
        bool? drainedFirst = null;
        _server.DuringPush = endpoint =>
        {
            // Boşaltmadan sonra, geri doldurma sürerken canlı yayın açılır ve etiket yazılır.
            if (endpoint != FakeSalesServer.SalesBackfill || live is not null) return;
            drainedFirst = _server.Order(Guid.Parse(early)) is not null;
            live = AddSession(T0 + 500_000, null, synced: false);
            fresh = AddLabel(live, synced: false);
        };

        await Ws.HelloAsync();
        (await Ws.TourAsync()).Should().Be(SalesBackfillTourOutcome.AwaitingPush, "bekleyen canlı oturum imleci tutar");
        drainedFirst.Should().BeTrue("§8.2-2: olağan kuyruk geri doldurmadan önce boşaltılır");
        _server.BackfillInserted.Should().NotContain(Guid.Parse(early));

        var sent = _server.BackfillCalls.Select(c => c.Backfill).ToList();
        N(sent.SelectMany(x => x.Sessions ?? new()).Select(s => s.Id)).Should().NotContain(live!);
        N(sent.SelectMany(x => x.Orders ?? new()).Select(o => o.Id)).Should().NotContain(fresh!);

        await Ws.Push.SyncOnceAsync();
        _server.Order(Guid.Parse(fresh!)).Should().NotBeNull("olağan push ekler (bildirimli ekleme dalı)");
        _server.Session(Guid.Parse(live!))!.OwnerReplicaId.Should().Be(ReplicaId);

        // Onaylanınca geri doldurma onları da götürür; eklemez (zaten sunucuda).
        (await Ws.TourAsync()).Should().Be(SalesBackfillTourOutcome.Completed);
        _server.BackfillInserted.Should().NotContain(Guid.Parse(fresh!));
        _server.Session(Guid.Parse(live!))!.EndedAt.Should().BeNull("canlı oturum geri doldurmayla bitmez");
    }

    // ── boşaltma canlı yayını kovalamaz ────────────────────────────────────

    [Fact]
    public async Task Bosaltma_acilistaki_kuyrukla_sinirli_canli_yayinin_yeni_etiketlerini_kovalamaz()
    {
        var live = AddSession(T0 + 500_000, null, synced: false);
        AddLabels(live, 150, synced: false);                              // açılışta kuyrukta: iki parti
        var drainRounds = 0;
        _server.DuringPush = endpoint =>
        {
            // Geri doldurmadan önce, boşaltma sürerken canlı yayın her turda yeni etiket yazar.
            if (endpoint != FakeSalesServer.OrdersSync || _server.BackfillCalls.Count > 0) return;
            drainRounds++;
            AddLabel(live, synced: false);
        };

        await Ws.HelloAsync();
        await Ws.TourAsync();

        drainRounds.Should().Be(2, "dolu parti gittikçe yinelenir, ilk eksik partide durur: yeni etiketi ≈5 sn'lik "
            + "gönderim götürür, boşaltma onu kovalayıp hız sınırını tüketmez");
        _server.Calls(FakeSalesServer.SessionsSync).Should().ContainSingle();
    }

    // ── bekleyen satır imleci tutar (v1 döneminden sunucuda olan satır) ────

    [Fact]
    public async Task Bekleyen_eski_etiket_imleci_tutar_onaylaninca_geri_doldurulur_kaynak_kopyasi_dolar()
    {
        var session = AddSession(T0, T0 + 3600);
        var labels = AddLabels(session, 4, printedAt: T0 + 3000);
        var edited = labels[1];
        SeedServerSession(session, T0, T0 + 3600, releasedAt: T0 + 3700);
        SeedServerCopies(labels);                                         // v1 dönemi: kaynak kopya boş
        var armed = true;
        _server.DuringPush = endpoint =>
        {
            // Oturum partisi uçarken operatör eski etiketin fiyatını düzeltir: tetikleyici onu kuyruğa alır.
            if (endpoint != FakeSalesServer.SalesBackfill || !armed) return;
            armed = false;
            Exec("UPDATE Label SET Price = 150 WHERE Id = @edited", new { edited });
        };

        await Ws.HelloAsync();
        (await Ws.TourAsync()).Should().Be(SalesBackfillTourOutcome.AwaitingPush);

        N(_server.BackfillCalls.SelectMany(c => c.Backfill.Orders ?? new()).Select(o => o.Id))
            .Should().BeEquivalentTo(new[] { labels[0] }, "imleç bekleyen satırı geçmez");
        Cursor()!.Seq.Should().Be(new SalesBackfillCursor(SalesBackfillPhase.Labels,
            Scalar<long>("SELECT rowid FROM Label WHERE Id = @id", new { id = labels[0] })).Encode());
        CompletionHellos.Should().BeEmpty();
        (await Ws.TourAsync()).Should().Be(SalesBackfillTourOutcome.AwaitingPush);
        Ws.Log.Entries.Count(e => e.Message.Contains("gönderilmeyi bekliyor")).Should().Be(1,
            "bekleme değişmedikçe bir kez yazılır");

        // Olağan push satırı sunucunun güncelleme dalında yazar: kaynak kopya orada dolmaz (§4.3).
        (await Ws.Push.SyncOnceAsync()).OrdersPushed.Should().Be(1);
        _server.Order(Guid.Parse(edited))!.OriginReplicaId.Should().BeNull();

        (await Ws.TourAsync()).Should().Be(SalesBackfillTourOutcome.Completed);
        labels.Should().OnlyContain(id => _server.Order(Guid.Parse(id))!.OriginReplicaId == ReplicaId,
            "onaylanan satır kaldığı yerden geri doldurulur");
        _server.Order(Guid.Parse(edited))!.Row.Price.Should().Be(150m);
        CompletionHellos.Should().ContainSingle();
        _server.Faults.Should().BeEmpty();
    }

    [Fact]
    public async Task Bekleme_gunlugu_asama_basina_bir_kez_bilgi_bekleyen_satir_degisse_de_sonrakiler_debug()
    {
        // §8.2: "günlükte bir kez, kimlik ve sayıyla". Canlı yayınla çakışan geri doldurmada her 60 sn'lik turda yeni
        // bir bekleyen satır olur; bastırma kimliğe göre olsaydı 5 saatlik yayında ≈300 Information satırı düşerdi.
        var session = AddSession(T0, T0 + 3600);
        var labels = AddLabels(session, 4, printedAt: T0 + 3000);
        SeedServerSession(session, T0, T0 + 3600, releasedAt: T0 + 3700);
        SeedServerCopies(labels);
        var armed = true;
        _server.DuringPush = endpoint =>
        {
            if (endpoint != FakeSalesServer.SalesBackfill || !armed) return;
            armed = false;
            Exec("UPDATE Label SET Price = 150 WHERE Id = @id", new { id = labels[1] });
        };

        await Ws.HelloAsync();
        (await Ws.TourAsync()).Should().Be(SalesBackfillTourOutcome.AwaitingPush);
        (await Ws.Push.SyncOnceAsync()).OrdersPushed.Should().Be(1);
        Exec("UPDATE Label SET Price = 160 WHERE Id = @id", new { id = labels[3] });   // sonraki turda başka satır bekler
        (await Ws.TourAsync()).Should().Be(SalesBackfillTourOutcome.AwaitingPush);
        N(_server.BackfillCalls.SelectMany(c => c.Backfill.Orders ?? new()).Select(o => o.Id))
            .Should().Contain(labels[2], "ön koşul: imleç ilerledi, bekleyen satır değişti");

        var waits = Ws.Log.Entries.Where(e => e.Message.Contains("gönderilmeyi bekliyor")).ToList();
        waits.Where(e => e.Level == LogLevel.Information).Should().ContainSingle("aşama başına bir kez");
        waits.Where(e => e.Level == LogLevel.Debug).Should().ContainSingle();
    }

    [Fact]
    public async Task Bekleyen_eski_oturum_imleci_tutar_onaylaninca_geri_doldurulur_sahibi_yazilir()
    {
        var first = AddSession(T0, T0 + 3600);
        var pending = AddSession(T0 + 86_400, T0 + 90_000, synced: false);
        var last = AddSession(T0 + 172_800, T0 + 180_000);
        SeedServerSession(first, T0, T0 + 3600, releasedAt: T0 + 3700);
        SeedServerSession(pending, T0 + 86_400, T0 + 90_000, releasedAt: T0 + 90_100);   // v1 dönemi: sahipsiz
        SeedServerSession(last, T0 + 172_800, T0 + 180_000, releasedAt: T0 + 180_100);
        _server.EnqueueStatus(FakeSalesServer.SessionsSync, 503);         // boşaltmada oturum gidemez

        await Ws.HelloAsync();
        (await Ws.TourAsync()).Should().Be(SalesBackfillTourOutcome.AwaitingPush);
        N(_server.BackfillCalls.SelectMany(c => c.Backfill.Sessions ?? new()).Select(s => s.Id))
            .Should().BeEquivalentTo(new[] { first }, "imleç bekleyen oturumu geçmez");
        _server.BackfillCalls.Should().OnlyContain(c => (c.Backfill.Orders ?? new()).Count == 0,
            "oturum aşaması bitmeden etiketlere geçilmez");

        (await Ws.Push.SyncOnceAsync()).SessionsPushed.Should().Be(1);
        _server.Session(Guid.Parse(pending))!.OwnerReplicaId.Should().BeNull("sessions/sync sahipsiz oturuma sahip yazmaz");

        (await Ws.TourAsync()).Should().Be(SalesBackfillTourOutcome.Completed);
        new[] { first, pending, last }.Should().OnlyContain(id => _server.Session(Guid.Parse(id))!.OwnerReplicaId == ReplicaId);
        CompletionHellos.Should().ContainSingle();
    }

    // ── hello, lisans ve kopya ─────────────────────────────────────────────

    [Fact]
    public async Task Hello_basarili_olmadan_hicbir_istek_gitmez()
    {
        var session = AddSession(T0, T0 + 3600);
        AddLabels(session, 2);
        _server.EnqueueStatus(FakeSalesServer.Hello, 503);

        (await Ws.TourAsync()).Should().Be(SalesBackfillTourOutcome.WaitingForHello);
        (await Ws.State.TourOnceAsync(CancellationToken.None)).Should().Be(SalesStateTourOutcome.Failed);
        (await Ws.TourAsync()).Should().Be(SalesBackfillTourOutcome.WaitingForHello);
        _server.BackfillCalls.Should().BeEmpty();
        _server.PushCalls.Should().BeEmpty("boşaltma da hello'yu bekler");

        Ws.License.CurrentLicenseKey = null;
        (await Ws.TourAsync()).Should().Be(SalesBackfillTourOutcome.NoLicense);
    }

    [Fact]
    public async Task Lisans_degisince_yeni_imlecle_yeni_lisansa_bastan_gider()
    {
        var session = AddSession(T0, T0 + 3600);
        var labels = AddLabels(session, 3);
        SeedServerSession(session, T0, T0 + 3600, releasedAt: T0 + 3700);
        await Ws.HelloAsync();
        (await Ws.TourAsync()).Should().Be(SalesBackfillTourOutcome.Completed);

        Ws.License.CurrentLicenseKey = _server.SecondLisans;
        (await Ws.TourAsync()).Should().Be(SalesBackfillTourOutcome.WaitingForHello);
        await Ws.HelloAsync();
        (await Ws.TourAsync()).Should().Be(SalesBackfillTourOutcome.Completed);

        var second = _server.BackfillCalls.Where(c => c.LicenseId == _server.SecondLicenseId)
            .Select(c => c.Backfill).ToList();
        N(second.SelectMany(b => b.Orders ?? new()).Select(o => o.Id)).Should().BeEquivalentTo(labels);
        _server.Hellos.Where(h => h.LicenseId == _server.SecondLicenseId && h.Body.BackfillCompleted == true)
            .Should().ContainSingle();
        Cursor(_server.SecondLisans)!.Seq.Should().Be(SalesBackfillCursor.Completed.Encode());
        Cursor()!.Seq.Should().Be(SalesBackfillCursor.Completed.Encode(), "önceki lisansın imleci kalır");
    }

    [Fact]
    public async Task Geri_yuklemede_kopya_kimligi_donunce_geri_doldurma_yeni_kopya_icin_bastan()
    {
        var session = AddSession(T0, T0 + 3600);
        AddLabels(session, 2);
        SeedServerSession(session, T0, T0 + 3600, releasedAt: T0 + 3700);
        await Ws.HelloAsync();
        (await Ws.TourAsync()).Should().Be(SalesBackfillTourOutcome.Completed);
        var oldReplica = ReplicaId;

        new ReplicaIdentityRepository(_db).Rotate();
        Restart();
        await Ws.HelloAsync();
        (await Ws.TourAsync()).Should().Be(SalesBackfillTourOutcome.Completed);

        ReplicaId.Should().NotBe(oldReplica);
        _server.BackfillCalls.Select(c => c.Backfill.ReplicaId).Distinct().Should().BeEquivalentTo(
            new Guid?[] { oldReplica, ReplicaId });
        _server.ReplicaRow(_server.LicenseId, ReplicaId)!.BackfillCompleted.Should().BeTrue();
        Cursor()!.LastId.Should().Be(ReplicaId);
    }

    // ── imleç kodlaması ────────────────────────────────────────────────────

    [Fact]
    public void Imlec_asama_ve_rowidi_tek_degerde_tasir_bozuk_deger_bastan_sayilir()
    {
        new SalesBackfillCursor(SalesBackfillPhase.ShipmentLinks, 123).Encode().Should().Be(2_000_000_000_123L);
        SalesBackfillCursor.Decode(1_000_000_000_042L).Should().Be(new SalesBackfillCursor(SalesBackfillPhase.Labels, 42));
        SalesBackfillCursor.Decode(SalesBackfillCursor.Completed.Encode()).Should().Be(SalesBackfillCursor.Completed);
        SalesBackfillCursor.Decode(-1).Should().BeNull();
        SalesBackfillCursor.Decode(5_000_000_000_000L).Should().BeNull();
        SalesBackfillCursor.Decode(3_000_000_000_001L).Should().BeNull("bitmiş aşama rowid taşımaz");
        SalesBackfillCursor.Start.NextPhase().Should().Be(new SalesBackfillCursor(SalesBackfillPhase.Labels, 0));
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TestPatience.Upper;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"koşul {TestPatience.Upper.TotalSeconds} sn'de sağlanmadı");
            await Task.Delay(10);
        }
    }
}
