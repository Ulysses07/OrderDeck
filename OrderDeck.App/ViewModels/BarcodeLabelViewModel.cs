using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using OrderDeck.Core.Catalog;
using OrderDeck.Labeling;

namespace OrderDeck.App.ViewModels;

/// <summary>
/// "Etiket bas" çekmecesi. Kartta AÇIK olan ürünün varyantlarını listeler,
/// operatör hangilerini kaç kez basacağını seçer.
///
/// <para><b>Yazıcıya dokunmuyor:</b> yalnız <see cref="BuildLabels"/> ile
/// yükü hazırlıyor. Basma, çekmecenin arkasındaki kod-behind'de
/// <c>BarcodeLabelPrinter</c> ile yapılıyor — böylece bu sınıf
/// <c>System.Drawing.Printing</c>'e ve Windows'a bağlanmadan test edilebiliyor.</para>
/// </summary>
public sealed partial class BarcodeLabelViewModel : ObservableObject
{
    public ObservableCollection<BarcodeLabelRow> Rows { get; } = new();

    [ObservableProperty]
    private string _productName = string.Empty;

    /// <summary>Etiket başına kopya. 1: operatör çoğunlukla tek parça etiketliyor.</summary>
    [ObservableProperty]
    private int _copies = 1;

    public bool CanPrint => Rows.Any(r => r.IsSelected);

    public void Load(BroadcastCodeResolution? resolution)
    {
        // Temizlik şart: aynı görünüm modeli ikinci bir ürünle yüklenirse
        // önceki ürünün satırları kalır ve operatör YANLIŞ ürüne etiket basar
        // — çıktı gözle kusursuz göründüğü için hata rafta anlaşılır.
        Rows.Clear();
        ProductName = resolution?.Product.Name ?? string.Empty;
        if (resolution is null) return;

        foreach (var v in resolution.Variants)
        {
            // Barkodsuz varyant sunucuda var olamaz; yine de replikada bayat
            // bir satır olabilir (senkron turu gelmemiş). Basılamayanı
            // listeye almıyoruz — boş barkodla etiket basmak okunamayan
            // bir çıktı üretirdi.
            if (string.IsNullOrWhiteSpace(v.Barcode)) continue;
            var row = new BarcodeLabelRow(v.Barcode!, Describe(v));
            row.PropertyChanged += (_, _) => OnPropertyChanged(nameof(CanPrint));
            Rows.Add(row);
        }

        OnPropertyChanged(nameof(CanPrint));
    }

    public IReadOnlyList<BarcodeLabelDocument.Label> BuildLabels() =>
        Rows.Where(r => r.IsSelected)
            .Select(r => new BarcodeLabelDocument.Label(r.Barcode, ProductName, r.Display))
            .ToList();

    private static string Describe(CatalogVariant v)
    {
        var parts = new[] { v.Axis1Value, v.Axis2Value }
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p!.Trim());
        return string.Join(" · ", parts);
    }
}

public sealed partial class BarcodeLabelRow : ObservableObject
{
    public BarcodeLabelRow(string barcode, string display)
    {
        Barcode = barcode;
        Display = display;
    }

    public string Barcode { get; }
    public string Display { get; }

    /// <summary>Varsayılan seçili: operatör çoğunlukla hepsini basıyor.</summary>
    [ObservableProperty]
    private bool _isSelected = true;
}
