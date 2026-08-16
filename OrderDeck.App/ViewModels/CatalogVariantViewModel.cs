using CommunityToolkit.Mvvm.ComponentModel;
using OrderDeck.Core.Catalog;

namespace OrderDeck.App.ViewModels;

/// <summary>
/// Ürün kartındaki tek varyant rozeti: üstte eksen değeri, altında bakiye.
///
/// <para>Eksen değerleri kartın ömrü boyunca değişmiyor (her <c>Load</c>
/// koleksiyonu baştan kuruyor), ama <see cref="Quantity"/> değişiyor: senkron
/// turu ya da yeni bir sipariş bakiyeyi kaydırıyor ve rozet <b>yerinde</b>
/// tazeleniyor. Bu yüzden tür artık <c>ObservableObject</c>.</para>
///
/// <para><see cref="IsZero"/>/<see cref="IsNegative"/> alan değil, XAML'in
/// kısıtı: <c>DataTrigger</c> yalnız eşitlik kurabiliyor, "0'dan küçük"
/// ifadesi kuramıyor. Sınıflandırmayı burada yapıp XAML'e bool veriyoruz.</para>
/// </summary>
public sealed partial class CatalogVariantViewModel : ObservableObject
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
        VariantId = variant.Id;

        var parts = new[] { variant.Axis1Value, variant.Axis2Value }
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v!.Trim());

        var label = string.Join(" · ", parts);

        // Eksensiz üründe de tam bir varyant var; gösterilecek eksen değeri
        // yoksa çağıranın verdiği etiket (ürünün stok kodu) yazılır.
        Display = label.Length > 0 ? label : fallbackLabel;
    }

    /// <summary>Bakiye aramasının anahtarı; rozette gösterilmiyor.</summary>
    public string VariantId { get; }

    public string Display { get; }

    /// <summary>
    /// Gösterilecek bakiye: sunucu bakiyesi eksi yerel bekleyen etiketler.
    /// <b>Negatif olabilir</b> — stok bitince satış engellenmiyor.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsZero))]
    [NotifyPropertyChangedFor(nameof(IsNegative))]
    private int _quantity;

    /// <summary>0 bilgilendirmedir, uyarı değil — sönük renkte yazılır.</summary>
    public bool IsZero => Quantity == 0;

    /// <summary>Eksi bakiye operatörün görmesi gereken tek anormallik.</summary>
    public bool IsNegative => Quantity < 0;
}
