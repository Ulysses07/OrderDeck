using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;

namespace OrderDeck.LicenseServer.Services.CustomerSync;

/// <summary>
/// <c>LicenseDbContext.SaveChanges</c> kancası (ortak bakiye spec 2026-10-11 §4.11, M6, M11). Kaydetmeden önce,
/// <c>StampVersions</c>'tan önce koşar:
/// <list type="number">
///   <item><b>Bakiye jetonu dokunuşu:</b> <c>GroupId</c>'si ordinal değişmiş her izlenen projeksiyonun bakiye satırı
///   (önce izlenenlerde, yoksa tek sorguyla) "değişti" işaretlenir; jetonu <c>StampVersions</c> ilerletir. Apply
///   üyeliği okuyup bakiye satırını yükledikten sonra kaydedilen bir ayırma, apply'ın UPDATE'ini tutmaz ve plan
///   baştan kurulur. Bakiye satırı olmayan üyede dokunulacak bir şey yok: para taşımaz. Tutar değişmez.</item>
/// </list>
/// Sunucuda GroupId'ye yazan üç yer de (senkron, <c>group-customers</c>/<c>--at-twins</c>, kimlik birleşmesi) izlenen
/// nesnelerle yazar; ham SQL / ExecuteUpdate yazımını <c>GroupIdWriteConventionTests</c> yasaklar.
///
/// <para><b>Ne görür:</b> tam olarak <c>SaveChanges</c>'in yazacağını — izleyicinin durumunu. Değişiklik algılaması
/// kapalı bir bağlam (<c>LicenseReadOnlyDbContext</c>) algılamadığı değişikliği kaydetmez, kanca da görmez; bu yüzden
/// ayrı bir "salt okunur bağlam" çıkışı yok (algılamayı kapatan bir yazıcı kancadan sessizce kaçamasın). Değişmiş
/// projeksiyon yoksa hiçbir sorgu atılmaz.</para>
///
/// <para><b>sync/async ikizi:</b> gövde tek; <paramref name="sync"/> true iken sorgular eşzamanlı koşar ve dönen görev
/// tamamlanmış olur (EF'in kendi kalıbı) — senkron <c>SaveChanges</c> onu bekleyebilir.</para>
/// </summary>
internal static class PersonGroupSaveHook
{
    internal static async Task RunAsync(LicenseDbContext db, bool sync, CancellationToken ct)
    {
        await TouchBalancesAsync(db, sync, ct);
    }

    private static async Task TouchBalancesAsync(LicenseDbContext db, bool sync, CancellationToken ct)
    {
        var changed = db.ChangeTracker.Entries<WpfCustomerProjection>()
            .Where(e => e.State == EntityState.Modified && GroupChanged(e))
            .Select(e => (e.Entity.LicenseId, e.Entity.Id))
            .ToList();
        if (changed.Count == 0) return;

        foreach (var license in changed.GroupBy(c => c.LicenseId))
        {
            var ids = license.Select(c => c.Id).ToHashSet();
            var local = db.CustomerBalances.Local
                .Where(b => b.LicenseId == license.Key && ids.Contains(b.WpfCustomerId))
                .ToList();
            var missing = ids.Except(local.Select(b => b.WpfCustomerId)).ToList();
            var loaded = new List<CustomerBalance>();
            if (missing.Count > 0)
            {
                // AsTracking: bağlamın varsayılanı NoTracking olsa da (ör. SalesAudit'in bağlamı) yüklenen satır
                // izleyicide olmalı — "değişti" işareti ancak izlenen bir girdide UPDATE'e dönüşür.
                var query = db.CustomerBalances.AsTracking()
                    .Where(b => b.LicenseId == license.Key && missing.Contains(b.WpfCustomerId));
                loaded = sync ? query.ToList() : await query.ToListAsync(ct);
            }
            foreach (var balance in local.Concat(loaded))
            {
                var entry = db.Entry(balance);
                if (entry.State is EntityState.Added or EntityState.Deleted) continue;
                entry.Property(b => b.UpdatedAt).IsModified = true;
            }
        }
    }

    private static bool GroupChanged(EntityEntry<WpfCustomerProjection> entry) =>
        !string.Equals(entry.Property(p => p.GroupId).OriginalValue, entry.Entity.GroupId, StringComparison.Ordinal);
}
