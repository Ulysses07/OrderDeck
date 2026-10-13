using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;

namespace OrderDeck.LicenseServer.Services.Sync;

/// <summary>
/// Yayının devri (release) — spec 2026-10-13 §2, §4.1, DM3. Devir, yayının birim yazıcısını sahip kopyadan sunucuya
/// geçirir: <c>ReleasedAt</c> sunucu saatiyle yazılır ve bir daha değişmez; aynı işlemde yayının bütün siparişlerine
/// <c>SessionReleasedAt</c> olarak aynı an basılır ve siparişler değişiklik akışına o anda girer. Tek yazım yolu
/// budur; <c>sessions/sync</c>, <c>sales/backfill</c> ve onarım işi (§4.9) aynı kodu çağırır.
///
/// <para><b>Koşul veritabanında.</b> Oturum yalnız bitmişse (<c>EndedAt</c> dolu) ve henüz devredilmemişse yazılır.
/// İlişkisel sağlayıcıda bu tek bir koşullu UPDATE'tir: aynı yayını aynı anda devretmeye çalışan iki istekten ikincisi
/// birincinin işlemini bekler, sonra satırı devredilmiş bulur ve yazmaz — devir anı ilk yazandır, siparişleri de o
/// damgalar.</para>
///
/// <para><b>Sıra önemli.</b> Önce oturum, sonra siparişler: siparişlerin rowversion'ı (<c>ChangeSeq</c>) oturumunkinden
/// büyük olur, akış oturumu siparişlerinden önce görür (§4.4). Damga siparişin birimine dokunmaz: <c>SyncVersion</c>
/// (telde <c>Version</c>, komutların beklenen sürümü) ilerlemez; koşullu UPDATE jetonu atladığı için eşzamanlı bir
/// <c>orders/sync</c> güncellemesiyle de çakışmaz (o yol <c>SessionReleasedAt</c> yazmaz).</para>
///
/// <para><b>Kalan pencere.</b> Devirle yarışan bir sipariş eklemesi oturumu devredilmemiş okuyup damgasız
/// eklenebilir; onu onarım işi kapatır (§4.9-b, DM3).</para>
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
            if (updated == 0) continue;

            released.Add(id);
            await _db.Orders
                .Where(o => o.LicenseId == licenseId && o.SessionId == id && o.SessionReleasedAt == null)
                .ExecuteUpdateAsync(u => u.SetProperty(o => o.SessionReleasedAt, releasedAt), ct);
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
        if (released.Count == 0) return released;

        var orders = await _db.Orders
            .Where(o => o.LicenseId == licenseId
                && o.SessionId != null && released.Contains(o.SessionId.Value)
                && o.SessionReleasedAt == null)
            .ToListAsync(ct);
        foreach (var order in orders) order.SessionReleasedAt = releasedAt;

        await _db.SaveChangesAsync(ct);
        return released;
    }
}
