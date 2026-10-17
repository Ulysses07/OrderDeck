using Dapper;
using FluentAssertions;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Sales;
using OrderDeck.Core.Sessions;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.Tests.Storage;

/// <summary>
/// Geri doldurmanın yerel okuma ve yazımları (spec 2026-10-13 §4.3, §8.2-3/-4/-5; W1-09): yalnız yerel ve onaylı
/// satırlar rowid sırasıyla; yeniden kuyruğa alma bir kez, onaylı satırda Revision+1 ve gerekiyorsa StockSyncedAt;
/// sunucu kazanırsa yalnız iz; tamamlanma koşulu; devir anı yalnız ReleasedAt; ServerVersion karşılaştırmalı.
/// </summary>
public sealed class SalesBackfillRepositoryTests : IDisposable
{
    private const long T0 = 1_755_000_000L;

    private readonly InMemorySqlite _db = new();
    private readonly SalesBackfillRepository _repo;
    private readonly SessionRepository _sessions;
    private readonly LabelRepository _labels;
    private readonly string _customerId = Guid.NewGuid().ToString("N");
    private long _addedAt = T0 + 100;

    public SalesBackfillRepositoryTests()
    {
        new MigrationRunner(_db).Run();
        _repo = new SalesBackfillRepository(_db);
        _sessions = new SessionRepository(_db);
        _labels = new LabelRepository(_db);
        new CustomerRepository(_db).Insert(new Customer(_customerId, "tiktok", "ornek_alici", "Örnek Alıcı",
            null, 1, 1, false, null, null, 0, 0m, null, null, null));
    }

    public void Dispose() => _db.Dispose();

    private void Exec(string sql, object? args = null)
    {
        using var c = _db.Open();
        c.Execute(sql, args);
    }

    private T Scalar<T>(string sql, object? args = null)
    {
        using var c = _db.Open();
        return c.ExecuteScalar<T>(sql, args)!;
    }

    private string AddSession(long? endedAt = T0 + 3600, bool synced = true, string origin = "local")
    {
        var id = Guid.NewGuid().ToString("N");
        Exec(@"INSERT INTO StreamSession (Id, Title, StartedAt, EndedAt, Platforms, SyncedAt, Origin, Authority)
               VALUES (@id, 'Örnek yayın', @T0, @endedAt, '[""tiktok""]', @syncedAt, @origin,
                       CASE WHEN @origin = 'remote' THEN 'server' ELSE 'local' END)",
            new { id, T0, endedAt, syncedAt = synced ? T0 + 10 : (long?)null, origin });
        return id;
    }

    private string AddLabel(string sessionId, bool synced = true, string? shipmentId = null, long? cancelledAt = null)
    {
        var id = Guid.NewGuid().ToString("N");
        var added = _addedAt++;
        // Uzak (Authority = 'server') oturumun etiketi akıştan iner: kilit altında, yerel ekleme ABORT edilir.
        using var scope = SyncApplyScope.Begin(_db);
        scope.Execute(@"INSERT INTO Label (Id, SessionId, CustomerId, Platform, Username, MessageText, Code, Price, AddedAt,
                                  CancelledAt, ShipmentId, SyncedAt, StockSyncedAt)
               VALUES (@id, @sessionId, @customerId, 'tiktok', 'ornek_alici', 'alıyorum', 'A1', 100, @added,
                       @cancelledAt, @shipmentId, @syncedAt, @syncedAt)",
            new { id, sessionId, customerId = _customerId, added, cancelledAt, shipmentId,
                  syncedAt = synced ? added + 5 : (long?)null });
        scope.Commit();
        return id;
    }

    private (long? SyncedAt, long? StockSyncedAt, long Revision) Row(string labelId)
    {
        using var c = _db.Open();
        return c.QuerySingle<(long?, long?, long)>(
            "SELECT SyncedAt, StockSyncedAt, Revision FROM Label WHERE Id = @labelId", new { labelId });
    }

    private long LogCount(string? labelId = null) => Scalar<long>(
        "SELECT COUNT(*) FROM SalesConflictLog WHERE @labelId IS NULL OR LabelId = @labelId", new { labelId });

    // ── okuma ──────────────────────────────────────────────────────────────

    [Fact]
    public void Okuma_yalniz_yerel_ve_onayli_satirlari_rowid_sirasiyla_partiler()
    {
        var a = AddSession();
        var pending = AddSession(synced: false);
        var remote = AddSession(origin: "remote");
        var b = AddSession(endedAt: null);
        var la1 = AddLabel(a);
        AddLabel(a, synced: false);
        AddLabel(remote);
        var lb1 = AddLabel(b);
        var la2 = AddLabel(a);

        _repo.GetSessionsAfter(0, 10).Select(r => r.Session.Id).Should().Equal(a, b);
        var first = _repo.GetSessionsAfter(0, 1).Single();
        first.Session.Id.Should().Be(a);
        _repo.GetSessionsAfter(first.RowId, 10).Select(r => r.Session.Id).Should().Equal(b);
        _ = pending;

        var labels = _repo.GetLabelsAfter(0, 10);
        labels.Select(r => r.Label.Id).Should().Equal(la1, lb1, la2);
        labels.Select(r => r.RowId).Should().BeInAscendingOrder();
        _repo.GetLabelsAfter(labels[0].RowId, 1).Select(r => r.Label.Id).Should().Equal(lb1);
    }

    [Fact]
    public void Dosya_baglari_dosya_basina_yerel_etiketleri_tasir()
    {
        var shipments = new ShipmentRepository(_db);
        var s1 = Guid.NewGuid().ToString("N");
        var s2 = Guid.NewGuid().ToString("N");
        shipments.Insert(new Shipment(s1, _customerId, ShipmentStatus.Held, T0, T0, null, 100m));
        shipments.Insert(new Shipment(s2, _customerId, ShipmentStatus.Pending, T0, null, null, 0m));
        var session = AddSession();
        var remote = AddSession(origin: "remote");
        var l1 = AddLabel(session, shipmentId: s1);
        var l2 = AddLabel(session, synced: false, shipmentId: s1);
        AddLabel(remote, shipmentId: s1);

        var rows = _repo.GetShipmentLinksAfter(0, 10);
        rows.Select(r => r.ShipmentId).Should().Equal(s1, s2);
        rows[0].LabelIds.Should().Equal(l1, l2);
        rows[1].LabelIds.Should().BeEmpty();
        _repo.GetShipmentLinksAfter(rows[0].RowId, 10).Select(r => r.ShipmentId).Should().Equal(s2);
    }

    // ── yeniden kuyruğa alma ───────────────────────────────────────────────

    [Fact]
    public void Onayli_satir_bir_kez_kuyruga_alinir_iptalde_stok_damgasi_duser_fiyatta_dusmez()
    {
        var session = AddSession();
        var cancelled = AddLabel(session, cancelledAt: T0 + 500);
        var priced = AddLabel(session);
        var before = Row(cancelled);

        _repo.Requeue(cancelled, ["cancel", "print"], T0 + 1000).Should().Be(BackfillRequeueOutcome.Requeued);
        _repo.Requeue(priced, ["price"], T0 + 1000).Should().Be(BackfillRequeueOutcome.Requeued);

        var after = Row(cancelled);
        after.SyncedAt.Should().BeNull();
        after.StockSyncedAt.Should().BeNull();
        after.Revision.Should().Be(before.Revision + 1);
        Row(priced).StockSyncedAt.Should().NotBeNull("yalnız fiyat ayrıştı");
        Scalar<string>("SELECT Kind || '|' || Reason FROM SalesConflictLog WHERE LabelId = @cancelled", new { cancelled })
            .Should().Be("backfill-requeued|units=cancel,print");

        // Aynı parti yeniden: satır zaten bekliyor ve iz onu izliyor.
        _repo.Requeue(cancelled, ["cancel", "print"], T0 + 1100).Should().Be(BackfillRequeueOutcome.AlreadyQueued);
        Row(cancelled).Revision.Should().Be(before.Revision + 1);
        LogCount(cancelled).Should().Be(1);
    }

    [Fact]
    public void Bekleyen_satirin_Revisioni_artmaz_iz_tamamlanmayi_bekletir_yok_sayilan_gonderimden_sonra_yeniden_izlenir()
    {
        var session = AddSession();
        var label = AddLabel(session, synced: false);
        var before = Row(label);

        _repo.Requeue(label, ["price"], T0 + 1000).Should().Be(BackfillRequeueOutcome.Tracked);
        Row(label).Revision.Should().Be(before.Revision, "push zaten bütün birimleri götürecek");
        LogCount(label).Should().Be(1);

        // Gönderim yok sayıldı (iz) ve satır yeniden değişti: yeni yeniden kuyruğa alma yeniden izlenir.
        Exec("INSERT INTO SalesConflictLog (At, LabelId, Kind, Reason) VALUES (@at, @label, 'push-ignored', 'x')",
            new { at = T0 + 1100, label });
        _repo.Requeue(label, ["price"], T0 + 1200).Should().Be(BackfillRequeueOutcome.Tracked);
        LogCount(label).Should().Be(3);

        _repo.Requeue(Guid.NewGuid().ToString("N"), ["price"], T0).Should().Be(BackfillRequeueOutcome.Missing);
    }

    [Fact]
    public void Sunucu_kazanirsa_yalniz_iz_tekrarlanan_partide_iz_yinelenmez()
    {
        var label = AddLabel(AddSession());
        var before = Row(label);

        _repo.LogDiverged(label, ["cancel"], T0).Should().BeTrue();
        _repo.LogDiverged(label, ["cancel"], T0 + 60).Should().BeFalse();
        _repo.LogDiverged(label, ["cancel", "print"], T0 + 60).Should().BeTrue("farklı birim kümesi");

        Row(label).Should().Be(before);
        Scalar<string>("SELECT Kind FROM SalesConflictLog WHERE LabelId = @label LIMIT 1", new { label })
            .Should().Be("backfill-diverged");
    }

    [Fact]
    public void Tamamlanma_her_yeniden_kuyruk_etiketinin_sonraki_gonderimi_applied_olunca()
    {
        var session = AddSession();
        var awaiting = AddLabel(session);
        var applied = AddLabel(session);
        var ignored = AddLabel(session);
        _repo.GetCompletionState().Should().Be(new BackfillCompletionState(0, 0, 0));
        _repo.GetCompletionState().IsComplete.Should().BeTrue();

        foreach (var id in new[] { awaiting, applied, ignored }) _repo.Requeue(id, ["cancel"], T0 + 1000);
        Exec("INSERT INTO SalesConflictLog (At, LabelId, Kind, Reason) VALUES (@at, @gone, 'backfill-requeued', 'units=price')",
            new { at = T0 + 1000, gone = Guid.NewGuid().ToString("N") });
        _repo.GetCompletionState().Should().Be(new BackfillCompletionState(3, 0, 1));

        // applied: onay; ignored: onay + push-ignored izi (sunucunun current'ı uygulandı).
        Exec("UPDATE Label SET SyncedAt = @at WHERE Id IN @ids", new { at = T0 + 2000, ids = new[] { applied, ignored } });
        Exec("INSERT INTO SalesConflictLog (At, LabelId, Kind, Reason) VALUES (@at, @ignored, 'push-ignored', 'x')",
            new { at = T0 + 2000, ignored });
        var state = _repo.GetCompletionState();
        state.Should().Be(new BackfillCompletionState(1, 1, 1));
        state.IsComplete.Should().BeFalse();

        Exec("UPDATE Label SET SyncedAt = @at WHERE Id = @awaiting", new { at = T0 + 3000, awaiting });
        _repo.GetCompletionState().Should().Be(new BackfillCompletionState(0, 1, 1), "hiç uygulanmayan satır bekletir");

        // Yeniden kuyruğa alınıp bu kez uygulanırsa tamamlanır.
        _repo.Requeue(ignored, ["cancel"], T0 + 4000).Should().Be(BackfillRequeueOutcome.Requeued);
        Exec("UPDATE Label SET SyncedAt = @at WHERE Id = @ignored", new { at = T0 + 5000, ignored });
        _repo.GetCompletionState().IsComplete.Should().BeTrue();
    }

    // ── oturum ve sürüm ────────────────────────────────────────────────────

    [Fact]
    public void Devir_ani_yalniz_ReleasedAt_yazar_bir_kez()
    {
        var id = AddSession();
        var before = _sessions.GetById(id)!;

        _repo.StoreReleasedAt(id, T0 + 4000).Should().BeTrue();
        _repo.StoreReleasedAt(id, T0 + 5000).Should().BeFalse("yerelde bilinen devir değişmez");

        var after = _sessions.GetById(id)!;
        after.ReleasedAt.Should().Be(T0 + 4000);
        after.Authority.Should().Be(StreamSessionAuthority.Local);
        after.SyncedAt.Should().Be(before.SyncedAt);
        after.Revision.Should().Be(before.Revision);
    }

    [Fact]
    public void Devri_gerceklesmeyen_bitmis_oturum_yalniz_degismediyse_kuyruga_verilir()
    {
        var ended = AddSession();
        var active = AddSession(endedAt: null);
        var released = AddSession();
        Exec("UPDATE StreamSession SET ReleasedAt = @at WHERE Id = @released", new { at = T0 + 4000, released });
        var changed = AddSession();

        _repo.QueueSessionForHandover(ended, 0).Should().BeTrue();
        _sessions.GetById(ended)!.SyncedAt.Should().BeNull();
        _sessions.GetById(ended)!.Revision.Should().Be(0);
        _repo.QueueSessionForHandover(active, 0).Should().BeFalse("aktif oturum");
        _repo.QueueSessionForHandover(released, 0).Should().BeFalse("devri biliniyor");
        _repo.QueueSessionForHandover(changed, 5).Should().BeFalse("okunduğundan beri değişti");
    }

    [Fact]
    public void ServerVersion_yalniz_degismemis_satira_ve_geri_gitmeden_yazilir()
    {
        var session = AddSession();
        var fresh = AddLabel(session);
        var changed = AddLabel(session);
        var newer = AddLabel(session);
        Exec("UPDATE Label SET ServerVersion = 9 WHERE Id = @newer", new { newer });

        _repo.WriteServerVersions(new[]
        {
            new BackfillVersion(fresh, 0, 4),
            new BackfillVersion(changed, 7, 4),
            new BackfillVersion(newer, 0, 6),
        }).Should().Be(1);

        Scalar<long?>("SELECT ServerVersion FROM Label WHERE Id = @fresh", new { fresh }).Should().Be(4);
        Scalar<long?>("SELECT ServerVersion FROM Label WHERE Id = @changed", new { changed }).Should().BeNull();
        Scalar<long?>("SELECT ServerVersion FROM Label WHERE Id = @newer", new { newer }).Should().Be(9);
        Row(fresh).Revision.Should().Be(0, "ServerVersion tetikleyici dışı");
    }
}
