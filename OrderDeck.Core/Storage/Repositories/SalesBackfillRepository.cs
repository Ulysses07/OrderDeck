using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Dapper;
using OrderDeck.Core.Sales;
using OrderDeck.Core.Sessions;

namespace OrderDeck.Core.Storage.Repositories;

/// <summary>Geri doldurmanın bir oturum satırı ve <c>rowid</c>'i (imleç).</summary>
public sealed record BackfillSessionRow(long RowId, StreamSession Session);

/// <summary>Geri doldurmanın bir etiket satırı ve <c>rowid</c>'i. Biçim gönderiminkiyle aynı
/// (<see cref="LabelPushRow"/>): tel öğesi aynı çeviriyle kurulur.</summary>
public sealed record BackfillLabelRow(long RowId, LabelPushRow Label);

/// <summary>Bir kargo dosyası, <c>rowid</c>'i ve ona bağlı yerel etiketler.</summary>
public sealed record BackfillShipmentRow(long RowId, string ShipmentId, IReadOnlyList<string> LabelIds);

/// <summary>
/// Geri doldurma imleci neden duruyor (§8.2-3): imleçten sonra yerel satır var ama ilki gönderilmeyi bekliyor.
/// Bekleyen satır sunucuda v1 döneminden zaten olabilir; olağan push onu güncelleme dalında yazar ve kaynak kopyayı
/// (<c>Orders.OriginReplicaId</c>) ya da sahibi (<c>OwnerReplicaId</c>) doldurmaz — onları yalnız geri doldurma
/// doldurur. İmleç bu yüzden satırı geçmez; push onaylayınca satır geri doldurmaya girer.
/// </summary>
/// <param name="PendingId">İmleçten sonraki ilk bekleyen satır; sayfa okumasından beri onaylandıysa <c>null</c>
/// (sonraki tur sürer).</param>
/// <param name="Pending">İmleçten sonraki bekleyen satır sayısı.</param>
public sealed record BackfillWait(string? PendingId, int Pending);

/// <summary>Bir ServerVersion yazımı: yalnız satır okunduğundan beri değişmediyse (<paramref name="Revision"/>).</summary>
public readonly record struct BackfillVersion(string LabelId, long Revision, int Version);

/// <summary><see cref="SalesBackfillRepository.Requeue"/>'nun sonucu.</summary>
public enum BackfillRequeueOutcome
{
    /// <summary>Onaylı satır kuyruğa alındı: <c>SyncedAt = NULL</c>, <c>Revision + 1</c> ve iz.</summary>
    Requeued,
    /// <summary>Satır zaten gönderilmeyi bekliyordu (yerel değişiklik): Revision'a dokunulmadı, tamamlanma onun
    /// onayını beklesin diye iz yazıldı.</summary>
    Tracked,
    /// <summary>Satır önceki bir yeniden kuyruğa almadan beri bekliyor (tekrarlanan parti): hiçbir şey yazılmadı.</summary>
    AlreadyQueued,
    /// <summary>Satır yerelde yok.</summary>
    Missing,
}

/// <summary>Tamamlanmayı bekleten yeniden kuyruğa alınmış etiketler (§8.2-5).</summary>
/// <param name="AwaitingPush">Henüz onaylanmadı: sıradaki v2 push'u götürecek.</param>
/// <param name="NotApplied">Yeniden kuyruğa alındıktan sonra gönderimi yok sayıldı ya da reddedildi: hiç
/// <c>applied</c> olmadı. Mod Off'ta olmaz (damga yok, K2).</param>
/// <param name="Missing">Yerelde artık olmayan etiket: bekletmez, yalnız sayılır.</param>
public readonly record struct BackfillCompletionState(int AwaitingPush, int NotApplied, int Missing)
{
    public bool IsComplete => AwaitingPush == 0 && NotApplied == 0;
}

/// <summary>
/// <c>sales/backfill</c>'in (spec 2026-10-13 §4.3, §8.2-3/-4/-5; W1-09) yerel okuma ve yazımları.
///
/// <para><b>Okuma:</b> yalnız bu bilgisayarın satırları (<c>Origin = 'local'</c> oturumlar ve etiketleri) ve yalnız
/// onaylı (<c>SyncedAt</c> dolu) oturum ve etiketler, <c>rowid</c> sırasıyla. Gönderilmeyi bekleyen satır
/// gönderilmez: geri doldurma onu sunucuya bildirimsiz eklese push güncelleme dalına düşer ve canlı yayının "Yayın
/// başladı" ve sipariş bildirimleri hiç çıkmazdı (§4.2 bildirimler yalnız eklemede). Ama imleç onu <b>geçmez</b>
/// (<see cref="BackfillWait"/>): sayfa imleçten sonraki ilk bekleyen satırda biter. v1 döneminden sunucuda zaten
/// olan bekleyen satırı push güncelleme dalında yazar ve kaynak kopyasını ya da sahibini doldurmaz; onaylanınca
/// geri doldurma onu kaldığı yerden götürür.</para>
///
/// <para><b>Yazım:</b> geri doldurma <c>SyncedAt</c> ve <c>Revision</c>'a dokunmaz (§5.2, F05) — iki istisna
/// dışında: birim ayrışmasının onarımı (<see cref="Requeue"/>, §8.2-4) ve devri sunucuda gerçekleşmeyen bitmiş
/// oturumun gönderim kuyruğuna verilmesi (<see cref="QueueSessionForHandover"/>, §5.2). Yazılan kolonların hiçbiri
/// 049 tetikleyicilerinin listesinde değil: kuyruğa alma tetikleyicisi çalışmaz, ABORT tetikleyicileri düşürmez.</para>
///
/// <para>İz satırları (<see cref="SalesConflictLog"/>) yalnız kimlik ve birim adı taşır (§9).</para>
/// </summary>
public sealed class SalesBackfillRepository
{
    private readonly IDbConnectionFactory _factory;

    public SalesBackfillRepository(IDbConnectionFactory factory) => _factory = factory;

    /// <summary>Yerel, onaylı oturumlar; <paramref name="afterRowId"/>'den sonra, rowid sırasıyla ve imleçten sonraki
    /// ilk bekleyen oturumdan önce (imleç onu geçmez). Boşsa <see cref="GetSessionWait"/> nedenini söyler.</summary>
    public IReadOnlyList<BackfillSessionRow> GetSessionsAfter(long afterRowId, int limit)
    {
        using var conn = _factory.Open();
        return conn.Query<SessionRow>(
                @"SELECT rowid AS RowId, Id, Title, StartedAt, EndedAt, Platforms, Notes, SyncedAt, Revision,
                         ReleasedAt, Authority
                  FROM StreamSession
                  WHERE rowid > @afterRowId AND Origin = 'local' AND SyncedAt IS NOT NULL
                    AND rowid < IFNULL((SELECT MIN(rowid) FROM StreamSession
                                        WHERE SyncedAt IS NULL AND rowid > @afterRowId AND Origin = 'local'),
                                       9223372036854775807)
                  ORDER BY rowid
                  LIMIT @limit",
                new { afterRowId, limit })
            .Select(r => new BackfillSessionRow(r.RowId, new StreamSession(
                r.Id, r.Title, r.StartedAt, r.EndedAt,
                JsonSerializer.Deserialize<string[]>(r.Platforms ?? "[]") ?? Array.Empty<string>(),
                r.Notes, r.SyncedAt, r.Revision, r.ReleasedAt, r.Authority)))
            .ToList();
    }

    /// <summary>Yerel oturumların onaylı etiketleri; <paramref name="afterRowId"/>'den sonra, rowid sırasıyla ve
    /// imleçten sonraki ilk bekleyen etiketten önce (imleç onu geçmez). Boşsa <see cref="GetLabelWait"/> nedenini
    /// söyler.</summary>
    public IReadOnlyList<BackfillLabelRow> GetLabelsAfter(long afterRowId, int limit)
    {
        using var conn = _factory.Open();
        return conn.Query<LabelRow>(
                @"SELECT l.rowid AS RowId, l.Id, l.SessionId, l.CustomerId, l.Platform, l.Username, l.DisplayName,
                         l.MessageText, l.Code, l.Price, l.AddedAt, l.PrintedAt, l.CancelledAt, l.CancelReason,
                         l.IsShippingFee, l.IsBackupPromoted, l.IsTentativeBackup, l.ProductId, l.ProductVariantId,
                         l.ParentLabelId, l.ShipmentId, l.PrintBatchId, l.PrintSeq, l.PriceCorrectedAt, l.PaidPrice,
                         l.ServerVersion, l.Revision
                  FROM Label l
                  JOIN StreamSession s ON s.Id = l.SessionId
                  WHERE l.rowid > @afterRowId AND s.Origin = 'local' AND l.SyncedAt IS NOT NULL
                    AND l.rowid < IFNULL((SELECT MIN(p.rowid) FROM Label p
                                          JOIN StreamSession ps ON ps.Id = p.SessionId
                                          WHERE p.SyncedAt IS NULL AND p.rowid > @afterRowId AND ps.Origin = 'local'),
                                         9223372036854775807)
                  ORDER BY l.rowid
                  LIMIT @limit",
                new { afterRowId, limit })
            .Select(r => new BackfillLabelRow(r.RowId, new LabelPushRow(
                r.Id, r.SessionId, r.CustomerId, r.Platform, r.Username, r.DisplayName, r.MessageText, r.Code,
                r.Price, r.AddedAt, r.PrintedAt, r.CancelledAt, r.CancelReason,
                IsShippingFee: r.IsShippingFee != 0,
                IsBackupPromoted: r.IsBackupPromoted != 0,
                IsTentativeBackup: r.IsTentativeBackup != 0,
                r.ProductId, r.ProductVariantId, r.ParentLabelId, r.ShipmentId,
                r.PrintBatchId, r.PrintSeq, r.PriceCorrectedAt, r.PaidPrice, r.ServerVersion, r.Revision)))
            .ToList();
    }

    /// <summary>
    /// <see cref="GetSessionsAfter"/> boş döndüğünde: aşama bitti mi (<c>null</c>: imleçten sonra yerel oturum yok)
    /// yoksa imleç bekleyen bir oturumda mı duruyor. Tek ifade, tek anlık görüntü: sayfa okumasıyla bu okuma
    /// arasında onaylanan satır "aşama bitti" sanılmaz.
    /// </summary>
    public BackfillWait? GetSessionWait(long afterRowId)
    {
        using var conn = _factory.Open();
        var row = conn.QuerySingle<WaitRow>(
            @"SELECT EXISTS(SELECT 1 FROM StreamSession WHERE rowid > @afterRowId AND Origin = 'local') AS AnyAfter,
                     (SELECT Id FROM StreamSession
                      WHERE SyncedAt IS NULL AND rowid > @afterRowId AND Origin = 'local'
                      ORDER BY rowid LIMIT 1) AS PendingId,
                     (SELECT COUNT(*) FROM StreamSession
                      WHERE SyncedAt IS NULL AND rowid > @afterRowId AND Origin = 'local') AS Pending",
            new { afterRowId });
        return row.AnyAfter == 0 ? null : new BackfillWait(row.PendingId, (int)row.Pending);
    }

    /// <summary><see cref="GetLabelsAfter"/> boş döndüğünde: aşama bitti mi (<c>null</c>) yoksa imleç bekleyen bir
    /// etikette mi duruyor (<see cref="GetSessionWait"/> ile aynı kural).</summary>
    public BackfillWait? GetLabelWait(long afterRowId)
    {
        using var conn = _factory.Open();
        var row = conn.QuerySingle<WaitRow>(
            @"SELECT EXISTS(SELECT 1 FROM Label l JOIN StreamSession s ON s.Id = l.SessionId
                            WHERE l.rowid > @afterRowId AND s.Origin = 'local') AS AnyAfter,
                     (SELECT l.Id FROM Label l JOIN StreamSession s ON s.Id = l.SessionId
                      WHERE l.SyncedAt IS NULL AND l.rowid > @afterRowId AND s.Origin = 'local'
                      ORDER BY l.rowid LIMIT 1) AS PendingId,
                     (SELECT COUNT(*) FROM Label l JOIN StreamSession s ON s.Id = l.SessionId
                      WHERE l.SyncedAt IS NULL AND l.rowid > @afterRowId AND s.Origin = 'local') AS Pending",
            new { afterRowId });
        return row.AnyAfter == 0 ? null : new BackfillWait(row.PendingId, (int)row.Pending);
    }

    /// <summary>Kargo dosyaları ve bağlı yerel etiketleri; <paramref name="afterRowId"/>'den sonra, rowid sırasıyla.
    /// Kargoya verilmiş dosya da gider: bağ sunucuda yalnız boşsa yazılır.</summary>
    public IReadOnlyList<BackfillShipmentRow> GetShipmentLinksAfter(long afterRowId, int limit)
    {
        using var conn = _factory.Open();
        var shipments = conn.Query<ShipmentRow>(
            "SELECT rowid AS RowId, Id FROM Shipment WHERE rowid > @afterRowId ORDER BY rowid LIMIT @limit",
            new { afterRowId, limit }).ToList();
        if (shipments.Count == 0) return Array.Empty<BackfillShipmentRow>();

        var links = conn.Query<LinkRow>(
                @"SELECT l.ShipmentId, l.Id
                  FROM Label l
                  JOIN StreamSession s ON s.Id = l.SessionId
                  WHERE l.ShipmentId IN @ids AND s.Origin = 'local'
                  ORDER BY l.rowid",
                new { ids = shipments.Select(s => s.Id).ToList() })
            .GroupBy(r => r.ShipmentId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(r => r.Id).ToList(), StringComparer.Ordinal);
        return shipments
            .Select(s => new BackfillShipmentRow(
                s.RowId, s.Id, links.TryGetValue(s.Id, out var ids) ? ids : Array.Empty<string>()))
            .ToList();
    }

    /// <summary>Bir yayının gönderilmemiş etiket sayısı (§5.2: <c>Release</c> yalnız 0 iken). Devir kapısı altında
    /// okunur.</summary>
    public int CountUnsyncedBySession(string sessionId)
    {
        using var conn = _factory.Open();
        return conn.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM Label WHERE SessionId = @sessionId AND SyncedAt IS NULL", new { sessionId });
    }

    /// <summary>
    /// Sunucunun devir anını saklar (§5.2 mod Off: yalnız <c>ReleasedAt</c>). Yerelde zaten biliniyorsa dokunulmaz.
    /// <c>Authority</c>, <c>SyncedAt</c> ve <c>Revision</c> değişmez; <c>ReleasedAt</c> hiçbir tetikleyici listesinde
    /// değil.
    /// </summary>
    /// <returns>Yazıldı mı.</returns>
    public bool StoreReleasedAt(string sessionId, long releasedAt)
    {
        using var conn = _factory.Open();
        return conn.Execute(
            "UPDATE StreamSession SET ReleasedAt = @releasedAt WHERE Id = @sessionId AND ReleasedAt IS NULL",
            new { sessionId, releasedAt }) == 1;
    }

    /// <summary>
    /// Devri sunucuda gerçekleşmeyen bitmiş oturumu (gönderilmemiş etiketi var ya da sunucu devri yazmadı) olağan
    /// gönderim kuyruğuna verir: <c>SyncedAt = NULL</c>. Kapanış gönderimi (§5.2) onu devir görülene kadar her turda
    /// yeniden gönderir ve kuyruğu boşalınca <c>Release = true</c> ile devreder; geri doldurma kaçmış kapanışı yalnız
    /// bir kez dener. Yalnız satır okunduğundan beri değişmediyse (Revision; değiştiyse zaten kuyruktadır) ve hâlâ
    /// bitmiş, yerel ve devri bilinmiyorsa. Revision artmaz: oturumun değeri değişmedi, yalnız yeniden gidecek.
    /// </summary>
    /// <returns>Kuyruğa verildi mi.</returns>
    public bool QueueSessionForHandover(string sessionId, long revision)
    {
        using var conn = _factory.Open();
        return conn.Execute(
            @"UPDATE StreamSession SET SyncedAt = NULL
              WHERE Id = @sessionId AND Revision = @revision AND SyncedAt IS NOT NULL
                AND EndedAt IS NOT NULL AND ReleasedAt IS NULL AND Authority = 'local'",
            new { sessionId, revision }) == 1;
    }

    /// <summary>
    /// Sunucunun sürümünü (<c>ServerVersion</c>, sonraki push'un <c>baseVersion</c>'ı) tek kısa işlemde yazar —
    /// yalnız satır okunduğundan beri değişmediyse (Revision) ve sürüm geri gitmiyorsa: aynı anda giden bir push
    /// daha yeni sürümü yazmış olabilir. <c>ServerVersion</c> hiçbir tetikleyici listesinde değil.
    /// </summary>
    /// <returns>Yazılan satır sayısı.</returns>
    public int WriteServerVersions(IReadOnlyList<BackfillVersion> versions)
    {
        if (versions.Count == 0) return 0;
        using var write = DbWrite.Begin(_factory);
        var written = write.Connection.Execute(
            @"UPDATE Label SET ServerVersion = @version
              WHERE Id = @labelId AND Revision = @revision
                AND (ServerVersion IS NULL OR ServerVersion < @version)",
            versions.Select(v => new { labelId = v.LabelId, revision = v.Revision, version = v.Version }).ToArray(),
            write.Transaction);
        write.Commit();
        return written;
    }

    /// <summary>
    /// Birim ayrışmasının onarımı, yerel kazanır (§8.2-4): tek işlemde satırı olağan kuyruğa alır ve
    /// <see cref="SalesConflictLog.BackfillRequeued"/> izini yazar. Onaylı satırda <c>SyncedAt = NULL</c> ve
    /// <c>Revision + 1</c>; iptal ya da yedek ayrıştıysa <c>StockSyncedAt = NULL</c> (<c>TR_Label_Enqueue</c>'nun
    /// aynası — sunucunun defteri o birimi görmedi). Zaten bekleyen satırın Revision'ı artmaz (push bütün birimleri
    /// götürecek); izi yalnız önceki bir yeniden kuyruğa alma onu izlemiyorsa yazılır — aynı parti ikinci kez gelse
    /// satır bir kez kuyruğa girer.
    /// </summary>
    /// <param name="units">Ayrışan birimler (teldeki adlarıyla, <see cref="SalesPushUnits"/>).</param>
    public BackfillRequeueOutcome Requeue(string labelId, IReadOnlyCollection<string> units, long now)
    {
        var stock = units.Contains(SalesPushUnits.Cancel) || units.Contains(SalesPushUnits.Backup);
        using var write = DbWrite.Begin(_factory);
        var syncedAt = write.Connection.QuerySingleOrDefault<SyncedRow>(
            "SELECT SyncedAt FROM Label WHERE Id = @labelId", new { labelId }, write.Transaction);
        if (syncedAt is null) return BackfillRequeueOutcome.Missing;

        var pending = syncedAt.SyncedAt is null;
        if (pending && IsTracked(write, labelId)) return BackfillRequeueOutcome.AlreadyQueued;

        // SET ifadeleri eski satırla değerlendirilir: Revision yalnız onaylı satırda artar.
        write.Connection.Execute(
            @"UPDATE Label SET
                  Revision = CASE WHEN SyncedAt IS NOT NULL THEN Revision + 1 ELSE Revision END,
                  SyncedAt = NULL,
                  StockSyncedAt = CASE WHEN @stock THEN NULL ELSE StockSyncedAt END
              WHERE Id = @labelId",
            new { labelId, stock }, write.Transaction);
        SalesConflictLog.Insert(write.Connection, write.Transaction, now, labelId,
            SalesConflictLog.BackfillRequeued, SalesConflictLog.UnitsReason(units));
        write.Commit();
        return pending ? BackfillRequeueOutcome.Tracked : BackfillRequeueOutcome.Requeued;
    }

    /// <summary>
    /// Birim ayrışması, sunucu kazanır (§4.3, §8.7): yalnız <see cref="SalesConflictLog.BackfillDiverged"/> izi —
    /// aynı etiket ve birimler için zaten varsa yazılmaz (tekrarlanan parti).
    /// </summary>
    /// <returns>İz yazıldı mı.</returns>
    public bool LogDiverged(string labelId, IReadOnlyCollection<string> units, long now)
    {
        var reason = SalesConflictLog.UnitsReason(units);
        using var write = DbWrite.Begin(_factory);
        var exists = write.Connection.ExecuteScalar<long>(
            @"SELECT EXISTS(SELECT 1 FROM SalesConflictLog
                            WHERE LabelId = @labelId AND Kind = @kind AND Reason = @reason)",
            new { labelId, kind = SalesConflictLog.BackfillDiverged, reason }, write.Transaction) != 0;
        if (exists) return false;
        SalesConflictLog.Insert(write.Connection, write.Transaction, now, labelId,
            SalesConflictLog.BackfillDiverged, reason);
        write.Commit();
        return true;
    }

    /// <summary>
    /// Tamamlanma koşulunun yerel yarısı (§8.2-5): her etiketin en son <see cref="SalesConflictLog.BackfillRequeued"/>
    /// izinden sonra satır onaylandı (<c>SyncedAt</c> dolu) ve o izden sonra <c>push-ignored</c> ya da
    /// <c>push-rejected</c> izi yok — yani ilk gönderimi <c>applied</c> oldu. Sıra izin <c>Id</c>'siyle (aynı saniyede
    /// de doğru).
    /// </summary>
    public BackfillCompletionState GetCompletionState()
    {
        using var conn = _factory.Open();
        var rows = conn.Query<CompletionRow>(
            @"WITH latest AS (
                  SELECT LabelId, MAX(Id) AS RequeueId
                  FROM SalesConflictLog
                  WHERE Kind = @requeued AND LabelId IS NOT NULL
                  GROUP BY LabelId)
              SELECT (l.Id IS NOT NULL) AS Present,
                     (l.SyncedAt IS NOT NULL) AS Synced,
                     EXISTS(SELECT 1 FROM SalesConflictLog x
                            WHERE x.LabelId = latest.LabelId AND x.Kind IN (@ignored, @rejected)
                              AND x.Id > latest.RequeueId) AS NotApplied
              FROM latest
              LEFT JOIN Label l ON l.Id = latest.LabelId",
            new
            {
                requeued = SalesConflictLog.BackfillRequeued,
                ignored = SalesConflictLog.PushIgnored,
                rejected = SalesConflictLog.PushRejected,
            }).ToList();

        int awaiting = 0, notApplied = 0, missing = 0;
        foreach (var r in rows)
        {
            if (r.Present == 0) missing++;
            else if (r.NotApplied != 0) notApplied++;
            else if (r.Synced == 0) awaiting++;
        }
        return new BackfillCompletionState(awaiting, notApplied, missing);
    }

    /// <summary>Etiketin en son yeniden kuyruğa alma izi var ve ondan sonra gönderimi yok sayılmadı/reddedilmedi:
    /// o iz hâlâ satırı izliyor.</summary>
    private static bool IsTracked(DbWrite write, string labelId)
        => write.Connection.ExecuteScalar<long>(
            @"SELECT EXISTS(
                  SELECT 1 FROM SalesConflictLog r
                  WHERE r.LabelId = @labelId AND r.Kind = @requeued
                    AND r.Id = (SELECT MAX(Id) FROM SalesConflictLog WHERE LabelId = @labelId AND Kind = @requeued)
                    AND NOT EXISTS(SELECT 1 FROM SalesConflictLog x
                                   WHERE x.LabelId = @labelId AND x.Kind IN (@ignored, @rejected) AND x.Id > r.Id))",
            new
            {
                labelId,
                requeued = SalesConflictLog.BackfillRequeued,
                ignored = SalesConflictLog.PushIgnored,
                rejected = SalesConflictLog.PushRejected,
            },
            write.Transaction) != 0;

    private sealed class SyncedRow
    {
        public long? SyncedAt { get; init; }
    }

    private sealed class WaitRow
    {
        public long AnyAfter { get; init; }
        public string? PendingId { get; init; }
        public long Pending { get; init; }
    }

    private sealed class CompletionRow
    {
        public long Present { get; init; }
        public long Synced { get; init; }
        public long NotApplied { get; init; }
    }

    private sealed class ShipmentRow
    {
        public long RowId { get; init; }
        public string Id { get; init; } = "";
    }

    private sealed class LinkRow
    {
        public string ShipmentId { get; init; } = "";
        public string Id { get; init; } = "";
    }

    private sealed class SessionRow
    {
        public long RowId { get; init; }
        public string Id { get; init; } = "";
        public string? Title { get; init; }
        public long StartedAt { get; init; }
        public long? EndedAt { get; init; }
        public string? Platforms { get; init; }
        public string? Notes { get; init; }
        public long? SyncedAt { get; init; }
        public long Revision { get; init; }
        public long? ReleasedAt { get; init; }
        public string Authority { get; init; } = StreamSessionAuthority.Local;
    }

    private sealed class LabelRow
    {
        public long RowId { get; init; }
        public string Id { get; init; } = "";
        public string SessionId { get; init; } = "";
        public string CustomerId { get; init; } = "";
        public string Platform { get; init; } = "";
        public string Username { get; init; } = "";
        public string? DisplayName { get; init; }
        public string MessageText { get; init; } = "";
        public string? Code { get; init; }
        public decimal Price { get; init; }
        public long AddedAt { get; init; }
        public long? PrintedAt { get; init; }
        public long? CancelledAt { get; init; }
        public string? CancelReason { get; init; }
        public int IsShippingFee { get; init; }
        public int IsBackupPromoted { get; init; }
        public int IsTentativeBackup { get; init; }
        public string? ProductId { get; init; }
        public string? ProductVariantId { get; init; }
        public string? ParentLabelId { get; init; }
        public string? ShipmentId { get; init; }
        public string? PrintBatchId { get; init; }
        public long? PrintSeq { get; init; }
        public long? PriceCorrectedAt { get; init; }
        public decimal? PaidPrice { get; init; }
        public long? ServerVersion { get; init; }
        public long Revision { get; init; }
    }
}
