using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Services.CustomerSync;

// Namespace kasten "ShopperLinking" (bkz. WpfCustomerLinkMatcher: "Services.Shopper" Shopper varlığıyla çakışırdı).
namespace OrderDeck.LicenseServer.Services.ShopperLinking;

/// <summary>
/// Shopper bağlantısından (<c>ShopperBroadcasterLink.WpfCustomerId</c>) müşteriye giden her yolun TEK çözücüsü:
/// dekontun bağdaştırılması, shopper silmesi, gönderimde dekontun müşterisi ve yeniden bağlamada yeniden yazımı bu
/// sınıftan geçer. Kural tasarım §2.5'tir. Bağlantının Id'si <see cref="CustomerIdResolver.ChainOfAsync"/> ile izlenir;
/// zincir birleştirilmiş geçici (<c>CreatedByShopper</c>) bir kopyadan geçiyorsa varılan kayıt yalnız asıl kayıtsa, kendisi
/// geçici değilse ve shopper'ın telefonu onu kanıtlıyorsa (<see cref="WpfCustomerLinkMatcher.PhoneProves"/>) müşteridir;
/// değilse müşteri bilinmez (null). Geçici kopyanın bağlantısı kanıtsız bağlanmıştır: asıl kayda çözülseydi silme gerçek
/// müşterinin projeksiyonunu siler, kanıtsız kişinin dekontu ona yazılırdı. Sonuç kopya Id'si de değildir: o yazılsaydı
/// <see cref="CustomerIdResolver"/> okuyucusu onu gerçek müşteriye çözerdi.
///
/// <para>Kanıt <c>CustomerIdentityMergeJob</c>'unkiyle aynıdır (asıl kayıt geçici değil ve
/// <c>PhoneProves(asıl.Phone, shopper.Phone, shopper.PhoneVerifiedAt)</c>, argüman sırası dahil): farklı yazılsaydı aynı
/// bağlantı birleştirmede beklemeye düşer, burada asıl kayda giderdi. Geçici kopyadan geçmeyen zincir
/// <see cref="CustomerIdResolver"/> ile aynı Id'ye varır, silinmiş asıl kayıt dahil (S2b planı, Karar 9).</para>
///
/// <para>SQL eşi S2b veri göçüdür (<c>BankPersonMatchingS2b</c>) ve kanıtlı geçici kopyada da bilerek NULL verir: SQL
/// telefonu normalleştiremez (<c>PhoneNormalizer</c>). Statiktir, DI kaydı yoktur; zincir için
/// <c>new CustomerIdResolver(db)</c> kurar.</para>
/// </summary>
public static class ShopperCustomerResolver
{
    /// <summary>
    /// Bağlantının müşterisi (sınıf özeti). <paramref name="shopperPhone"/> ve <paramref name="phoneVerifiedAt"/> yalnız
    /// zincir geçici kopyadan geçince kullanılır.
    /// </summary>
    public static Task<Guid?> ResolveLinkAsync(
        LicenseDbContext db, Guid licenseId, Guid linkCustomerId, string? shopperPhone, DateTimeOffset? phoneVerifiedAt,
        CancellationToken ct)
        => ResolveAsync(db, licenseId, linkCustomerId, () => Task.FromResult((shopperPhone, phoneVerifiedAt)), ct);

    /// <summary>
    /// Dekontun müşterisini türetme kuralı (tasarım §2.3): shopper×lisansın müşterili bağlantılarından en yeni katılanı
    /// (<c>JoinedAt</c>, eşitlikte <c>Id</c> azalan; <c>LeftAt</c>'e bakılmaz), sonra <see cref="ResolveLinkAsync"/>'in
    /// kuralı; shopper'ın telefonu yalnız zincir geçici kopyadan geçince okunur. Seçilen bağlantının Id'si ve okunan
    /// müşterisi (kopya Id'si olabilir) de döner: yeniden yazım ancak bağlantı hâlâ o müşterideyse yazar. Shopper yoksa
    /// ya da müşterili bağlantı yoksa null.
    /// </summary>
    public static async Task<DerivedCustomer?> DeriveAsync(
        LicenseDbContext db, Guid licenseId, Guid? shopperId, CancellationToken ct)
    {
        if (shopperId is null) return null;
        var link = await db.ShopperBroadcasterLinks.AsNoTracking()
            .Where(l => l.ShopperId == shopperId && l.LicenseId == licenseId && l.WpfCustomerId != null)
            .OrderByDescending(l => l.JoinedAt).ThenByDescending(l => l.Id)
            .Select(l => new { l.Id, CustomerId = l.WpfCustomerId!.Value })
            .FirstOrDefaultAsync(ct);
        if (link is null) return null;
        var customerId = await ResolveAsync(db, licenseId, link.CustomerId, async () =>
        {
            var shopper = await db.Shoppers
                .Where(s => s.Id == shopperId)
                .Select(s => new { s.Phone, s.PhoneVerifiedAt })
                .FirstOrDefaultAsync(ct);
            return (shopper?.Phone, shopper?.PhoneVerifiedAt);
        }, ct);
        return new DerivedCustomer(customerId, link.Id, link.CustomerId);
    }

    /// <summary>Dekontun müşterisi: <see cref="DeriveAsync"/>'in sonucu.</summary>
    public static async Task<Guid?> PaymentCustomerAsync(
        LicenseDbContext db, Guid licenseId, Guid? shopperId, CancellationToken ct)
        => (await DeriveAsync(db, licenseId, shopperId, ct))?.CustomerId;

    /// <summary>Kuralın kendisi (sınıf özeti); <paramref name="shopperProof"/> yalnız geçici kopyadan geçen zincirde
    /// çağrılır.</summary>
    private static async Task<Guid?> ResolveAsync(
        LicenseDbContext db, Guid licenseId, Guid linkCustomerId,
        Func<Task<(string? Phone, DateTimeOffset? VerifiedAt)>> shopperProof, CancellationToken ct)
    {
        var chain = (await new CustomerIdResolver(db).ChainOfAsync(licenseId, [linkCustomerId], ct))[linkCustomerId];
        if (!chain.ViaProvisionalCopy) return chain.Canonical;
        var arrived = await db.WpfCustomerProjections.IgnoreQueryFilters()
            .Where(p => p.LicenseId == licenseId && p.Id == chain.Canonical)
            .Select(p => new { p.MergedIntoId, p.CreatedByShopper, p.Phone })
            .FirstOrDefaultAsync(ct);
        if (arrived is null || arrived.MergedIntoId is not null || arrived.CreatedByShopper) return null;
        var (phone, verifiedAt) = await shopperProof();
        return WpfCustomerLinkMatcher.PhoneProves(arrived.Phone, phone, verifiedAt) ? chain.Canonical : null;
    }
}

/// <summary>
/// <see cref="ShopperCustomerResolver.DeriveAsync"/>'in sonucu: dekontun müşterisi (bilinmiyorsa null), seçilen
/// bağlantının Id'si ve o bağlantıdan okunan müşteri Id'si (çözülmemiş; kopya Id'si olabilir).
/// </summary>
public readonly record struct DerivedCustomer(Guid? CustomerId, Guid LinkId, Guid LinkCustomerId);
