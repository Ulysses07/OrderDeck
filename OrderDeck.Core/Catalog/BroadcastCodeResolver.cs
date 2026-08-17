using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Shared.Text;

namespace OrderDeck.Core.Catalog;

/// <summary>
/// Yayın kodunu çözer. Operatörün kod kutusuna yazdığı kod ürünü <b>ve</b> satıcı
/// ekseninin değerini birlikte belirler; geriye kalan tek serbestlik izleyici
/// ekseni olur — yorumdan çıkarılacak şey tam olarak odur.
///
/// Hem ürün kartı hem sipariş akışı bu sınıfı kullanır: kartta görünen varyant
/// listesiyle çekmecede seçilebilen değerlerin ayrışması mümkün olmamalı.
/// </summary>
public sealed class BroadcastCodeResolver
{
    // Sunucudaki AxisRole enum'ının sayısal karşılıkları (Product.cs).
    // Replika bunları ham int olarak taşıyor; paylaşılan bir enum yok.
    private const int SellerRole = 1;
    private const int ViewerRole = 2;

    private readonly CatalogReplicaRepository _repo;

    public BroadcastCodeResolver(CatalogReplicaRepository repo) => _repo = repo;

    /// <summary>
    /// Kutuya yazılan/okutulan metni çözer.
    ///
    /// <para><b>Sıra:</b> önce yayın kodu, sonra barkod — operatörün ağzından
    /// çıkan kod, elindeki parçadan önce gelir.</para>
    ///
    /// <para><b>Bu sıra estetik değil, TAŞIYICI.</b> Çakışmada kod kazanıyor,
    /// yani barkodun sahibi ürüne bu yolla ERİŞİLEMEZ olur. Sırayı çevirmek
    /// daha kötüsünü yapardı: operatörün söylediği koda başkasının etiketi
    /// gölge düşürürdü. Sıra bir testle sabit
    /// (<c>Yayin_kodu_barkoda_gore_oncelikli</c>).</para>
    ///
    /// <para><b>Çakışma sunucuda ÖNLENİYOR:</b> lisans içinde iki uzay ayrık
    /// tutuluyor — sayaç barkodunun biçimi (10 haneli saf sayı) yayın kodu
    /// olarak yasak, elle yazılan barkod var olan bir kodla aynı olamıyor
    /// (<c>barcode-shadows-broadcast-code</c>), yayın kodu da var olan bir
    /// barkodla aynı olamıyor (<c>code-shadows-barcode</c>). Bekçiler geriye
    /// dönük DEĞİL: onlardan önce yazılmış veride çakışma hâlâ mümkün, bu
    /// yüzden yukarıdaki öncelik kuralı yaşamaya devam ediyor.</para>
    ///
    /// <para><b>İki arama aynı kutuda buluşuyor ama farklı eşleşme kuralları
    /// kullanıyor:</b> yayın kodu NORMALİZE edilerek aranıyor (büyük harf +
    /// Türkçe katlama), barkod BİREBİR (opak yük, gerekçesi
    /// <c>CatalogReplicaRepository.FindVariantByBarcode</c>'da). Sonuç: kod
    /// tarafı geniş, barkod tarafı dar. Sayaç barkodları saf rakam olduğu
    /// için etkilenmiyor; elle yazılan <c>abc-12</c> barkodu ise kutuya
    /// <c>ABC-12</c> diye yazılırsa eşleşmez.</para>
    /// </summary>
    public BroadcastCodeResolution? Resolve(string? code)
    {
        var hit = _repo.FindBroadcastCode(code) ?? ResolveByBarcode(code);
        if (hit is null) return null;

        var product = _repo.GetProductById(hit.ProductId);
        // Kod var ama ürün yok: replika tutarsız (olmaması gereken durum, ama
        // replika UNIQUE/FK kurmadığı için imkânsız değil). Sessizce
        // "bilinmeyen kod" davranışına düşüyoruz — çökmek yerine.
        if (product is null) return null;

        var sellerAxis = AxisIndexOf(product, SellerRole);
        var viewerAxis = AxisIndexOf(product, ViewerRole);

        var variants = _repo.GetVariants(product.Id)
            .Where(v => v.IsActive)
            .Where(v => sellerAxis == 0
                     || Same(AxisValue(v, sellerAxis), hit.SellerAxisValue))
            .ToList();

        var viewerValues = viewerAxis == 0
            ? Array.Empty<string>()
            : variants.Select(v => AxisValue(v, viewerAxis))
                      .Where(v => !string.IsNullOrWhiteSpace(v))
                      .Select(v => v!.Trim())
                      // Sıra varyant sırasından gelir (SortOrder), alfabetik
                      // değil: sunucudaki sıralama tek doğru kaynak.
                      .Distinct(StringComparer.OrdinalIgnoreCase)
                      .ToArray();

        return new BroadcastCodeResolution(
            product,
            hit.Code,
            hit.SellerAxisValue,
            viewerAxis == 0 ? null : AxisName(product, viewerAxis),
            viewerAxis,
            variants,
            viewerValues);
    }

    /// <summary>
    /// Barkodu bir <see cref="CatalogBroadcastCode"/>'a indirger.
    ///
    /// <para><b>Neden koda indirgeniyor:</b> barkod bir varyantı gösteriyor
    /// ama kartın ve sipariş akışının tamamı bir yayın kodu üzerinden
    /// çalışıyor. Barkodu koda çevirince aşağıdaki gövde — varyant süzme,
    /// izleyici ekseni, stok rozetleri — hiç değişmeden çalışıyor. Ayrı bir
    /// çözümleme dalı yazsaydık iki yol zamanla ayrışırdı.</para>
    ///
    /// <para><b>Pasif varyant reddedilir.</b> <c>FindVariantByBarcode</c>
    /// pasif varyantı da döndürüyor (kararı çağırana bırakıyor) — karar
    /// burada: reddetmek. Kabul etseydik kart açılır ama okutulan kırılım
    /// listede olmazdı (gövde <c>IsActive</c> süzüyor); operatör kart
    /// açıldığı için parçanın satılabilir olduğuna inanıp kodu izleyicilere
    /// söyler, hata ancak yorumdan gelen sipariş varyanta çevrilemediğinde —
    /// yani yayının ortasında, izleyici parçayı çoktan istemişken — ortaya
    /// çıkardı. <c>null</c> dönmek operatörü yanlış anda değil, güvenli anda
    /// durduruyor. <c>FindVariantByBarcode</c>'un doc'u tam da bunun bedelini
    /// uyarıyor — "sessizce bulunamadı demek operatörü etiketin bozuk olduğuna
    /// inandırır" — ve o bedel burada BİLEREK kabul ediliyor: yanlış kırılımla
    /// yayına devam etmek, bir etiketi boşuna şüpheli saymaktan pahalı.
    /// Mesajı düzeltmenin yolu <see cref="BroadcastCodeResolution"/>'a bir
    /// sebep alanı eklemekten geçiyor ve bu kapsamın dışında.</para>
    ///
    /// <para><b>Kodu olmayan ürün reddedilir</b> (<c>null</c> → kartta
    /// "katalogda yok"): kart yayın kodunu gösteriyor, kodu olmayan bir ürünü
    /// açmak operatöre izleyicilere söyleyecek kodu olmayan bir ürün
    /// göstermek olurdu.</para>
    ///
    /// <para><b>İlk eşleşen kod = GÜNCEL kod, sıralamaya bağımlı.</b> Bir
    /// ürünün aynı kırılımında birden çok kod satırı olabilir: kod değişikliği
    /// güncelleme değil YENİ SATIR (eskisi kodu rezerve tutmaya devam eder) ve
    /// emekli satırlar da WPF'e iniyor. Buradaki <c>codes[0]</c> /
    /// <c>FirstOrDefault</c> güncel kodu veriyor çünkü zincirin tamamı öyle
    /// kurulu: sunucu <c>CreatedAt</c> AZALAN gönderir → <c>CatalogSyncService</c>
    /// dizi indeksini <c>SortOrder</c> yapar → <c>GetBroadcastCodes</c>
    /// <c>ORDER BY SortOrder</c> der. <b>O <c>ORDER BY</c> gösterim uğruna
    /// değiştirilirse</b> (ör. <c>ORDER BY Code</c>) burası sessizce EMEKLİ bir
    /// kodu karta yazar; operatör izleyicilere panelde artık görünmeyen bir kod
    /// söyler. Kural teste bağlı: <c>Barkod_emekli_degil_guncel_kodu_verir</c>.</para>
    ///
    /// <para>Satıcı ekseni eşleşmesi C#'ta, <see cref="Same"/> ile: SQLite'ta
    /// Türkçe katlama yok, SQL'de karşılaştırmak "İ/ı" çiftlerini kaçırırdı.</para>
    /// </summary>
    private CatalogBroadcastCode? ResolveByBarcode(string? barcode)
    {
        var variant = _repo.FindVariantByBarcode(barcode);
        if (variant is null || !variant.IsActive) return null;

        var product = _repo.GetProductById(variant.ProductId);
        if (product is null) return null;

        var codes = _repo.GetBroadcastCodes(product.Id);
        if (codes.Count == 0) return null;

        var sellerAxis = AxisIndexOf(product, SellerRole);
        if (sellerAxis == 0) return codes[0];

        var sellerValue = AxisValue(variant, sellerAxis);
        return codes.FirstOrDefault(c => Same(c.SellerAxisValue, sellerValue));
    }

    private static int AxisIndexOf(CatalogProduct p, int role) =>
        p.Axis1Role == role ? 1 : p.Axis2Role == role ? 2 : 0;

    internal static string? AxisValue(CatalogVariant v, int axis) =>
        axis == 1 ? v.Axis1Value : axis == 2 ? v.Axis2Value : null;

    private static string? AxisName(CatalogProduct p, int axis) =>
        axis == 1 ? p.Axis1Name : axis == 2 ? p.Axis2Name : null;

    internal static bool Same(string? a, string? b) =>
        SearchNormalizer.Normalize(a) == SearchNormalizer.Normalize(b);
}

/// <summary>Çözülmüş yayın kodu; kart ve sipariş akışı bunu paylaşır.</summary>
/// <param name="Code">Operatörün yazdığı kodun kanonik hâli ("Ateş").</param>
/// <param name="ViewerAxisIndex">1, 2 veya 0 (izleyici ekseni yok).</param>
/// <param name="Variants">Satıcı ekseni değerine göre süzülmüş aktif varyantlar.</param>
/// <param name="ViewerAxisValues">Varyant sırasında, tekilleştirilmiş izleyici değerleri.</param>
public sealed record BroadcastCodeResolution(
    CatalogProduct Product,
    string Code,
    string? SellerAxisValue,
    string? ViewerAxisName,
    int ViewerAxisIndex,
    IReadOnlyList<CatalogVariant> Variants,
    IReadOnlyList<string> ViewerAxisValues)
{
    public bool HasViewerAxis => ViewerAxisIndex != 0;

    private bool HasAnyAxis => Product.Axis1Name is not null || Product.Axis2Name is not null;

    /// <summary>
    /// Sipariş satırına yazılacak varyant kimliği.
    /// <list type="bullet">
    /// <item>Ürünün hiç ekseni yoksa <b>null</b> — stok ürün düzeyinden düşer
    /// (kabul kriteri 11). Replikada tek bir varyant satırı olsa bile bilerek
    /// null: panel de o ürünü "Ürün geneli" kovasında gösteriyor.</item>
    /// <item>Yalnız satıcı ekseni varsa süzme zaten tek varyant bırakır.</item>
    /// <item>İzleyici ekseni varsa değer varyanta çevrilir.</item>
    /// </list>
    /// </summary>
    public string? ResolveVariantId(string? viewerAxisValue)
    {
        if (!HasAnyAxis) return null;

        if (!HasViewerAxis)
            return Variants.Count == 1 ? Variants[0].Id : null;

        if (string.IsNullOrWhiteSpace(viewerAxisValue)) return null;

        return Variants.FirstOrDefault(v =>
            BroadcastCodeResolver.Same(
                BroadcastCodeResolver.AxisValue(v, ViewerAxisIndex), viewerAxisValue))?.Id;
    }
}
