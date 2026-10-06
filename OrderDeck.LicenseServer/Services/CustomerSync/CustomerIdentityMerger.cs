using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;

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
        // Korumasız bırakılsaydı iki gerçek hata oluşurdu: (1) Order.SyncVersion
        // hiçbir anlamı olmadan artardı (CustomerId zaten toHex'e eşit olsa da
        // ++ koşulsuz çalışır), (2) CustomerBalance tarafında fromBal/toBal
        // AYNI satıra düşer ve "toBal var" dalı o satırı KENDİ ÜZERİNE
        // toplayıp SİLERDİ — bakiye kaybı. Erken çıkış hiçbir satırı okumadan/
        // izlemeden sıfır sayaçla döner.
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
            o.SyncVersion++; // eşzamanlı orders/sync bu satırı ezmesin (Order.SyncVersion)
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

        var balances = 0;
        var fromBal = await _db.CustomerBalances
            .SingleOrDefaultAsync(b => b.LicenseId == licenseId && b.WpfCustomerId == fromId, ct);
        if (fromBal is not null)
        {
            var toBal = await _db.CustomerBalances
                .SingleOrDefaultAsync(b => b.LicenseId == licenseId && b.WpfCustomerId == toId, ct);
            if (toBal is null)
            {
                fromBal.WpfCustomerId = toId;
                fromBal.UpdatedAt = now;
            }
            else
            {
                // Kararlaştırıldı (2026-10-05): aynı kişinin kopyalarındaki
                // bakiyeler toplanır.
                toBal.Balance += fromBal.Balance;
                toBal.UpdatedAt = now;
                _db.CustomerBalances.Remove(fromBal);
            }
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
            m.UpdatedAt = now; // PaymentMatch eşzamanlılık jetonu
        }

        var convs = await _db.WaConversations
            .Where(c => c.LicenseId == licenseId && c.WpfCustomerId == fromId).ToListAsync(ct);
        foreach (var c in convs) c.WpfCustomerId = toId;

        return new RepointCounts(orders.Count, shipments.Count, links.Count, txs.Count,
            balances, ibans.Count, matches.Count, convs.Count);
    }
}
