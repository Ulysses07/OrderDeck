using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;

namespace OrderDeck.LicenseServer.Services.Sync;

/// <summary>
/// Yayının devri (release) — spec 2026-10-13 §2, §4.1. Devir, yayının birim yazıcısını sahip kopyadan sunucuya
/// geçirir: <c>ReleasedAt</c> sunucu saatiyle yazılır ve bir daha değişmez. Tek yazım yolu budur; <c>sessions/sync</c>,
/// <c>sales/backfill</c> ve onarım işi (§4.9) aynı kodu çağırır.
///
/// <para><b>Koşul veritabanında.</b> Oturum yalnız bitmişse (<c>EndedAt</c> dolu) ve henüz devredilmemişse yazılır.
/// İlişkisel sağlayıcıda bu tek bir koşullu UPDATE'tir: aynı yayını aynı anda devretmeye çalışan iki istekten ikincisi
/// birincinin işlemini bekler, sonra satırı devredilmiş bulur ve yazmaz — devir anı ilk yazandır.</para>
///
/// <para><b>İşlem.</b> Çağıranın açık işlemi varsa onun içinde koşar (uç, kendi yazımlarıyla devri tek işlemde
/// tutar); yoksa kendi işlemini açar. InMemory'de ne işlem ne <c>ExecuteUpdate</c> var: aynı kural izlenen
/// varlıklara uygulanır (<see cref="SalesReplicaService"/> deseni).</para>
/// </summary>
public sealed class SessionReleaser
{
    private readonly LicenseDbContext _db;

    public SessionReleaser(LicenseDbContext db) => _db = db;

    /// <summary>Verilen oturumları devreder.</summary>
    /// <returns>Bu çağrının devrettiği oturumlar; zaten devredilmiş, bitmemiş ya da başka lisansın oturumu
    /// dönmez.</returns>
    public async Task<IReadOnlyList<Guid>> ReleaseAsync(
        Guid licenseId, IReadOnlyCollection<Guid> sessionIds, DateTimeOffset releasedAt, CancellationToken ct)
    {
        var ids = sessionIds.Distinct().ToList();
        if (ids.Count == 0) return Array.Empty<Guid>();

        if (!_db.Database.IsRelational())
            return await ReleaseTrackedAsync(licenseId, ids, releasedAt, ct);

        await using var ownTransaction = _db.Database.CurrentTransaction is null
            ? await _db.Database.BeginTransactionAsync(ct)
            : null;

        var released = new List<Guid>(ids.Count);
        foreach (var id in ids)
        {
            var updated = await _db.StreamSessions
                .Where(s => s.LicenseId == licenseId && s.Id == id && s.EndedAt != null && s.ReleasedAt == null)
                .ExecuteUpdateAsync(u => u.SetProperty(s => s.ReleasedAt, releasedAt), ct);
            if (updated > 0) released.Add(id);
        }

        if (ownTransaction is not null) await ownTransaction.CommitAsync(ct);
        return released;
    }

    private async Task<IReadOnlyList<Guid>> ReleaseTrackedAsync(
        Guid licenseId, List<Guid> ids, DateTimeOffset releasedAt, CancellationToken ct)
    {
        var sessions = await _db.StreamSessions
            .Where(s => s.LicenseId == licenseId && ids.Contains(s.Id))
            .ToListAsync(ct);

        var released = new List<Guid>(sessions.Count);
        foreach (var session in sessions.Where(s => s.EndedAt != null && s.ReleasedAt == null))
        {
            session.ReleasedAt = releasedAt;
            released.Add(session.Id);
        }

        if (released.Count > 0) await _db.SaveChangesAsync(ct);
        return released;
    }
}
