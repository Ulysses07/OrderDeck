using CommunityToolkit.Mvvm.ComponentModel;

namespace OrderDeck.App.ViewModels;

/// <summary>
/// Beden ızgarasının tek karesi. Adet kartta satır-içi düzenlenir; otomatik
/// düşüş YOK (spec §9.1 — Faz 1'de grid yalnız gösterir).
/// </summary>
public sealed partial class ProductSizeViewModel : ObservableObject
{
    /// <summary>
    /// "Az kaldı" eşiği. Mockup'ta amber rozet; 2 ve altı = son parçalar.
    /// Ayarlanabilir yapmıyoruz — stok projesi gelene kadar tek sabit yeter.
    /// </summary>
    public const int LowStockThreshold = 2;

    public ProductSizeViewModel(string size, int quantity, int sortOrder)
    {
        Size = size;
        _quantity = quantity;
        SortOrder = sortOrder;
    }

    public string Size { get; }
    public int SortOrder { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLow))]
    [NotifyPropertyChangedFor(nameof(IsOutOfStock))]
    private int _quantity;

    public bool IsOutOfStock => Quantity <= 0;
    public bool IsLow => Quantity > 0 && Quantity <= LowStockThreshold;
}
