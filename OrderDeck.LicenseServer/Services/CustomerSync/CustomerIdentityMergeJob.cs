using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.ShopperLinking;

namespace OrderDeck.LicenseServer.Services.CustomerSync;

/// <summary>
/// Bir kerelik: aynı kişinin (platform + kimlik anahtarı) birden çok asıl
/// kaydını tek kayıtta toplar. Kuru çalıştırma hiçbir şey yazmaz.
///
/// <para><b>Gruplama</b> saklı IdentityKey ile değil bellekte hesaplanan
/// <see cref="WpfCustomerProjection.IdentityKeyOf"/> ile: göçün SQL dolgusu ya
/// da geri alınmış bir deploy'un NEWID varsayılanı saklı anahtarı bozmuş
/// olabilir; aynı kişinin satırları yine aynı gruba düşer. Hesaplanan anahtarı
/// boş (yalnız boşluk) kullanıcı adları HARİÇ — onarım işinin atlama kuralıyla
/// aynı; yoksa lisansın bütün boş adlı satırları tek kişi sanılırdı.</para>
///
/// <para><b>Asıl kayıt:</b> Shopper'ın açtığı geçici kayıt, yayıncının satırı
/// varken asıl olamaz (A5c); sonra en erken siparişi olan; hiçbirinin siparişi
/// yoksa en eski UpdatedAt; eşitlikte küçük Id ("N" metni). Projeksiyonda
/// oluşturma zamanı tutulmadığı için "en eski kayıt"ın tek dürüst ölçüsü bu.</para>
///
/// <para><b>Alanlar</b> istemci gönderimiyle AYNI birim kurallarıyla aktarılır
/// (<see cref="CustomerFieldMerge.Apply"/>). Bugünkü satırların hiçbirinde
/// damga yok; pratikte doldurma kipi: kopyalar UpdatedAt'i en yeniden eskiye
/// gezilir, asıl kayıtta boş olan alan ilk dolu değerle doldurulur (adres
/// bloğu bütün olarak; eski sürümün takma ad yedeği kullanıcı adıyla tanınır).
/// Her kopya alan kaynağıdır, iki süzgeçle: geçici kopyadan yalnız DAMGALI
/// birimler (<see cref="CustomerSyncFields.StampedOnly"/> — damgasız alanları
/// kaydolanın kendi beyanı; boş telefonu doldursa kanıt kendiliğinden geçerdi),
/// silinmiş (PurgedAt) kopyanın boşaltılmış kişisel birimleri hiç
/// (<see cref="CustomerSyncFields.WithoutScrubbedUnits"/> — oradaki "damgalı
/// boş" bilinçli silme değil). Kara liste ve iş notu gibi yayıncı kararları
/// ikisinden de kaybolmaz. Herhangi bir (geçici olmayan) kopya KVKK ile
/// silinmişse kişinin tamamı silinmiş sayılır; ilk silme tarihi açıkça en
/// erkeni. Geçici satırın silinmişliği kişiye YAYILMAZ: sahte bir hesabın KVKK
/// silmesi gerçek müşterinin asıl kaydını silmesin.</para>
///
/// <para><b>Kopya</b> silinmez: kişisel alanları boşaltılır (PurgedAt'e
/// dokunulmaz — eşzamanlılık jetonu), MergedIntoId = asıl kayıt; geçici
/// kopyanın CreatedByShopper bayrağı köken olarak kalır. Ona zaten yönlenmiş
/// kopyalar da asıl kayda yönlendirilir (zincir olmaz). Kopyaya bağlı
/// sipariş/kargo/bakiye/bağlantılar <see cref="CustomerIdentityMerger"/> ile
/// taşınır; her kopya DOĞRUDAN asıl kayda (zincirleme birleştirme tek
/// SaveChanges içinde yapılmaz — birleştiricinin bakiye araması buna dayanır).</para>
///
/// <para><b>Geçici kopyadan taşınan bağlantı</b> — AYRILMIŞ olanı dahil:
/// ShopperPurgeService ayrılmış bağlantıdan ulaştığı projeksiyonu da siler,
/// kanıtsız kalsa saldırganın KVKK silme talebi asıl kaydı silerdi — asıl
/// kaydın telefonuyla yeniden kanıt ister
/// (<see cref="WpfCustomerLinkMatcher.PhoneProves"/>); veremeyen beklemeye düşer
/// (asıl kayıt da geçiciyse hepsi) — normal kanıt akışları yeniden bağlar. Kanıt
/// grubun TÜM kopyaları işlendikten ve silme uygulandıktan SONRA, kaydedilecek
/// telefona karşı verilir: kopya döngüsünün ara durumuna karşı verilseydi sonuç
/// kopyaların işlenme sırasına bağlı kalırdı — sonradan işlenen bir kopyanın
/// damgalı telefonu kanıtı geçmiş bağlantıyı başka numaralı kayda bağlı
/// bırakırdı, silinen kişinin kaydına da (sync ucunun ReproveLinksAsync'i gibi:
/// parti bittikten sonra, şimdiki telefona karşı).</para>
///
/// <para>Grup başına ayrı işlem: eşzamanlılık çakışması (bu arada bir KVKK
/// silmesi, bakiye uygulaması, sipariş senkronu) o grubu geri alır ve sayılır;
/// iş sıradaki gruba geçer. Yeniden koşmak yalnız kalanları bulur (idempotent).
/// Rapor sayıları (FailedGroups ve LinksUnbound hariç) uygulamada da "bulunan"
/// sayılardır: atlanan grubun satırları da içlerindedir.</para>
/// </summary>
public sealed class CustomerIdentityMergeJob
{
    private readonly LicenseDbContext _db;
    private readonly CustomerIdentityMerger _merger;

    public CustomerIdentityMergeJob(LicenseDbContext db, CustomerIdentityMerger merger)
    {
        _db = db;
        _merger = merger;
    }

    /// <param name="Groups">Bulunan kopyalı kişi sayısı (kuru çalıştırmada ve
    /// uygulamada aynı anlam).</param>
    /// <param name="LinksUnbound">Geçici kopyadan taşınıp telefon kanıtını
    /// veremediği için beklemeye düşen Shopper bağlantısı (ayrılmışlar dahil);
    /// yalnız uygulamada ve yalnız kaydedilen gruplardan.</param>
    public sealed record Report(
        int Groups, int CopyRows, int OrdersToMove, int ShipmentsToMove,
        int LinksToMove, int BalancesToSum, int PurgedGroups, int FailedGroups, int LinksUnbound = 0);

    /// <summary>Projeksiyonu olan lisanslar (CLI <c>--all</c>).</summary>
    public async Task<IReadOnlyList<Guid>> LicenseIdsAsync(CancellationToken ct)
        => await _db.WpfCustomerProjections.IgnoreQueryFilters()
            .Select(p => p.LicenseId).Distinct().OrderBy(x => x).ToListAsync(ct);

    /// <summary>Kimlik anahtarı hesaplananla uyuşmayan satır sayısı — onarım
    /// işinin yüklemiyle BİREBİR (boş hesaplanan anahtar sayılmaz: iş onu asla
    /// yazmaz). Kopyalar dahil, tüm lisanslar.</summary>
    public async Task<int> CountMismatchedKeysAsync(CancellationToken ct)
    {
        var rows = await _db.WpfCustomerProjections.IgnoreQueryFilters().AsNoTracking()
            .Select(p => new { p.Username, p.IdentityKey })
            .ToListAsync(ct);
        return rows.Count(r =>
        {
            var key = WpfCustomerProjection.IdentityKeyOf(r.Username);
            return key != "" && key != r.IdentityKey;
        });
    }

    public async Task<Report> RunAsync(Guid licenseId, bool apply, CancellationToken ct)
    {
        var heads = await _db.WpfCustomerProjections.AsNoTracking()
            .Where(p => p.LicenseId == licenseId && p.MergedIntoId == null)
            .Select(p => new { p.Id, p.Platform, p.Username })
            .ToListAsync(ct);
        var groups = heads
            .Select(h => new { h.Id, Platform = h.Platform.ToLowerInvariant(), Key = WpfCustomerProjection.IdentityKeyOf(h.Username) })
            .Where(h => h.Key != "")
            .GroupBy(h => (h.Platform, h.Key))
            .Where(g => g.Count() > 1)
            .Select(g => g.Select(h => h.Id).ToList())
            .ToList();

        int copies = 0, orders = 0, shipments = 0, links = 0, balances = 0, purged = 0, failed = 0, unbound = 0;
        foreach (var ids in groups)
        {
            var rows = await _db.WpfCustomerProjections
                .Where(p => p.LicenseId == licenseId && p.MergedIntoId == null && ids.Contains(p.Id))
                .ToListAsync(ct);
            if (rows.Count < 2)
            {
                _db.ChangeTracker.Clear(); // bu arada başka bir koşu birleştirdi
                continue;
            }

            var hexes = rows.Select(r => r.Id.ToString("N")).ToList();
            var orderStats = (await _db.Orders
                    .Where(o => o.LicenseId == licenseId && hexes.Contains(o.CustomerId))
                    .GroupBy(o => o.CustomerId)
                    .Select(g => new { CustomerId = g.Key, First = g.Min(o => o.AddedAt), Count = g.Count() })
                    .ToListAsync(ct))
                .ToDictionary(x => x.CustomerId);

            var canonical = rows
                // Shopper'ın açtığı geçici kayıt asıl olamaz, yayıncının satırı varken (A5c).
                .OrderBy(r => r.CreatedByShopper ? 1 : 0)
                .ThenBy(r => orderStats.TryGetValue(r.Id.ToString("N"), out var s) ? s.First : DateTimeOffset.MaxValue)
                .ThenBy(r => r.UpdatedAt)
                .ThenBy(r => r.Id.ToString("N"), StringComparer.Ordinal)
                .First();
            var others = rows.Where(r => r.Id != canonical.Id).OrderByDescending(r => r.UpdatedAt).ToList();
            var otherIds = others.Select(r => r.Id).ToList();
            var otherHexes = others.Select(r => r.Id.ToString("N")).ToList();

            copies += others.Count;
            orders += others.Sum(r => orderStats.TryGetValue(r.Id.ToString("N"), out var s) ? s.Count : 0);
            shipments += await _db.Shipments
                .CountAsync(s => s.LicenseId == licenseId && otherHexes.Contains(s.CustomerId), ct);
            links += await _db.ShopperBroadcasterLinks
                .CountAsync(l => l.LicenseId == licenseId && l.WpfCustomerId != null && otherIds.Contains(l.WpfCustomerId.Value), ct);
            balances += await _db.CustomerBalances
                .CountAsync(b => b.LicenseId == licenseId && otherIds.Contains(b.WpfCustomerId) && b.Balance != 0m, ct);
            // Geçici (Shopper'ın açtığı) satırın silinmişliği kişiye YAYILMAZ:
            // o satır kişinin değil kaydolanın beyanı — sahte bir hesabın KVKK
            // silmesi gerçek müşterinin asıl kaydını silmesin (A5c).
            var anyPurged = rows.Any(r => r.PurgedAt is not null && !r.CreatedByShopper);
            if (anyPurged) purged++;

            if (!apply)
            {
                _db.ChangeTracker.Clear();
                continue;
            }

            try
            {
                await using var tx = await _db.Database.BeginTransactionAsync(ct);
                var now = DateTimeOffset.UtcNow;
                // Geçici kopyalardan taşınan bağlantılar (ayrılmışlar dahil):
                // kanıt aşağıda, bütün kopyalar ve silme uygulandıktan SONRA.
                var provisionalLinks = new List<ShopperBroadcasterLink>();
                foreach (var copy in others)
                {
                    // Her kopya alan kaynağıdır, iki süzgeçle: Shopper'ın açtığı
                    // geçici kopyadan yalnız DAMGALI birimler (yayıncının kararı;
                    // damgasız birim kaydolanın kendi beyanı — boş telefonu doldursa
                    // kanıt kendiliğinden geçerdi); silinmiş kopyanın boşaltılmış
                    // kişisel birimleri hiç (oradaki "damgalı boş" bilinçli silme
                    // değil). Kara liste ve not gibi yayıncı kararları böylece
                    // ikisinden de kaybolmaz.
                    var fields = CustomerSyncFields.From(copy);
                    if (copy.CreatedByShopper) fields = fields.StampedOnly();
                    if (copy.PurgedAt is not null) fields = fields.WithoutScrubbedUnits();
                    CustomerFieldMerge.Apply(canonical, fields);
                    // Taşımadan ÖNCE yüklenir (izlenen örnekler): birleştirici aynı
                    // örneklerin WpfCustomerId'sini asıl kayda çevirir. Ayrılmış
                    // bağlantılar DAHİL: birleştirici onları da taşır ve
                    // ShopperPurgeService ayrılmış bağlantıdan ulaştığı projeksiyonu
                    // da siler — kanıtsız kalsa saldırganın KVKK silme talebi asıl
                    // kaydı silerdi.
                    if (copy.CreatedByShopper)
                        provisionalLinks.AddRange(await _db.ShopperBroadcasterLinks
                            .Where(l => l.LicenseId == licenseId && l.WpfCustomerId == copy.Id)
                            .Include(l => l.Shopper)
                            .ToListAsync(ct));
                    // Kopyanın boşaltılması silme kararı DEĞİL: PurgedAt'e dokunulmaz
                    // (eşzamanlılık jetonu — aç/kapa yapmak eşzamanlı bir purge'ü ezerdi).
                    copy.ScrubPersonal();
                    copy.UpdatedAt = now;
                    copy.MergedIntoId = canonical.Id;

                    var chained = await _db.WpfCustomerProjections.IgnoreQueryFilters()
                        .Where(p => p.LicenseId == licenseId && p.MergedIntoId == copy.Id)
                        .ToListAsync(ct);
                    foreach (var alias in chained)
                    {
                        alias.MergedIntoId = canonical.Id;
                        alias.UpdatedAt = now;
                    }

                    await _merger.RepointReferencesAsync(licenseId, copy.Id, canonical.Id, ct);
                }
                if (anyPurged)
                {
                    var purgedAt = rows.Where(r => r.PurgedAt is not null && !r.CreatedByShopper).Min(r => r.PurgedAt!.Value);
                    // Asıl kayıt zaten silinmişse kendi (belki daha geç) tarihini
                    // korumaz: ilk silme tarihi açıkça en erkeni.
                    canonical.ScrubPersonal();
                    canonical.PurgedAt = canonical.PurgedAt is { } p && p < purgedAt ? p : purgedAt;
                }
                // Kaydedilecek telefona karşı (bkz. sınıf dokümanı).
                var groupUnbound = 0; // yalnız işlem başarılıysa toplama eklenir
                foreach (var link in provisionalLinks)
                {
                    if (canonical.CreatedByShopper
                        || !WpfCustomerLinkMatcher.PhoneProves(canonical.Phone, link.Shopper.Phone, link.Shopper.PhoneVerifiedAt))
                    {
                        link.WpfCustomerId = null; // beklemeye: normal kanıt akışları yeniden bağlar
                        groupUnbound++;
                    }
                }
                canonical.UpdatedAt = now;
                await _db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                unbound += groupUnbound;
            }
            catch (DbUpdateConcurrencyException)
            {
                failed++;
            }
            finally
            {
                _db.ChangeTracker.Clear();
            }
        }

        return new Report(groups.Count, copies, orders, shipments, links, balances, purged, failed, unbound);
    }
}
