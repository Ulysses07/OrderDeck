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

    public BroadcastCodeResolution? Resolve(string? code)
    {
        var hit = _repo.FindBroadcastCode(code);
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
