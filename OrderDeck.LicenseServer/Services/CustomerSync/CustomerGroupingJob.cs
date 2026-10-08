using System.Globalization;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;

namespace OrderDeck.LicenseServer.Services.CustomerSync;

/// <summary>
/// Bir kerelik: yayıncının elle seçtiği (alıcı satırı, kayıtlı satır) çiftlerini
/// sunucuda aynı kişi grubuna (<see cref="WpfCustomerProjection.GroupId"/>)
/// DAMGALI olarak koyar; her bilgisayar değişiklik akışıyla benimser (damgalı
/// sunucu değeri damgasız yerel değeri yener). 2026-10-08: yayıncı farklı
/// platformlarda ya da yazım hatasıyla iki ayrı satırda kalmış 75 kişiyi elle
/// eşledi. Neden ŞİMDİ güvenli: bilgisayarlardaki eski (damgasız) grupların
/// numarası göç 046 ile üyelerden türetilir oldu — her bilgisayarda aynı — ve
/// her bilgisayar onları gönderdi; sunucudaki 570 damgasız grubun 570'i
/// bilgisayarlarınkiyle aynı. Buradaki damgalı numara bilgisayarların zaten
/// tanıdığı gruba düşer, kimseyi yerel grubundan koparmaz. Kuru çalıştırma
/// hiçbir şey yazmaz; ikisi de yalnız SAYI raporlar (kişisel veri yok).
///
/// <para><b>Çözümleme:</b> çiftteki her Id lisans içinde ASIL kayda çözülür.
/// Kopyalar varsayılan sorgulardan gizli (<c>LicenseDbContext</c>'teki
/// <c>MergedIntoId == null</c> süzgeci); bu yüzden süzgeçsiz okunur ve
/// <see cref="WpfCustomerProjection.MergedIntoId"/> en çok <see cref="MaxHops"/>
/// adım izlenir — daha uzunu çözülmemiş sayılır. Satırı yok, başka lisansın ya
/// da asıl kaydı silinmiş (<see cref="WpfCustomerProjection.PurgedAt"/>) olan
/// çift ÇÖZÜLEMEYEN sayılır ve atlanır: KVKK ile silinmiş kişiyi gruplamak
/// silinmiş bir kaydı yeniden yaşatmaktır.</para>
///
/// <para><b>Düğüm ve bileşen:</b> asıl kaydın düğümü kırpılmış, boş olmayan
/// grup numarasıdır; grubu yoksa satırın kendisi. Her çözülmüş çift alıcı
/// düğümünü kayıtlı düğümüne bağlar (birleşim-bul). İki ucu zaten aynı düğümde
/// olan çift "zaten birlikte" sayılır. İki ya da daha çok düğümlü her bileşen
/// tek gruba iner.</para>
///
/// <para><b>Hedef numara:</b> KAYITLI taraftan ulaşılan grup numaralarının
/// ordinal en küçüğü; kayıtlı tarafta grup yoksa bileşendeki numaraların en
/// küçüğü; hiç numara yoksa yeni bir numara (Guid "N"). Kayıtlı tarafın grubu
/// formdan gelir, her bilgisayarda aynı numarayla zaten durur ve çoğunlukla
/// kişinin öbür platform satırlarını taşır: alıcı tarafı oraya taşınır — az
/// satır değişir, form grubu bozulmaz. Ordinal en küçük: yeniden koşu aynı
/// numarayı seçer. Bileşen birden çok kayıtlı düğümü birleştiriyorsa (iki ayrı
/// kayıtlı kişi tek kişi oluyor) ayrıca sayılır — operatörün gözden geçirmesi
/// için.</para>
///
/// <para><b>Değişen satırlar:</b> lisansın asıl (MergedIntoId null), silinmemiş
/// satırlarından grup numarası bileşenin numaralarından biri olup hedefle
/// BİREBİR aynı olmayanlar ve grubu olmayan düğüm satırları: GroupId = hedef,
/// GroupIdChangedAt = koşunun anı (koşu başına tek "şimdi", UTC). Zaten
/// hedefte olan satıra dokunulmaz, damgası da değişmez — bilgisayarlarda zaten
/// o numara var; gereksiz damga yalnız yazım dalgası olurdu. Damga ancak
/// satırınki koşunun anından eski değilse (saati ileri bir bilgisayarın damgası)
/// onun 1 ms sonrası olur (<see cref="StampAt"/>). Taşıma ve kara liste yazımı
/// "@" ikizi birleştirmesiyle ORTAK (<see cref="CustomerGroupUnion"/>).</para>
///
/// <para><b>Kara liste yayılımı</b> masaüstündeki elle birleştirmenin
/// (<c>CustomerRepository.PropagateGroupBlacklist</c>, <c>formAt</c> boş kolu)
/// aynası (<see cref="CustomerGroupUnion.PropagateBlacklist"/>): taşımadan sonra
/// hedef grupta kara listede biri varsa kaynak = en yeni BlacklistedAt'li (boşlar
/// sona), eşitlikte küçük Id ("N" metni — masaüstüyle aynı bozma: herkes aynı
/// kaynağı seçsin). Grubun SİLİNMİŞ asıl kayıtları da kaynak olabilir (KVKK
/// boşaltması kara listeyi korur, masaüstü de onları seçer) ama hedef olmaz.
/// Kara listede olmayan her silinmemiş üye kaynağın sebebi ve tarihiyle (tarih
/// yoksa koşunun anı) kara listeye girer, BlacklistChangedAt = koşunun anı.
/// Gruplama kara listeden kaçışın yolu olmasın.</para>
///
/// <para><b>UpdatedAt'e dokunulmaz</b> (birleştirme işiyle aynı): değişiklik
/// akışı rowversion (ChangeSeq) kullanır, satırın yazılması yeter.</para>
///
/// <para>Bileşen başına ayrı işlem. Eşzamanlılık çakışması (arada bir KVKK
/// silmesi — <see cref="WpfCustomerProjection.PurgedAt"/> eşzamanlılık jetonu)
/// ya da başka bir veritabanı hatası o bileşeni geri alır ve sayılır; günlüğe
/// yalnız istisna tipi ve SQL hata numarası yazılır (iletisi kişisel veri
/// taşıyabilir). İş sıradaki bileşene geçer; yeniden koşmak idempotent —
/// biten bileşenlerin çiftleri artık "zaten birlikte", ikinci uygulama hiçbir
/// satırı değiştirmez. Rapor sayıları (FailedComponents hariç) uygulamada da
/// "bulunan" sayılardır: atlanan bileşenin satırları da içlerindedir.</para>
/// </summary>
public sealed class CustomerGroupingJob
{
    /// <summary>Kopya → asıl kayıt en çok bu kadar adım izlenir; daha uzun
    /// zincir (bir yarışın izi) çözülmemiş sayılır.</summary>
    public const int MaxHops = 3;

    private readonly LicenseDbContext _db;
    private readonly ILogger<CustomerGroupingJob> _log;

    public CustomerGroupingJob(LicenseDbContext db, ILogger<CustomerGroupingJob>? log = null)
    {
        _db = db;
        _log = log ?? NullLogger<CustomerGroupingJob>.Instance;
    }

    /// <summary>Eşleşme dosyasının bir satırı: alıcı satırının ve kayıtlı
    /// satırın projeksiyon Id'leri (kopya Id'si de olabilir — asıl kayda
    /// çözülür).</summary>
    public readonly record struct Pair(Guid BuyerId, Guid RegisteredId);

    /// <param name="PairsRead">Dosyadaki çift.</param>
    /// <param name="PairsResolved">İki ucu da asıl kayda çözülen çift.</param>
    /// <param name="PairsUnresolved">En az bir ucu yok, başka lisansın, silinmiş
    /// ya da <see cref="MaxHops"/>'tan uzun zincirde — atlandı.</param>
    /// <param name="AlreadyTogether">İki ucu zaten aynı düğümde (aynı grup ya da
    /// aynı satır) olan çözülmüş çift.</param>
    /// <param name="Components">İki ya da daha çok düğümlü, tek gruba inecek
    /// bileşen.</param>
    /// <param name="RowsToChange">Grup numarası hedefe değişecek satır.</param>
    /// <param name="ComponentsJoiningRegistered">Kayıtlı taraftan iki ya da daha
    /// çok düğümü birleştiren bileşen (iki ayrı kayıtlı kişi tek kişi oluyor).</param>
    /// <param name="BlacklistPropagations">Kara listeye yayılımla girecek satır.</param>
    /// <param name="FailedComponents">Geri alınıp atlanan bileşen (eşzamanlılık
    /// ya da başka bir veritabanı hatası); yeniden koşu tamamlar.</param>
    public sealed record Report(
        int PairsRead, int PairsResolved, int PairsUnresolved, int AlreadyTogether, int Components,
        int RowsToChange, int ComponentsJoiningRegistered, int BlacklistPropagations, int FailedComponents);

    /// <inheritdoc cref="CustomerGroupUnion.StampAt"/>
    public static DateTimeOffset StampAt(DateTimeOffset now, DateTimeOffset? current)
        => CustomerGroupUnion.StampAt(now, current);

    public async Task<Report> RunAsync(Guid licenseId, IReadOnlyList<Pair> pairs, bool apply, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow; // koşu başına tek "şimdi"
        var canonicalOf = await ResolveAsync(licenseId, pairs.SelectMany(p => new[] { p.BuyerId, p.RegisteredId }), ct);

        var nodes = new UnionFind();
        var registeredNodes = new HashSet<Node>();
        var rowNodes = new Dictionary<Guid, Info>();
        int resolved = 0, unresolved = 0, together = 0;
        foreach (var pair in pairs)
        {
            if (!canonicalOf.TryGetValue(pair.BuyerId, out var buyer)
                || !canonicalOf.TryGetValue(pair.RegisteredId, out var registered))
            {
                unresolved++;
                continue;
            }
            resolved++;
            var b = NodeOf(buyer);
            var r = NodeOf(registered);
            if (b == r) together++;
            if (b.GroupId is null) rowNodes[buyer.Id] = buyer;
            if (r.GroupId is null) rowNodes[registered.Id] = registered;
            registeredNodes.Add(r);
            nodes.Union(b, r);
        }

        var components = nodes.Components().Where(c => c.Count >= 2).ToList();
        // Grup üyeleri: lisansın grubu olan asıl kayıtları — silinmişler DAHİL
        // (yalnız kara liste kaynağı olarak). Eşleme bellekte, ORDİNAL: kolonun
        // SQL karşılaştırması (varsayılan collation) harf ve sondaki boşluk
        // farkını yok sayar, bilgisayarlar ise numarayı bayt bayt karşılaştırır.
        var byGroup = components.Count == 0
            ? Enumerable.Empty<Info>().ToLookup(i => "", StringComparer.Ordinal)
            : (await _db.WpfCustomerProjections.AsNoTracking()
                    .Where(p => p.LicenseId == licenseId && p.MergedIntoId == null && p.GroupId != null)
                    .Select(p => new Info(p.Id, p.LicenseId, p.MergedIntoId, p.PurgedAt, p.GroupId, p.IsBlacklisted, p.BlacklistedAt))
                    .ToListAsync(ct))
                .Where(i => CustomerGroupUnion.KeyOf(i.GroupId) is not null)
                .ToLookup(i => CustomerGroupUnion.KeyOf(i.GroupId)!, StringComparer.Ordinal);

        var plans = components.Select(c => PlanOf(c, registeredNodes, rowNodes, byGroup)).ToList();
        var failed = 0;
        if (apply)
        {
            foreach (var plan in plans)
            {
                try
                {
                    await ApplyAsync(licenseId, plan, now, ct);
                }
                catch (DbUpdateException ex) // DbUpdateConcurrencyException dahil
                {
                    failed++;
                    Skipped(licenseId, ex);
                }
                catch (SqlException ex)
                {
                    failed++;
                    Skipped(licenseId, ex);
                }
                finally
                {
                    _db.ChangeTracker.Clear();
                }
            }
        }

        return new Report(
            pairs.Count, resolved, unresolved, together, plans.Count,
            plans.Sum(p => p.Move.Count), plans.Count(p => p.JoinsRegistered),
            plans.Sum(p => p.Propagations), failed);
    }

    /// <summary>Bir bileşenin hedefi, değişecek satırları ve kara liste
    /// yayılımı — kuru çalıştırma ile uygulama aynı planı sayar.</summary>
    private static Plan PlanOf(
        List<Node> component, HashSet<Node> registeredNodes, Dictionary<Guid, Info> rowNodes,
        ILookup<string, Info> byGroup)
    {
        var groupIds = component.Where(n => n.GroupId is not null).Select(n => n.GroupId!)
            .Order(StringComparer.Ordinal).ToList();
        var target = component.Where(n => n.GroupId is not null && registeredNodes.Contains(n)).Select(n => n.GroupId!)
                         .Order(StringComparer.Ordinal).FirstOrDefault()
                     ?? groupIds.FirstOrDefault()
                     ?? Guid.NewGuid().ToString("N");
        var groupRows = groupIds.SelectMany(g => byGroup[g]).ToList();
        // Taşınacak/yayılım alacak üyeler silinmemiş olanlar; silinmiş üye yalnız
        // kara liste kaynağı (bkz. CustomerGroupUnion.PropagateBlacklist).
        var members = groupRows.Where(i => i.PurgedAt is null)
            .Concat(component.Where(n => n.GroupId is null).Select(n => rowNodes[n.RowId]))
            .DistinctBy(i => i.Id)
            .ToList();
        var purgedSources = groupRows.Where(i => i.PurgedAt is not null && i.IsBlacklisted).ToList();
        var move = members.Where(m => !string.Equals(m.GroupId, target, StringComparison.Ordinal)).ToList();
        var propagations = members.Any(m => m.IsBlacklisted) || purgedSources.Count > 0
            ? members.Count(m => !m.IsBlacklisted)
            : 0;
        return new Plan(
            target, members.Concat(purgedSources).Select(m => m.Id).ToList(), move, propagations,
            component.Count(registeredNodes.Contains) >= 2);
    }

    /// <summary>Planı bileşenin kendi işleminde uygular. Satırlar işlem içinde
    /// yeniden okunur (izlenen örnekler); bu arada kopyaya dönen satırı süzgeç
    /// gizler, silinmiş satıra yazılmaz (yalnız kara liste kaynağı olabilir).
    /// Hata çağırana çıkar — işlem kapsamdan çıkarken geri alınır.</summary>
    private async Task ApplyAsync(Guid licenseId, Plan plan, DateTimeOffset now, CancellationToken ct)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        var ids = plan.Members;
        var rows = await _db.WpfCustomerProjections
            .Where(p => p.LicenseId == licenseId && p.MergedIntoId == null && ids.Contains(p.Id))
            .ToListAsync(ct);

        CustomerGroupUnion.MoveToGroup(rows, plan.Target, now);
        CustomerGroupUnion.PropagateBlacklist(rows, now);

        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>Çiftlerdeki Id'leri lisans içindeki asıl kayda çözer (bkz.
    /// sınıf dokümanı); çözülemeyen Id sözlükte yoktur. Kopya zinciri katman
    /// katman okunur: her turda bir önceki turun yönlendirme hedefleri.</summary>
    private async Task<Dictionary<Guid, Info>> ResolveAsync(Guid licenseId, IEnumerable<Guid> ids, CancellationToken ct)
    {
        var wanted = ids.Distinct().ToList();
        var known = new Dictionary<Guid, Info>();
        var frontier = wanted;
        for (var hop = 0; hop <= MaxHops && frontier.Count > 0; hop++)
        {
            var batch = frontier.Where(id => !known.ContainsKey(id)).Distinct().ToList();
            if (batch.Count == 0) break;
            // Kopyalar varsayılan süzgeçte gizli; başka lisansın satırı hiç okunmaz.
            var rows = await _db.WpfCustomerProjections.IgnoreQueryFilters().AsNoTracking()
                .Where(p => p.LicenseId == licenseId && batch.Contains(p.Id))
                .Select(p => new Info(p.Id, p.LicenseId, p.MergedIntoId, p.PurgedAt, p.GroupId, p.IsBlacklisted, p.BlacklistedAt))
                .ToListAsync(ct);
            foreach (var row in rows) known[row.Id] = row;
            frontier = rows.Where(r => r.MergedIntoId is not null).Select(r => r.MergedIntoId!.Value).ToList();
        }

        var result = new Dictionary<Guid, Info>();
        foreach (var id in wanted)
            if (Resolve(id) is { } canonical)
                result[id] = canonical;
        return result;

        Info? Resolve(Guid id)
        {
            var current = id;
            for (var hop = 0; ; hop++)
            {
                if (!known.TryGetValue(current, out var row)) return null; // yok ya da başka lisansın
                if (row.MergedIntoId is null) return row.PurgedAt is null ? row : null;
                if (hop == MaxHops) return null;
                current = row.MergedIntoId.Value;
            }
        }
    }

    private static Node NodeOf(Info row)
        => CustomerGroupUnion.KeyOf(row.GroupId) is { } key ? new Node(key, Guid.Empty) : new Node(null, row.Id);

    private void Skipped(Guid licenseId, Exception ex)
        => _log.LogWarning(
            "Müşteri gruplama: bileşen geri alındı ve atlandı (lisans {LicenseId}): {ExceptionType}, SQL hata {SqlError}",
            licenseId, ex.GetType().Name,
            CustomerIdentityMergeJob.SqlErrorOf(ex)?.ToString(CultureInfo.InvariantCulture) ?? "yok");

    /// <summary>Planlama için okunan satır özeti.</summary>
    private sealed record Info(
        Guid Id, Guid LicenseId, Guid? MergedIntoId, DateTimeOffset? PurgedAt, string? GroupId,
        bool IsBlacklisted, DateTimeOffset? BlacklistedAt);

    /// <summary>Düğüm: grup numarası (kırpılmış) ya da grubu olmayan satırın
    /// kendisi (<see cref="RowId"/>).</summary>
    private readonly record struct Node(string? GroupId, Guid RowId);

    /// <param name="Members">Taşımadan sonra hedef grupta olacak bütün satırlar
    /// (zaten hedefte olanlar dahil) ve grubun kara listedeki silinmiş asıl
    /// kayıtları — kara liste yayılımının kapsamı.</param>
    /// <param name="Move">Grup numarası hedefe değişecek satırlar.</param>
    private sealed record Plan(
        string Target, List<Guid> Members, IReadOnlyList<Info> Move, int Propagations, bool JoinsRegistered);

    /// <summary>Düğümler üzerinde birleşim-bul. Bileşenler düğümlerin ilk
    /// görüldüğü sırayla döner: uygulama sırası dosyanın sırasını izlesin.</summary>
    private sealed class UnionFind
    {
        private readonly Dictionary<Node, Node> _parent = new();
        private readonly List<Node> _order = new();

        public Node Find(Node node)
        {
            if (!_parent.TryGetValue(node, out var parent))
            {
                _parent[node] = node;
                _order.Add(node);
                return node;
            }
            if (parent == node) return node;
            var root = Find(parent);
            _parent[node] = root;
            return root;
        }

        public void Union(Node a, Node b)
        {
            var ra = Find(a);
            var rb = Find(b);
            if (ra != rb) _parent[rb] = ra;
        }

        public IEnumerable<List<Node>> Components() => _order.GroupBy(Find).Select(g => g.ToList());
    }
}
