using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.CustomerSync;

// Namespace kasten "ShopperLinking" (bkz. WpfCustomerLinkMatcher: "Services.Shopper" Shopper varlığıyla çakışırdı).
namespace OrderDeck.LicenseServer.Services.ShopperLinking;

/// <summary>
/// Shopper bağlantısından (<c>ShopperBroadcasterLink.WpfCustomerId</c>) müşteriye giden dört yolun ortak çözücüsü:
/// dekontun bağdaştırılması, shopper silmesi, gönderimde dekontun müşterisi ve yeniden bağlamada yeniden yazımı bu
/// sınıftan geçer. Tek yol değildir: shopper'ın bakiye ucu (<c>ShopperBalanceController</c>) bağlantıyı hâlâ
/// <see cref="CustomerIdResolver"/> ile kanıtsız çözer (banka dışı, ayrı iş). Kural tasarım §2.5'tir. Bağlantının Id'si
/// <see cref="CustomerIdResolver.ChainOfAsync"/> ile izlenir; zincir birleştirilmiş geçici (<c>CreatedByShopper</c>) bir
/// kopyadan geçiyorsa varılan kayıt yalnız asıl kayıtsa, kendisi
/// geçici değilse ve shopper'ın telefonu onu kanıtlıyorsa (<see cref="WpfCustomerLinkMatcher.PhoneProves"/>) müşteridir;
/// değilse müşteri bilinmez (null). Geçici kopyanın bağlantısı kanıtsız bağlanmıştır: asıl kayda çözülseydi silme gerçek
/// müşterinin projeksiyonunu siler, kanıtsız kişinin dekontu ona yazılırdı. Sonuç kopya Id'si de değildir: o yazılsaydı
/// <see cref="CustomerIdResolver"/> okuyucusu onu gerçek müşteriye çözerdi.
///
/// <para>Kanıt <c>CustomerIdentityMergeJob</c>'unkiyle aynıdır (asıl kayıt geçici değil ve
/// <c>PhoneProves(asıl.Phone, shopper.Phone, shopper.PhoneVerifiedAt)</c>, argüman sırası dahil): farklı yazılsaydı aynı
/// bağlantı birleştirmede beklemeye düşer, burada asıl kayda giderdi. Geçici kopyadan geçen zincir lisansın asıl kaydına
/// varmazsa (başka lisansın Id'si, adım sınırında kalan kopya) kanıt aranmaz, müşteri bilinmez. Geçici kopyadan geçmeyen zincir
/// <see cref="CustomerIdResolver"/> ile aynı Id'ye varır: silinmiş asıl kayıt (S2b planı, Karar 9), başka lisansın Id'si ve adım
/// sınırında (3 adım) kalan kopya dahil. Sınırda varılan dördüncü düğüm geçici kopya olsa da yönlendirmesi izlenmediği için
/// bayrağı sayılmaz, sonuç o kopyanın Id'sidir: yukarıdaki "kopya Id'si değildir" kuralının tek istisnası. Birleştirme lisans
/// içinde kalır ve zinciri düzleştirir; başka lisansa ya da adım sınırına pratikte varılmaz.</para>
///
/// <para>SQL eşi S2b veri göçüdür (<c>BankPersonMatchingS2b</c>) ve kanıtlı geçici kopyada da bilerek NULL verir: SQL
/// telefonu normalleştiremez (<c>PhoneNormalizer</c>). Statiktir, DI kaydı yoktur; zincir için
/// <c>new CustomerIdResolver(db)</c> kurar.</para>
///
/// <para>Dekontun müşterisini yazan iki koşullu deyim (<see cref="RederivePaymentsAsync"/>,
/// <see cref="ClearIfLinkChangedAsync"/>; S2b planı Karar 11) koşul tablolarını (<c>ShopperBroadcasterLinks</c>,
/// <c>Shoppers</c>) <c>WITH (READCOMMITTEDLOCK)</c> ile kilitli okur: bağı kesen işlem commit olana dek beklenir, RCSI
/// ayarından bağımsız. Prod veritabanı ilk kurulumda <c>EnsureCreated()</c> ile oluşturuldu ve EF Core SQL Server'da
/// veritabanını oluştururken <c>READ_COMMITTED_SNAPSHOT</c>'ı açar: prod'da RCSI büyük olasılıkla açık. RCSI açıkken ipucusuz
/// koşul ifade başındaki anlık görüntüyü okurdu; bağlantıyı silen ya da boşaltan, shopper'a <c>DeletedAt</c> yazan işlem commit
/// olmamışken koşan yazım eski bağlantıyı görür, kesilen bağı yazardı. Hedef tablo (<c>Payments</c>) RCSI açıkken de U
/// kilidiyle en son commit'ten okunur; yalnız koşul tabloları sürümlü okunurdu. EF Core LINQ'te tablo ipucu yok: ilişkisel
/// yolda iki deyim ham SQL'dir, değerler parametredir. Kalan pencere RCSI kapalı tasarımdakiyle aynıdır ve plana bağlıdır.
/// Plan dekontu önce U kilidiyle okuyup koşulu sonra sınarsa (yerelde gözlenen plan), yazım dekontun kilidini tutarak bekler;
/// kesen ardından aynı dekonta yazarsa kilitlenme olur, kurban çoğunlukla az günlük yazmış olan yazımdır. Yeniden yazımda
/// <see cref="TryRederivePaymentsAsync"/>, denetimde gönderimin 12b adımı yakalar; dekont kesenin değerinde kalır. Plan
/// ilişkisiz EXISTS'i dekontun kilidinden önce sınarsa, kesen aynı kayıtta dekontu bağlantıdan ya da shopper'dan önce
/// yazdığında (milisaniye altı) yazım kesmeyi görmeden dekontun kilidinde bekler ve commit'ten sonra yazar. Bugünkü kesenler
/// (silme, <c>ReproveLinksAsync</c>, birleştirme işi) tek SaveChanges'te dekontu bağlantıdan ve shopper'dan önce yazar (EF
/// komutları tablo adına göre sıralar, bu tablolar arasında yabancı anahtar yok); <c>DeleteMe</c> dekont yazmaz. Kilitlenme dalı
/// ancak bu sırayı tersine çeviren yeni bir kesenle doğar; bugünkü kesenlerle kalan pencere ikinci daldır.</para>
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

    /// <summary>
    /// Yeniden bağlamada dekontların yeniden yazımı (tasarım §2.3'ün açık maddesi; S2b planı Karar 10, 11). Bağ kaydedildikten
    /// SONRA her lisans için <see cref="DeriveAsync"/> okunur ve shopper'ın o lisanstaki dekontları (<c>Source = Receipt</c>)
    /// türetilen müşteriye yazılır (bilinmiyorsa null); müşterili bağlantısı olmayan lisans geçilir. Yazım lisans başına tek
    /// koşullu UPDATE'tir: seçilen bağlantı hâlâ okunan müşterideyse ve shopper silinmemişse yazar. Bağı kesen yollar (silme
    /// bağlantıyı siler ve dekontun müşterisini boşaltır; kanıt kaybı bağlantıyı boşaltır) <c>Status</c>'a dokunmaz; koşul
    /// okumada kalsaydı okumadan sonra commit olan kesme geri yazılırdı, okuyucu da geride kalan asıl kaydı ayırt edemezdi.
    /// Koşul tabloları kilitli okunur: kesme henüz commit olmadıysa yazım onun commit'ini bekler, RCSI ayarından bağımsız (sınıf
    /// özeti; kalan pencere orada). <c>Status</c>'a bakmaz; onu, <c>UpdatedAt</c>'i (<c>payments/since</c> imleci) ve jetonu
    /// yazmaz: araya giren onay yazımı düşürmez. İlişkiselde ham SQL'dir ve izleyiciyi atlar (çağıranlar sonra
    /// <see cref="Payment"/> okumaz), InMemory'de aynı süzgeçle izlenen yazım ve kayıt (<c>SessionReleaser</c> deseni). Değişen
    /// satır sayısını döner. Çağıranlar yalıtılmış <see cref="TryRederivePaymentsAsync"/>'i kullanır.
    /// </summary>
    public static async Task<int> RederivePaymentsAsync(
        LicenseDbContext db, Guid shopperId, IReadOnlyCollection<Guid> licenseIds, CancellationToken ct)
    {
        var changed = 0;
        foreach (var licenseId in licenseIds.Distinct())
        {
            if (await DeriveAsync(db, licenseId, shopperId, ct) is not { } d) continue;
            if (db.Database.IsRelational())
            {
                // Koşul tabloları kilitli okunur (sınıf özeti). Source sütunu metin saklar; eşitsizlik null-güvenlidir
                // (türetilen null olabilir; IS DISTINCT FROM 2022'den önce yok).
                changed += await db.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE p SET p.[WpfCustomerId] = {d.CustomerId}
                    FROM [Payments] AS p
                    WHERE p.[LicenseId] = {licenseId} AND p.[ShopperId] = {shopperId} AND p.[Source] = N'Receipt'
                      AND NOT EXISTS (SELECT p.[WpfCustomerId] INTERSECT SELECT {d.CustomerId})
                      AND EXISTS (SELECT 1 FROM [ShopperBroadcasterLinks] AS l WITH (READCOMMITTEDLOCK)
                                  WHERE l.[Id] = {d.LinkId} AND l.[WpfCustomerId] = {d.LinkCustomerId})
                      AND EXISTS (SELECT 1 FROM [Shoppers] AS s WITH (READCOMMITTEDLOCK)
                                  WHERE s.[Id] = {shopperId} AND s.[DeletedAt] IS NULL)
                    """, ct);
                continue;
            }
            var (link, live) = StillLinked(db, shopperId, d);
            var stale = db.Payments.Where(p => p.LicenseId == licenseId && p.ShopperId == shopperId
                && p.Source == PaymentSource.Receipt && p.WpfCustomerId != d.CustomerId && link.Any() && live.Any());
            var rows = await stale.ToListAsync(ct);
            foreach (var p in rows) p.WpfCustomerId = d.CustomerId;
            if (rows.Count > 0) await db.SaveChangesAsync(ct);
            changed += rows.Count;
        }
        return changed;
    }

    /// <summary>
    /// <see cref="RederivePaymentsAsync"/>; her hatayı yakalar (kilitlenme kurbanı 1205, zaman aşımı, iptal, programlama
    /// hatası): bağ çağıranın kaydıyla yazılmıştır, yanıt değişmez, dekont önceki müşterisinde kalır. İzlenen
    /// <see cref="Payment"/> girdileri ayrılır (aynı kapsamdaki sonraki bir kayıt yarım yazımı taşımasın); uyarı logu shopper
    /// ve lisans Id'lerini, istisna türünü ve yığın izini yazar (ileti değil: SQL parametresi taşıyabilir); 0 döner.
    /// </summary>
    public static async Task<int> TryRederivePaymentsAsync(
        LicenseDbContext db, Guid shopperId, IReadOnlyCollection<Guid> licenseIds, ILogger log, CancellationToken ct)
    {
        try
        {
            return await RederivePaymentsAsync(db, shopperId, licenseIds, ct);
        }
        catch (Exception ex)
        {
            foreach (var entry in db.ChangeTracker.Entries<Payment>().ToList()) entry.State = EntityState.Detached;
            log.LogWarning("Dekont yeniden yazımı: shopper {ShopperId} bağlandı ama lisans {LicenseIds} dekontları yeniden "
                + "türetilemedi ({ErrorType}); bağ kayıtlı, yanıt değişmedi. Yığın: {StackTrace}",
                shopperId, licenseIds, ex.GetType().Name, ex.StackTrace);
            return 0;
        }
    }

    /// <summary>
    /// Gönderimin kayıttan sonraki denetimi (S2b planı Karar 11): gönderim müşteriyi kayıttan hemen önce
    /// <see cref="DeriveAsync"/> ile çözer; arada bağı kesen bir yol commit olduysa (silme bağlantıyı siler,
    /// <c>ReproveLinksAsync</c> ve <c>CustomerIdentityMergeJob</c> bağlantıyı boşaltır) <see cref="RederivePaymentsAsync"/>'in
    /// koşulu artık tutmaz ve dekontun müşterisi boşaltılır. Koşul tabloları kilitli okunur: kesme henüz commit olmadıysa denetim
    /// onun commit'ini bekler, RCSI ayarından bağımsız (sınıf özeti). Yalnız çözülen müşteriyi hâlâ taşıyan satıra yazar. Yalnız
    /// ilişkiselde koşar (ham SQL): InMemory'de eşzamanlı yazım yok.
    /// </summary>
    public static async Task ClearIfLinkChangedAsync(
        LicenseDbContext db, Guid paymentId, Guid shopperId, DerivedCustomer d, CancellationToken ct)
    {
        if (!db.Database.IsRelational()) return;
        // RederivePaymentsAsync'in koşulu, aynı kilitli okumayla; eşitlik null-güvenlidir.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE p SET p.[WpfCustomerId] = NULL
            FROM [Payments] AS p
            WHERE p.[Id] = {paymentId}
              AND EXISTS (SELECT p.[WpfCustomerId] INTERSECT SELECT {d.CustomerId})
              AND NOT (EXISTS (SELECT 1 FROM [ShopperBroadcasterLinks] AS l WITH (READCOMMITTEDLOCK)
                               WHERE l.[Id] = {d.LinkId} AND l.[WpfCustomerId] = {d.LinkCustomerId})
                       AND EXISTS (SELECT 1 FROM [Shoppers] AS s WITH (READCOMMITTEDLOCK)
                                   WHERE s.[Id] = {shopperId} AND s.[DeletedAt] IS NULL))
            """, ct);
    }

    /// <summary>Karar 11'in koşulu InMemory yolunda, yazımın kendi süzgecinde: <paramref name="d"/>'de seçilen bağlantı hâlâ
    /// okunan müşteride ve shopper silinmemiş. İlişkisel yol aynı koşulu ham SQL'de kilitli okur (sınıf özeti).</summary>
    private static (IQueryable<ShopperBroadcasterLink> Link, IQueryable<Shopper> Live) StillLinked(
        LicenseDbContext db, Guid shopperId, DerivedCustomer d)
        => (db.ShopperBroadcasterLinks.Where(l => l.Id == d.LinkId && l.WpfCustomerId == d.LinkCustomerId),
            db.Shoppers.Where(s => s.Id == shopperId && s.DeletedAt == null));

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
