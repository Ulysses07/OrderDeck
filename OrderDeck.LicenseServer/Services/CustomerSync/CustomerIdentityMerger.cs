using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;

namespace OrderDeck.LicenseServer.Services.CustomerSync;

/// <summary>
/// Bir müşteri kopyasına (fromId) bağlı her şeyi asıl kayda (toId) taşır.
/// Değişiklikleri yalnız izlemeye ekler; SaveChanges çağırmaz — çağıran kendi
/// işlem sınırında kaydeder (sync ucu aynı istekte, birleştirme işi grup
/// başına işlemde).
///
/// İzlenen güncelleme kullanılıyor, ExecuteUpdate değil: InMemory test
/// sağlayıcısı ExecuteUpdate desteklemiyor ve hacim küçük (kişi başına
/// birkaç düzine satır).
///
/// Bilerek taşınmayan: SmsCampaignRecipient.WpfCustomerId — gönderim anının
/// denetim kaydı; o an hangi kayda gidildiyse o kalır.
///
/// CustomerBalance de bilerek taşınmaz/silinmez (A4 kalite incelemesi,
/// 2026-10-06): tutar asıl kaydın satırına eklenir (yoksa açılır), kopyanınki
/// 0'lanır ama satır kalır. Gerekçe RepointReferencesAsync'in bakiye bloğunda.
/// </summary>
public sealed class CustomerIdentityMerger
{
    private readonly LicenseDbContext _db;
    public CustomerIdentityMerger(LicenseDbContext db) => _db = db;

    public sealed record RepointCounts(
        int Orders, int Shipments, int Links, int BalanceTransactions,
        int Balances, int IbanMemories, int PaymentMatches, int WaConversations);

    public async Task<RepointCounts> RepointReferencesAsync(
        Guid licenseId, Guid fromId, Guid toId, CancellationToken ct)
    {
        // Yozlaşmış çağrı: kopya ve asıl aynı kayıtsa taşınacak bir şey yok.
        // Korumasız bırakılsaydı CustomerBalance tarafında fromBal/toBal AYNI
        // satıra düşerdi: "toBal.Balance += fromBal.Balance" kendi üzerine
        // toplar (iki katına çıkar), hemen ardından "fromBal.Balance = 0m"
        // AYNI nesneyi sıfırlar — net sonuç bakiyenin sıfırlanıp kaybolması.
        // Erken çıkış hiçbir satırı okumadan/izlemeden sıfır sayaçla döner.
        if (fromId == toId)
            return new RepointCounts(0, 0, 0, 0, 0, 0, 0, 0);

        var now = DateTimeOffset.UtcNow;
        var fromHex = fromId.ToString("N");
        var toHex = toId.ToString("N");

        var orders = await _db.Orders
            .Where(o => o.LicenseId == licenseId && o.CustomerId == fromHex).ToListAsync(ct);
        foreach (var o in orders)
        {
            o.CustomerId = toHex;
            o.UpdatedAt = now;
            // SyncVersion ARTIRILMAZ: EF jetonu bu UPDATE'in WHERE'ine zaten
            // koyar; orders/sync güncellemede CustomerId yazmaz. Artış
            // yalnız eşzamanlı orders/sync partilerini 409'a çevirirdi (A4
            // kalite incelemesi).
        }

        var shipments = await _db.Shipments
            .Where(s => s.LicenseId == licenseId && s.CustomerId == fromHex).ToListAsync(ct);
        foreach (var s in shipments) { s.CustomerId = toHex; s.UpdatedAt = now; }

        var links = await _db.ShopperBroadcasterLinks
            .Where(l => l.LicenseId == licenseId && l.WpfCustomerId == fromId).ToListAsync(ct);
        foreach (var l in links) l.WpfCustomerId = toId;

        var txs = await _db.CustomerBalanceTransactions
            .Where(t => t.LicenseId == licenseId && t.WpfCustomerId == fromId).ToListAsync(ct);
        foreach (var t in txs) t.WpfCustomerId = toId;

        // Bakiye: kopyanın satırı TAŞINMAZ ve SİLİNMEZ — tutarı asıl kaydın
        // satırına eklenir (yoksa açılır), kopyanınki 0'lanır (A4 kalite
        // incelemesi). (1) Birleştirme işi bir kişinin birden çok kopyasını tek
        // kayıt işleminde taşır: taşıma, ikinci kopyada asıl satırı
        // veritabanında bulamayıp ikinci bir satır açar ve tekil indeksi
        // (LicenseId, WpfCustomerId) patlatırdı — asıl satır önce izlenen
        // (henüz kaydedilmemiş) satırlarda aranır. (2) Kopyanın bakiyesine aynı
        // anda dokunan bir uygulama silinmiş ya da başka kişiye geçmiş satırı
        // yeniden yükleyip defteri bozardı; 0'lanmış satırda mevcut
        // yeniden-yükle mantığı temiz bir "bakiye yok" verir.
        // Kararlaştırıldı (2026-10-05): aynı kişinin kopyalarındaki bakiyeler toplanır.
        var balances = 0;
        var fromBal = await _db.CustomerBalances
            .SingleOrDefaultAsync(b => b.LicenseId == licenseId && b.WpfCustomerId == fromId, ct);
        if (fromBal is not null && fromBal.Balance != 0m)
        {
            // DbSet.Local Deleted durumdakileri içermez.
            var toBal = _db.CustomerBalances.Local
                            .SingleOrDefault(b => b.LicenseId == licenseId && b.WpfCustomerId == toId)
                        ?? await _db.CustomerBalances
                            .SingleOrDefaultAsync(b => b.LicenseId == licenseId && b.WpfCustomerId == toId, ct);
            if (toBal is null)
            {
                toBal = new CustomerBalance
                {
                    Id = Guid.NewGuid(), LicenseId = licenseId, WpfCustomerId = toId,
                    Balance = 0m, UpdatedAt = now,
                };
                _db.CustomerBalances.Add(toBal);
            }
            toBal.Balance += fromBal.Balance;
            toBal.UpdatedAt = now;
            fromBal.Balance = 0m;
            fromBal.UpdatedAt = now;
            balances = 1;
        }

        var ibans = await _db.CustomerIbanMemories
            .Where(m => m.LicenseId == licenseId && m.WpfCustomerId == fromId).ToListAsync(ct);
        foreach (var m in ibans) m.WpfCustomerId = toId;

        var matches = await _db.PaymentMatches
            .Where(m => m.LicenseId == licenseId
                && (m.ProposedWpfCustomerId == fromId || m.ActualWpfCustomerId == fromId))
            .ToListAsync(ct);
        foreach (var m in matches)
        {
            if (m.ProposedWpfCustomerId == fromId) m.ProposedWpfCustomerId = toId;
            if (m.ActualWpfCustomerId == fromId) m.ActualWpfCustomerId = toId;
            m.UpdatedAt = now; // jeton; SaveChanges'teki StampPaymentMatchVersions zaten yeniden damgalar
        }

        var convs = await _db.WaConversations
            .Where(c => c.LicenseId == licenseId && c.WpfCustomerId == fromId).ToListAsync(ct);
        foreach (var c in convs) c.WpfCustomerId = toId;

        return new RepointCounts(orders.Count, shipments.Count, links.Count, txs.Count,
            balances, ibans.Count, matches.Count, convs.Count);
    }
}
