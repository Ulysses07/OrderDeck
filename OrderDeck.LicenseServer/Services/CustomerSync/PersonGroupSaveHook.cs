using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
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
///   <item><b>Silinmiş üyenin numara izlemesi (K3, M11):</b> canlı asıl bir üye g'den ayrıldıysa (GroupId'si ordinal
///   değişti → hedef yeni numara; ya da kopyaya döndü → hedef asıl kaydın numarası) ve bu kayıttan sonra g'de canlı
///   asıl üye kalmadıysa (veritabanı + izlenen değişiklikler, ordinal), g'deki silinmiş asıl üyeler hedefe taşınır,
///   damgalı (<see cref="CustomerGroupUnion.StampAt"/>). Hedef boşsa (Ayır; ya da son canlı üye grupsuz bir asıl kayda
///   kopya oldu) silinmiş üye g'de tek kalır (K3'ün istisnası, sahip kararı bekliyor, §10); ayrılanlar farklı numaralara
///   gittiyse de kalır ve günlüğe yazılır (verify'ın "grubu dağılmış silinmiş satır" kalemi hepsini görür). Taşıma
///   yalnız sunucudadır, WPF'te silinmiş satırın numarası değişmez: sunucu grubunu istemcinin üye listesiyle
///   karşılaştıran denetimler yalnız canlı üyeleri karşılaştırır (§4.0-3, §15-7). KVKK silmesi ayrılma değildir.
///   Taşıma da bir GroupId değişimidir — silinmiş üyenin bakiye jetonu 1. maddeyle ilerler; bu yüzden önce bu madde
///   koşar.</item>
/// </list>
/// Sunucuda GroupId'ye yazan dört yer de — senkron, <c>group-customers</c>/<c>--at-twins</c>, kimlik birleşmesi ve bu
/// kancanın kendi numara izlemesi (2. madde; izlenen girdi üzerinden, 1. madde onu görür) — izlenen nesnelerle yazar; ham
/// SQL / ExecuteUpdate yazımını <c>GroupIdWriteConventionTests</c> yasaklar.
///
/// <para><b>Numara izlemesinin hakemi yok</b> (§4.13, §5.5). "Bu kayıttan sonra g'de canlı asıl üye kaldı mı" kararı
/// <c>base.SaveChanges</c>'ten önce, kilitsiz okunur: senkronda açık işlem yok, işlerin işlemi READ COMMITTED. 1. maddenin
/// bakiye jetonu yalnız bakiye satırının yazımını hakemler, bu kararı değil. Kalan pencere: g'nin kalan canlı üyelerini
/// eşzamanlı iki kayıt ayrı ayrı ayırırsa (iki PC'nin senkronu; senkron ile bir iş) ikisi de okumasını öbürünün
/// UPDATE'inden önce yapabilir. Her biri öbür üyeyi g'de canlı görür, hiçbiri silinmiş üyeyi taşımaz: silinmiş üye g'de
/// canlı üyesiz kalır ve kişinin havuzundan düşer (K3 o kişide bozulur; hiçbir sıralı yürütme bu sonucu vermez). Kanca
/// bunu görmez, günlüğe yazmaz; para silinmiş üyenin satırında kalır, <c>verify</c>'ın §7.3-4 "grubunda canlı asıl üye
/// kalmamış" kalemi sayar (Ayır'ın sonucundan ayırt edilemez). Ters yön — son canlı üye ayrılırken eşzamanlı bir kayıt
/// g'ye canlı üye ekler, silinmiş üye hedefe gider — "önce ayrılış, sonra katılma" sırasının sonucudur; kural bozulmaz.
/// Kalıcı hakem (numara başına <c>sp_getapplock</c>) senkron yolunda açık işlem ister: sahip kararı (§10 açık
/// kalanlar).</para>
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
        await FollowPurgedMembersAsync(db, sync, ct);
        await TouchBalancesAsync(db, sync, ct);
    }

    /// <summary>Bu kayıtta canlı asıl bir üyenin <see cref="From"/> numarasından ayrılışı; <see cref="To"/> = gittiği
    /// numara (gruplu değilse null).</summary>
    private sealed record Departure(Guid LicenseId, string From, string? To);

    private static async Task FollowPurgedMembersAsync(LicenseDbContext db, bool sync, CancellationToken ct)
    {
        // Tek değişiklik algılaması: bu listeden sonra yalnız kancanın kendi yükledikleri izleyiciye eklenir.
        var entries = db.ChangeTracker.Entries<WpfCustomerProjection>().ToList();
        var departures = new List<Departure>();
        foreach (var entry in entries.Where(e => e.State == EntityState.Modified))
        {
            var from = entry.Property(p => p.GroupId).OriginalValue;
            var wasLive = entry.Property(p => p.MergedIntoId).OriginalValue is null
                          && entry.Property(p => p.PurgedAt).OriginalValue is null;
            if (!wasLive || !PersonMembership.IsGrouped(from)) continue;

            var row = entry.Entity;
            if (row.PurgedAt is not null) continue; // KVKK silmesi: satır numarasında silinmiş üye olarak kalır
            if (row.MergedIntoId is { } canonicalId)
                departures.Add(new Departure(
                    row.LicenseId, from!, await GroupOfAsync(db, entries, row.LicenseId, canonicalId, sync, ct)));
            else if (!string.Equals(row.GroupId, from, StringComparison.Ordinal))
                departures.Add(new Departure(row.LicenseId, from!, row.GroupId));
        }
        if (departures.Count == 0) return;

        var now = DateTimeOffset.UtcNow;
        foreach (var license in departures.GroupBy(d => d.LicenseId))
        {
            var origins = license.Select(d => d.From).Distinct(StringComparer.Ordinal).ToList();
            var pool = await RowsAfterSaveAsync(db, entries, license.Key, origins, sync, ct);
            foreach (var origin in license.GroupBy(d => d.From, StringComparer.Ordinal))
            {
                var group = origin.Key;
                var members = pool.Where(p => string.Equals(p.GroupId, group, StringComparison.Ordinal)).ToList();
                // Numarada canlı üye kaldı. Kilitsiz okuma: eşzamanlı başka bir kaydın ayırdığı üye burada hâlâ canlı
                // görünebilir (hakemsiz pencere, sınıf yorumu).
                if (members.Any(p => p.PurgedAt is null)) continue;
                var purged = members.Where(p => p.PurgedAt is not null).ToList();
                if (purged.Count == 0) continue;

                var targets = origin.Select(d => PersonMembership.IsGrouped(d.To) ? d.To : null).ToList();
                var target = targets[0];
                if (targets.Any(t => !string.Equals(t, target, StringComparison.Ordinal)))
                {
                    Logger(db).LogWarning(
                        "Ortak bakiye: numarası boşalan grubun ayrılanları farklı numaralara gitti; {Count} silinmiş üye eski numarada kaldı (lisans {LicenseId})",
                        purged.Count, license.Key);
                    continue;
                }
                // Ayır ya da grupsuz asıl kayda kopya dönüşü: silinmiş üye eski numarada tek kalır (K3 istisnası, §10).
                if (target is null) continue;
                // Kopyaya dönen son üyenin asıl kaydı aynı numaradaysa (o da silinmiş) gidilecek yer yok.
                if (string.Equals(target, group, StringComparison.Ordinal)) continue;

                foreach (var p in purged)
                {
                    // Girdi üzerinden yazılır: değişiklik algılamasına kalmadan "değişti" işaretlenir, 1. madde ve
                    // SaveChanges onu görür.
                    var moved = db.Entry(p);
                    moved.Property(x => x.GroupIdChangedAt).CurrentValue =
                        CustomerGroupUnion.StampAt(now, p.GroupIdChangedAt);
                    moved.Property(x => x.GroupId).CurrentValue = target;
                }
            }
        }
    }

    /// <summary>
    /// Kayıttan SONRA numarası <paramref name="groups"/>'tan biri olabilecek asıl kayıtlar (eşitliği çağıran ordinal
    /// karşılaştırır): veritabanındaki adaylar — SQL yalnız daraltır, sondaki boşluğu yutar — izlenerek yüklenir; izlenen
    /// örnekler kimlik çözümüyle bellekteki (kaydedilecek) değerleriyle döner. Veritabanında başka numarada olup bu
    /// kayıtta gelen ya da yeni eklenen izlenen satırlar da eklenir. Silinecek satır ve kopyaya dönen satır süzülür.
    /// </summary>
    private static async Task<List<WpfCustomerProjection>> RowsAfterSaveAsync(
        LicenseDbContext db, IReadOnlyList<EntityEntry<WpfCustomerProjection>> entries, Guid licenseId,
        List<string> groups, bool sync, CancellationToken ct)
    {
        // AsTracking: bağlamın varsayılanı NoTracking olsa da kimlik çözümü izlenen örneği döndürmeli (aksi hâlde
        // ayrılan üye veritabanındaki eski numarasıyla "kaldı" sayılırdı) ve taşınacak silinmiş üye izlenmeli.
        var query = db.WpfCustomerProjections.AsTracking()
            .Where(p => p.LicenseId == licenseId && p.GroupId != null && groups.Contains(p.GroupId));
        var fromDb = sync ? query.ToList() : await query.ToListAsync(ct);
        var deleted = entries.Where(e => e.State == EntityState.Deleted)
            .Select(e => e.Entity).ToHashSet<WpfCustomerProjection>(ReferenceEqualityComparer.Instance);
        return fromDb
            .Concat(entries.Select(e => e.Entity))
            .Distinct<WpfCustomerProjection>(ReferenceEqualityComparer.Instance)
            .Where(p => !deleted.Contains(p) && p.LicenseId == licenseId && p.MergedIntoId is null)
            .ToList();
    }

    /// <summary>Kopyanın asıl kaydının bu kayıttaki numarası: izleniyorsa bellekteki, değilse veritabanındaki.</summary>
    private static async Task<string?> GroupOfAsync(
        LicenseDbContext db, IReadOnlyList<EntityEntry<WpfCustomerProjection>> entries, Guid licenseId, Guid id,
        bool sync, CancellationToken ct)
    {
        var tracked = entries.FirstOrDefault(e => e.Entity.Id == id);
        if (tracked is not null) return tracked.Entity.GroupId;
        var query = db.WpfCustomerProjections.IgnoreQueryFilters().AsNoTracking()
            .Where(p => p.LicenseId == licenseId && p.Id == id)
            .Select(p => p.GroupId);
        return sync ? query.FirstOrDefault() : await query.FirstOrDefaultAsync(ct);
    }

    private static ILogger Logger(LicenseDbContext db) =>
        (db.GetInfrastructure().GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance)
            .CreateLogger(typeof(PersonGroupSaveHook).FullName!);

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
