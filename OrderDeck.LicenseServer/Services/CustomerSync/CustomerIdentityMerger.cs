using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Domain.Bank;

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
/// 0'lanır ama satır kalır. Sıfır bakiyeli kopya satırı da damgalanır (ortak bakiye M12).
/// Gerekçe RepointReferencesAsync'in bakiye bloğunda.
///
/// IBAN bağı (banka havalesi onayı, spec 2026-10-10 §9): asıl kayıtta aynı özetli bağ varsa ikisi tek satıra iner; ikisi
/// de asıl kayda geçseydi geniş tekil (LicenseId, IbanHash, WpfCustomerId) kaydı 2601'le düşürürdü. Çift ancak dar tekil
/// (LicenseId, IbanHash) düşünce (S3b) doğar. Kural tek kayıttakinin eşidir: orada ikinci öğrenme yeni satır açmaz, ilk
/// öğrenilen satırı tazeler. Kalan satırı <see cref="PreferBond"/> seçer (eski olan); Id'si, CreatedAt'i, öğreten havalesi
/// (SourceBankTransactionId) ve maskesi değişmez. İkisinden biri BankConfirm ise kaynağı BankConfirm olur (tasarım §2.5'in
/// "Evet" upsert'i). Son doğrulaması LearnIbanAsync'teki gibi yalnız ileri gider: düşeninki kalanın başlangıcından
/// (LastConfirmedAt, yoksa CreatedAt) yeniyse alınır (KVKK K-6: süre son doğrulanmış havaleden işler). Düşen satıra imha
/// kaydı YAZILMAZ: bağ kalan satırda yaşar.
/// Eşliğin iki sınırı var. (1) İki satırın bugünkü hâline göredir: satırlardan biri kaldırmayla silinip yeniden öğrenildiyse
/// ayrışma birleşmeden önce, iki kayıtlı dünyada başlamıştır (tek kayıtta o kaldırma tek satırı silerdi); birleşme onu
/// düzeltemez, iki kayıtlı dünyanın fazlasını korur. (2) Son doğrulama iki satırda tek kayıttakiyle aynıdır. Aynı özetli üç ya
/// da daha çok satır tek SaveChanges'te birleşirken (birden çok kopya; birleştirme işi kopyaları bağ yaşına değil UpdatedAt'e
/// göre sıralar) ara adımda kalan satırın son doğrulaması boşsa (P1 öncesi), onun CreatedAt'inden eski diye alınmayan
/// doğrulama, sonraki adımda daha eski bir satır kalınca kaybolmuş olur: sonuç sıraya bağlıdır. Hata yönü yalnız erken
/// silmedir: kalanın son doğrulaması hep gerçek bir doğrulamadır, en yenisini aşmaz.
///
/// Dekontun müşterisi (Payment.WpfCustomerId) çevrilir, başka alanı yazılmaz. Payment.Status eşzamanlılık jetonudur: okuma
/// ile kayıt arasında karara bağlanan dekont DbUpdateConcurrencyException üretir. Onu çağıran ele alır: müşteri senkronu
/// partiyi taze okumayla bir kez yeniden uygular (ikincide 409), birleştirme işi grubu geri alır ve sayar.
/// </summary>
public sealed class CustomerIdentityMerger
{
    private readonly LicenseDbContext _db;
    public CustomerIdentityMerger(LicenseDbContext db) => _db = db;

    /// <summary>Taşınan/birleştirilen satır sayıları.</summary>
    /// <param name="IbanMemories">Kopyanın bütün bağları: taşınan da, tekilleşen (kendisi ya da asıl kayıttaki ikizi silinen) de.</param>
    /// <param name="Balances">
    /// 1 yalnız kopyanın SIFIR OLMAYAN bir bakiyesi asıl kayda eklendiğinde; kopyanın bakiye satırı zaten 0'sa
    /// sayılmaz — satır yalnız damgalanır (ortak bakiye M12: jetonu ilerler, tutarı değişmez). A7'nin günlüklediği
    /// sayaç budur.
    /// </param>
    public sealed record RepointCounts(
        int Orders, int Shipments, int Links, int BalanceTransactions,
        int Balances, int IbanMemories, int PaymentMatches, int WaConversations, int Payments);

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
            return new RepointCounts(0, 0, 0, 0, 0, 0, 0, 0, 0);

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
        // Eksi bakiye oluşabilirse (örn. panelin elle ayarı negatif bırakabilir)
        // toplama aynen işler — karar tutarın işaretine bakmaz.
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
        else if (fromBal is not null)
        {
            // Ortak bakiye M12 (spec 2026-10-11 §4.12): tutarı 0 olsa da kopyanın bakiye satırı damgalanır (jetonu
            // LicenseDbContext.StampVersions ilerletir). Çok bacaklı düşümden sonra bacak satırı tipik olarak 0'da
            // kalır; damgasız, o satırı okuyan eşzamanlı bir grup geri alması çakışmadan commit eder ve parayı kopya
            // satıra yazardı — bütün okumalar asıl kayda çözüldüğü için o para görünmez olurdu. Damgayla geri alma
            // çakışır ve bacağın güncel (asıl) satırıyla yeniden kurulur.
            fromBal.UpdatedAt = now;
            _db.Entry(fromBal).Property(b => b.UpdatedAt).IsModified = true;
        }

        var moving = await _db.CustomerIbanMemories
            .Where(m => m.LicenseId == licenseId && m.WpfCustomerId == fromId).ToListAsync(ct);
        if (moving.Count > 0)
        {
            var hashes = moving.Select(m => m.IbanHash).Distinct().ToList();
            // Karar Local'den: aynı SaveChanges'te önceki kopyadan taşınmış satır veritabanında hâlâ eski kimliktedir. Local,
            // Deleted'ı içermez.
            await _db.CustomerIbanMemories
                .Where(m => m.LicenseId == licenseId && m.WpfCustomerId == toId && hashes.Contains(m.IbanHash))
                .LoadAsync(ct);
            foreach (var m in moving)
            {
                var twin = _db.CustomerIbanMemories.Local.FirstOrDefault(x => x != m
                    && x.LicenseId == licenseId && x.WpfCustomerId == toId && x.IbanHash == m.IbanHash);
                if (twin is null)
                {
                    m.WpfCustomerId = toId;
                    continue;
                }
                var (keep, drop) = PreferBond(m, twin);
                // Tek kayıtta sonradan gelen "Evet" ilk öğrenilen satırın kaynağını yükseltir; ManualMatch ve HumanApproval tazelemesi
                // kaynağı değiştirmez.
                if (drop.LearnedFrom == IbanMemorySource.BankConfirm) keep.LearnedFrom = IbanMemorySource.BankConfirm;
                if (drop.LastConfirmedAt is { } at && (keep.LastConfirmedAt ?? keep.CreatedAt) < at)
                    keep.LastConfirmedAt = at;
                keep.WpfCustomerId = toId;
                _db.CustomerIbanMemories.Remove(drop);
            }
        }

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

        // UpdatedAt İLERLETİLMEZ: payments/since imleci odur, ilerlese dekontlar bütün bilgisayarlara yeniden inerdi. Status
        // jetondur, dokunulmaz.
        var payments = await _db.Payments
            .Where(p => p.LicenseId == licenseId && p.WpfCustomerId == fromId).ToListAsync(ct);
        foreach (var p in payments) p.WpfCustomerId = toId;

        var convs = await _db.WaConversations
            .Where(c => c.LicenseId == licenseId && c.WpfCustomerId == fromId).ToListAsync(ct);
        foreach (var c in convs) c.WpfCustomerId = toId;

        return new RepointCounts(orders.Count, shipments.Count, links.Count, txs.Count,
            balances, moving.Count, matches.Count, convs.Count, payments.Count);
    }

    /// <summary>
    /// Kanıtı düşen Shopper bağlantısının eşi (spec 2026-10-10 §2.3, §12): müşterisini o bağlantıdan almış dekontlarda
    /// Payment.WpfCustomerId boşalır. Onlar shopper'ın, bağlantının veritabanındaki (bu kayıttan önceki) müşterisine bağlı
    /// dekontlarıdır: birleştirici bağlantıyla birlikte onları da <paramref name="customerId"/>'ye çevirdi (yerinde kanıt
    /// isteyen bağlantıda ikisi aynı kayıt). Kalsaydı kanıtsız kişinin dekontu birleştirmeyle gerçek müşteriye geçerdi.
    /// Shopper'ın o kayda zaten bağlı dekontu kalır: müşterisi başka bir bağlantısından geldi, o bağa bu işlem dokunmadı
    /// (türetme kuralı, ResolveWpfCustomerAsync: en yeni müşterili bağ). Süzgeç veritabanındaki değere bakar (taşınan
    /// bağlantı da dekont da orada hâlâ eski kimliktedir), karar izlenen değere. SaveChanges çağırmaz; UpdatedAt ilerlemez
    /// (payments/since imleci), Status jetonuna dokunulmaz.
    /// </summary>
    /// <param name="link">Kanıtı düşen bağlantı; WpfCustomerId'si boşaltılmış olabilir, veritabanındaki değeri okunur.</param>
    /// <param name="customerId">Bağlantının boşaltılmadan önceki (izlenen) müşterisi.</param>
    public async Task UnbindPaymentsAsync(ShopperBroadcasterLink link, Guid customerId, CancellationToken ct)
    {
        // Skaler okuma kimlik çözümüne girmez: izlenen örneğin değerini değil, veritabanındakini verir.
        var boundTo = await _db.ShopperBroadcasterLinks
            .Where(l => l.Id == link.Id).Select(l => l.WpfCustomerId).SingleOrDefaultAsync(ct);
        if (boundTo is null) return;
        var payments = await _db.Payments
            .Where(p => p.LicenseId == link.LicenseId && p.ShopperId == link.ShopperId && p.WpfCustomerId == boundTo)
            .ToListAsync(ct);
        foreach (var p in payments.Where(p => p.WpfCustomerId == customerId))
            p.WpfCustomerId = null;
    }

    /// <summary>
    /// Aynı özetli iki bağdan kalan ve düşen: eski CreatedAt, eşitse küçük Id ("N" ordinal); kaynağa bakılmaz. Tek kayıttaki
    /// tazelemenin eşi: orada kalan satır ilk öğrenilendir, sonraki öğrenme onu tazeler (PaymentMatchReconciler.LearnIbanAsync:
    /// kaynağa ve öğreten havaleye dokunmaz, LastConfirmedAt yalnız ileri gider). Tasarım §2.5'in "Evet" upsert'i de var olan
    /// satırın kaynağını BankConfirm'e yükseltip süresini tazeler; öğreten havale (SourceBankTransactionId) ve CreatedAt
    /// değişmez. Yükseltmeyi çağıran yapar. "BankConfirm önce" seçimi kopyanın daha yeni onayını tutardı: kalan satırın öğreten
    /// havalesi ve CreatedAt'i, eşleme kaldırmanın ve yeni onayın sonucu tek kayıttakinden ayrışırdı (S2a incelemesinin
    /// SharedConfirm ölçümü). Eşlik iki satırın bugünkü hâline göredir (sınırları sınıf özetinde). Çift ancak dar tekil düşünce
    /// (S3b) doğar, kural orada canlanır.
    /// </summary>
    private static (CustomerIbanMemory Keep, CustomerIbanMemory Drop) PreferBond(CustomerIbanMemory a, CustomerIbanMemory b)
    {
        var keepA = a.CreatedAt != b.CreatedAt ? a.CreatedAt < b.CreatedAt
            : string.CompareOrdinal(a.Id.ToString("N"), b.Id.ToString("N")) < 0;
        return keepA ? (a, b) : (b, a);
    }
}
