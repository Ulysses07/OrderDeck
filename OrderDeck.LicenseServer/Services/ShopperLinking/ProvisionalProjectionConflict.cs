using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;

namespace OrderDeck.LicenseServer.Services.ShopperLinking;

/// <summary>
/// Shopper kaydı/katılması kimliğin hiç adayını görmeyip GEÇİCİ projeksiyon
/// açarken, okuma ile kayıt arasında aynı kimliği başka biri açmışsa (yayıncının
/// gönderimi, başka bir shopper) kayıt kimlik indeksine (B1,
/// <see cref="CustomerSync.CustomerIdentityIndex"/>) çarpar. Geçici satır
/// bırakılır ve bağlantı, aradaki asıl kayda karşı normal kuralla kurulur:
/// telefon kanıtı varsa bağlanır, yoksa beklemede kalır (WpfCustomerId = null) —
/// geriye dönük eşleştirme ve telefon doğrulaması sonra bağlar. Kullanıcıya 500
/// gitmez; aynı kimliğe ikinci bir asıl kayıt da açılmaz.
/// </summary>
public static class ProvisionalProjectionConflict
{
    /// <summary>Geçici satırı izleyiciden çıkarır, adayları kimlik anahtarıyla
    /// yeniden okur, bağlantıyı kanıtla bağlar ya da beklemede bırakır. KAYDETMEZ:
    /// çağıran aynı izleyicideki öteki değişikliklerle (shopper, bağlantı…)
    /// birlikte yeniden kaydeder — ilk kayıt bütünüyle geri alındı.</summary>
    public static async Task YieldAsync(
        LicenseDbContext db, WpfCustomerProjection provisional, ShopperBroadcasterLink link,
        Shopper shopper, CancellationToken ct)
    {
        db.Entry(provisional).State = EntityState.Detached;
        // IdentityKey != "": filtreli kimlik indeksinin koşulu (kayıt/katılma
        // aday aramasıyla aynı sorgu).
        var candidates = await db.WpfCustomerProjections
            .Where(p => p.LicenseId == provisional.LicenseId
                && p.Platform == provisional.Platform
                && p.IdentityKey == provisional.IdentityKey && p.IdentityKey != "")
            .ToListAsync(ct);
        link.WpfCustomerId = WpfCustomerLinkMatcher.FindProven(
            candidates, shopper.Phone, shopper.PhoneVerifiedAt)?.Id;
    }
}
