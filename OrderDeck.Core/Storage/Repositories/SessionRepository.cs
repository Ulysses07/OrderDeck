using System.Data;
using System.Linq;
using System.Text.Json;
using Dapper;
using OrderDeck.Core.Sessions;

namespace OrderDeck.Core.Storage.Repositories;

/// <summary>
/// Sunucunun bir oturum öğesine yanıtı (<c>sessions/sync</c>; spec 2026-10-13 §4.1, §5.2) —
/// <see cref="SessionRepository.ApplyRelease"/>'in girdisi. Eski sunucunun yanıtında (ya da yanıtta olmayan
/// oturumda) hepsi boştur.
/// </summary>
/// <param name="Protocol">Sunucunun konuştuğu protokol; 2 değilse sunucu eski sürümdür.</param>
/// <param name="ReleasedAt">Sunucunun devir anı (unix saniye); devredilmemiş oturumda boş.</param>
/// <param name="Status"><c>applied</c> ya da <c>ignored</c>.</param>
/// <param name="Reason"><c>ignored</c>'ın nedeni (<see cref="SalesConflictLog.SessionOwnedElsewhere"/>, …).</param>
public sealed record SessionPushResult(int? Protocol, long? ReleasedAt, string? Status, string? Reason)
{
    public const int ProtocolV2 = 2;
    public const string StatusIgnored = "ignored";

    /// <summary>Eski sunucu: yanıtta protokol yok.</summary>
    public static readonly SessionPushResult Legacy = new(null, null, null, null);
}

/// <summary>Devri bekleyen kendi oturumu (<see cref="SessionRepository.GetPendingHandovers"/>): bitmiş,
/// <c>Authority = 'local'</c>, devri görülmemiş ve kuyrukta.</summary>
/// <param name="StartedAt">Yayının başlangıcı (unix saniye): durum satırı yayını bununla anar.</param>
/// <param name="UnsentLabels">Yayının gönderilmemiş (<c>SyncedAt IS NULL</c>) etiket sayısı.</param>
public sealed record PendingHandover(string SessionId, long StartedAt, int UnsentLabels);

/// <summary><see cref="SessionRepository.ApplyRelease"/>'in sonucu.</summary>
public enum SessionAckOutcome
{
    /// <summary>Onaylandı (eski sunucu ya da devir beklemeyen oturum).</summary>
    Synced,
    /// <summary>Devir görüldü: <c>ReleasedAt</c> ve onay tek işlemde yazıldı.</summary>
    Released,
    /// <summary>Bitmiş ama devredilmemiş (M2): onaylanmadı, kuyrukta kalır ve sonraki turda yeniden gider.
    /// "Gönderilen kayıt" sayılmaz.</summary>
    Held,
    /// <summary>Sahip olmayanın kapanışı (§8.7): iz yazıldı ve onaylandı; son durum.</summary>
    OwnedElsewhere,
    /// <summary>Satır uçuşta değişti (F05): onay yazılmadı, güncel hâliyle yeniden gider. Devir anı yine
    /// saklanmış olabilir (sunucunun olgusu).</summary>
    Stale,
}

public sealed class SessionRepository
{
    private const string Columns =
        "Id, Title, StartedAt, EndedAt, Platforms, Notes, SyncedAt, Revision, ReleasedAt, Authority, Origin, OwnerMachineName";

    private readonly IDbConnectionFactory _factory;
    public SessionRepository(IDbConnectionFactory factory) => _factory = factory;

    public void Insert(StreamSession session)
    {
        using var conn = _factory.Open();
        conn.Execute(
            @"INSERT INTO StreamSession(Id, Title, StartedAt, EndedAt, Platforms, Notes, SyncedAt)
              VALUES(@Id, @Title, @StartedAt, @EndedAt, @Platforms, @Notes, @SyncedAt)",
            new
            {
                session.Id,
                session.Title,
                session.StartedAt,
                session.EndedAt,
                Platforms = JsonSerializer.Serialize(session.Platforms),
                session.Notes,
                session.SyncedAt
            });
    }

    public void End(string id, long endedAt)
    {
        using var conn = _factory.Open();
        // State değişikliği → SyncedAt NULL'a düşür ki sonraki tick'te push olsun.
        conn.Execute(
            "UPDATE StreamSession SET EndedAt=@endedAt, SyncedAt=NULL, Revision = Revision + 1 WHERE Id=@id",
            new { id, endedAt });
    }

    /// <summary>Gönderim kuyruğu boş mu — kısmi <c>IX_StreamSession_SyncedAt</c> dizininden tek satır okur
    /// (<see cref="LabelRepository.HasUnsynced"/> ile aynı amaç).</summary>
    public bool HasUnsynced()
    {
        using var conn = _factory.Open();
        return conn.ExecuteScalar<long>("SELECT EXISTS(SELECT 1 FROM StreamSession WHERE SyncedAt IS NULL)") != 0;
    }

    /// <summary>Bu bilgisayarın devri bekleyen bir oturumu var mı (spec 2026-10-13 §5.2): bitmiş,
    /// <c>Authority = 'local'</c>, devri görülmemiş ve kuyrukta (gönderilmemiş etiketleri yüzünden bekletiliyor). Kısmi
    /// <c>IX_StreamSession_SyncedAt</c> dizininden okur.</summary>
    public bool HasPendingHandover()
    {
        using var conn = _factory.Open();
        return conn.ExecuteScalar<long>(
            @"SELECT EXISTS(SELECT 1 FROM StreamSession
                            WHERE SyncedAt IS NULL AND EndedAt IS NOT NULL AND ReleasedAt IS NULL
                              AND Authority = 'local')") != 0;
    }

    /// <summary>Devri bekleyen kendi oturumları (<see cref="HasPendingHandover"/> ile aynı koşul) ve her birinin
    /// gönderilmemiş etiket sayısı (spec 2026-10-13 §5.7 "Yayın X devredilemedi", §12 risk 4). Başlama sırasıyla.</summary>
    public System.Collections.Generic.IReadOnlyList<PendingHandover> GetPendingHandovers()
    {
        using var conn = _factory.Open();
        return conn.Query<PendingHandoverRow>(
                @"SELECT s.Id, s.StartedAt,
                         (SELECT COUNT(*) FROM Label l WHERE l.SessionId = s.Id AND l.SyncedAt IS NULL) AS UnsentLabels
                  FROM StreamSession s
                  WHERE s.SyncedAt IS NULL AND s.EndedAt IS NOT NULL AND s.ReleasedAt IS NULL
                    AND s.Authority = 'local'
                  ORDER BY s.StartedAt, s.Id")
            .Select(r => new PendingHandover(r.Id, r.StartedAt, (int)r.UnsentLabels))
            .ToList();
    }

    private sealed class PendingHandoverRow
    {
        public string Id { get; init; } = "";
        public long StartedAt { get; init; }
        public long UnsentLabels { get; init; }
    }

    /// <summary>
    /// PR siparis-sync (2026-05-13): henüz LicenseServer'a push edilmemiş
    /// session'lar. Outbox query.
    ///
    /// <para>Devir bekleyen bitmiş oturum (spec 2026-10-13 §5.2, M2) devir görülene kadar burada kalır; etiketi
    /// kalıcı olarak gönderilemeyen yayınlar birikebilir (§12 risk 4). Bu yüzden önce açık (aktif) oturumlar gelir:
    /// birikmiş kapanışlar partiyi doldurup canlı yayının oturum satırını — ve onsuz sunucunun kabul etmeyeceği
    /// etiketlerini — dışarıda bırakamaz.</para>
    /// </summary>
    public System.Collections.Generic.IReadOnlyList<StreamSession> GetUnsynced(int limit = 50)
    {
        using var conn = _factory.Open();
        var rows = SqlMapper.Query<Row>(conn,
            $@"SELECT {Columns}
              FROM StreamSession
              WHERE SyncedAt IS NULL
              ORDER BY EndedAt IS NOT NULL, StartedAt
              LIMIT @limit",
            new { limit }).ToList();
        return rows.Select(Map).ToList();
    }

    /// <summary>Push başarılı sonrası SyncedAt'i set eder.
    /// F05: compare-and-set — push uçuştayken Revision değiştiyse 0 satır
    /// etkiler, satır bekleyen kalır (bkz. <see cref="LabelRepository.MarkSynced"/>).</summary>
    public void MarkSynced(string id, long syncedAt, long revision)
    {
        using var conn = _factory.Open();
        conn.Execute("UPDATE StreamSession SET SyncedAt=@syncedAt WHERE Id=@id AND Revision=@revision",
            new { id, syncedAt, revision });
    }

    /// <summary>
    /// Bir oturumun <c>sessions/sync</c> yanıtını uygular (spec 2026-10-13 §5.2 "Kapanış sırası", §2 "Yerel
    /// otorite", §5.4, §8.7). <paramref name="revision"/> gönderilen satırın Revision'ı (F05 karşılaştır-ve-yaz).
    /// <list type="bullet">
    /// <item><c>Protocol ≠ 2</c> (eski sunucu, ya da yanıtta olmayan oturum): bugünkü gibi onay.</item>
    /// <item><c>ReleasedAt</c> dolu: tek <see cref="SyncApplyScope"/> işleminde <c>ReleasedAt</c> ve onay.
    /// <c>Authority = 'server'</c> YALNIZ aynı işlemde okunan <c>SalesSyncState.Mode = 1</c> ∧ oturum yerelde
    /// bitmişken yazılır: mod Off'ta (bugün her lisans) oturum <c>local</c> kalır ve bugünkü gibi düzenlenir; mod
    /// On'a geçişte §8.4 adım 3 çevirir. Yerelde hâlâ açık (aktif) oturum (force-end, §5.4): yalnız
    /// <c>ReleasedAt</c> saklanır — <c>EndedAt</c> ve <c>Authority</c>'ye dokunulmaz, <c>GetActive</c> onu
    /// döndürmeye devam eder; canlı yayın kesilmez (M18).</item>
    /// <item><c>ignored: session-owned-elsewhere</c> (devir yok): iz (<see cref="SalesConflictLog"/>, yalnız kimlik)
    /// ve onay. Son durum: oturum yeniden gönderilmez ve hiçbir zaman <c>server</c> olmaz — devralamayan geri
    /// yüklenmiş kopya "devredildi" sanmaz.</item>
    /// <item>Bitmiş ama <c>ReleasedAt</c> yok: oturum bekletilir (<see cref="SessionAckOutcome.Held"/>) — onay
    /// YAZILMAZ, kuyrukta kalır, sonraki turda yeniden gider (M2: geri alma penceresinde kapanış kaybolmaz).</item>
    /// <item>Aktif oturum, devir yok: onay.</item>
    /// </list>
    /// </summary>
    public SessionAckOutcome ApplyRelease(string id, long revision, SessionPushResult result, long now)
    {
        if (result.Protocol != SessionPushResult.ProtocolV2)
        {
            using var legacy = _factory.Open();
            return AckIfUnchanged(legacy, null, id, now, revision) ? SessionAckOutcome.Synced : SessionAckOutcome.Stale;
        }

        if (result.ReleasedAt is { } releasedAt)
        {
            using var scope = SyncApplyScope.Begin(_factory);
            scope.Connection.Execute(
                @"UPDATE StreamSession
                  SET ReleasedAt = @releasedAt,
                      Authority = CASE
                          WHEN EndedAt IS NOT NULL
                               AND COALESCE((SELECT Mode FROM SalesSyncState WHERE Id = 1), 0) = 1
                          THEN 'server' ELSE Authority END
                  WHERE Id = @id",
                new { id, releasedAt }, scope.Transaction);
            var acked = AckIfUnchanged(scope.Connection, scope.Transaction, id, now, revision);
            scope.Commit();
            return acked ? SessionAckOutcome.Released : SessionAckOutcome.Stale;
        }

        if (result.Status == SessionPushResult.StatusIgnored && result.Reason == SalesConflictLog.SessionOwnedElsewhere)
        {
            using var write = DbWrite.Begin(_factory);
            SalesConflictLog.Insert(write.Connection, write.Transaction, now, null,
                SalesConflictLog.SessionOwnedElsewhere, SalesConflictLog.SessionReason(id));
            var acked = AckIfUnchanged(write.Connection, write.Transaction, id, now, revision);
            write.Commit();
            return acked ? SessionAckOutcome.OwnedElsewhere : SessionAckOutcome.Stale;
        }

        // Devir yok. Karar gönderilen satıra göre: Revision tutmuyorsa satır uçuşta değişti (ör. yayın bu arada
        // bitti) — onaylanmaz, güncel hâliyle yeniden gider ve "gönderilen kayıt" sayılır.
        using var conn = _factory.Open();
        var state = conn.QuerySingleOrDefault<AckState>(
            "SELECT EndedAt, Revision FROM StreamSession WHERE Id = @id", new { id });
        if (state is null || state.Revision != revision) return SessionAckOutcome.Stale;
        if (state.EndedAt is not null) return SessionAckOutcome.Held;
        return AckIfUnchanged(conn, null, id, now, revision) ? SessionAckOutcome.Synced : SessionAckOutcome.Stale;
    }

    private sealed class AckState
    {
        public long? EndedAt { get; init; }
        public long Revision { get; init; }
    }

    /// <summary>F05 karşılaştır-ve-yaz onayı (<see cref="MarkSynced"/> ile aynı koşul), çağıranın işleminde.
    /// <c>SyncedAt</c> hiçbir tetikleyici listesinde değil.</summary>
    private static bool AckIfUnchanged(IDbConnection conn, IDbTransaction? tx, string id, long now, long revision)
        => conn.Execute("UPDATE StreamSession SET SyncedAt = @now WHERE Id = @id AND Revision = @revision",
            new { id, now, revision }, tx) == 1;

    /// <summary>Bu bilgisayarın canlı yayını: açık (EndedAt NULL) ve bu bilgisayarın yazdığı
    /// (Authority = 'local') en yeni oturum. Authority koşulu savunma katmanıdır (§5.9): uzak oturum
    /// hiçbir zaman EndedAt NULL olarak inmez, başka bilgisayarın canlı yayını ayrı tabloda durur
    /// (RemoteLiveSession, M5). 'server' bir satır yine de "aktif" sayılsaydı sohbet etiketi o
    /// oturuma yazılmaya çalışılır, 049'un ABORT tetikleyicisi canlı yolu kırardı.</summary>
    public StreamSession? GetActive()
    {
        using var conn = _factory.Open();
        var row = conn.QueryFirstOrDefault<Row>(
            $"SELECT {Columns} " +
            "FROM StreamSession WHERE EndedAt IS NULL AND Authority = 'local' ORDER BY StartedAt DESC LIMIT 1");
        return row is null ? null : Map(row);
    }

    public StreamSession? GetById(string id)
    {
        using var conn = _factory.Open();
        var row = conn.QueryFirstOrDefault<Row>(
            $"SELECT {Columns} " +
            "FROM StreamSession WHERE Id=@id", new { id });
        return row is null ? null : Map(row);
    }

    /// <summary>Phase 4g: en son tamamlanmış (EndedAt dolu) session'ı döndürür.</summary>
    public StreamSession? GetLatestEnded()
    {
        using var conn = _factory.Open();
        var row = conn.QueryFirstOrDefault<Row>(
            $@"SELECT {Columns}
              FROM StreamSession
              WHERE EndedAt IS NOT NULL
              ORDER BY EndedAt DESC
              LIMIT 1");
        return row is null ? null : Map(row);
    }

    public System.Collections.Generic.IReadOnlyList<Sessions.StreamSession> GetAllEnded(int limit)
    {
        using var conn = _factory.Open();
        var rows = Dapper.SqlMapper.Query<Row>(conn,
            $@"SELECT {Columns}
              FROM StreamSession
              WHERE EndedAt IS NOT NULL
              ORDER BY StartedAt DESC
              LIMIT @limit",
            new { limit }).ToList();
        return rows.Select(Map).ToList();
    }

    private static StreamSession Map(Row r) => new(
        r.Id, r.Title, r.StartedAt, r.EndedAt,
        JsonSerializer.Deserialize<string[]>(r.Platforms ?? "[]") ?? System.Array.Empty<string>(),
        r.Notes,
        r.SyncedAt,
        r.Revision,
        r.ReleasedAt,
        r.Authority,
        r.Origin,
        r.OwnerMachineName);

    private sealed class Row
    {
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
        public string Origin { get; init; } = StreamSessionOrigin.Local;
        public string? OwnerMachineName { get; init; }
    }
}
