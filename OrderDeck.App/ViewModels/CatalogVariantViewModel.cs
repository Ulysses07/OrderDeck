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
    public CatalogVariantViewModel(CatalogVariant variant)
    {
        var parts = new[] { variant.Axis1Value, variant.Axis2Value }
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v!.Trim());

        var label = string.Join(" · ", parts);

        // Eksensiz üründe de tam bir varyant var; gösterilecek değer yoksa
        // ürün Id'sini fallback olarak kullanıyoruz (Task 4'te gözden geçirilecek).
        Display = label.Length > 0 ? label : variant.Id;
    }

    public string Display { get; }
}
