using OrderDeck.Core.Catalog;

namespace OrderDeck.App.ViewModels;

/// <summary>
/// Ürün kartındaki tek varyant satırı. <b>Salt okunur</b> ve
/// <c>ObservableObject</c> değil: replikadan gelen satır kartın ömrü boyunca
/// değişmiyor, her <c>Load</c> koleksiyonu baştan kuruyor. Adet alanı YOK —
/// bakiyeler stok defterinden gelecek (plan 3).
/// </summary>
public sealed class CatalogVariantViewModel
{
    /// <param name="fallbackLabel">
    /// Eksen değeri olmayan varyantta rozette gösterilecek metin; çağıran
    /// ürünün stok kodunu geçiyor (panelin <c>variantLabel(v, product.code)</c>
    /// davranışının aynısı). Sabit bir yedek yerine parametre olmasının sebebi
    /// bilinçli: bu görünüm modeli ürünü hiç görmüyor, elindeki tek şey varyant
    /// satırı — kodu ancak dışarıdan alabilir.
    /// </param>
    public CatalogVariantViewModel(CatalogVariant variant, string fallbackLabel)
    {
        var parts = new[] { variant.Axis1Value, variant.Axis2Value }
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v!.Trim());

        var label = string.Join(" · ", parts);

        // Eksensiz üründe de tam bir varyant var; gösterilecek eksen değeri
        // yoksa çağıranın verdiği etiket (ürünün stok kodu) yazılır.
        Display = label.Length > 0 ? label : fallbackLabel;
    }

    public string Display { get; }
}
