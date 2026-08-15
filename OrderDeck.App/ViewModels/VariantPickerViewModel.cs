using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using OrderDeck.Core.Catalog;

namespace OrderDeck.App.ViewModels;

/// <summary>Çekmecedeki tek satır: bir izleyici ekseni değeri.</summary>
public sealed partial class VariantPickerItemViewModel : ObservableObject
{
    public VariantPickerItemViewModel(string value, bool isChecked)
    {
        Value = value;
        _isChecked = isChecked;
    }

    public string Value { get; }

    [ObservableProperty]
    private bool _isChecked;
}

/// <summary>
/// Varyant seçici çekmecesinin içeriği. Çekmece <b>yalnız</b> kod katalogda
/// çözüldüğünde, üründe izleyici ekseni varken ve eşleşme tek-ve-tam DEĞİLKEN
/// açılır — tek eşleşmede akış kesilmez, operatör hiçbir şey tıklamaz.
/// </summary>
public sealed partial class VariantPickerViewModel : ObservableObject
{
    public VariantPickerViewModel(BroadcastCodeResolution resolution, AxisMatchResult match)
    {
        ProductLine = string.IsNullOrWhiteSpace(resolution.SellerAxisValue)
            ? resolution.Product.Name
            : $"{resolution.Product.Name} · {resolution.SellerAxisValue!.Trim()}";
        AxisName = resolution.ViewerAxisName ?? "";

        Hint = match.Kind switch
        {
            // Kombinasyon bir TAHMİN; operatör onaylamadan sipariş yazılmaz.
            AxisMatchKind.Combination => "Bitişik yazımdan tahmin edildi — onayla.",
            AxisMatchKind.None => "Yorumda beden bulunamadı.",
            _ => "Birden çok beden yazılmış — her biri ayrı satır olur."
        };

        var preselected = new HashSet<string>(match.Values, StringComparer.OrdinalIgnoreCase);
        Items = new ObservableCollection<VariantPickerItemViewModel>(
            resolution.ViewerAxisValues.Select(v =>
                new VariantPickerItemViewModel(v, preselected.Contains(v))));

        foreach (var item in Items)
            item.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(VariantPickerItemViewModel.IsChecked))
                    OnPropertyChanged(nameof(CanConfirm));
            };
    }

    public string ProductLine { get; }
    public string AxisName { get; }
    public string Hint { get; }
    public ObservableCollection<VariantPickerItemViewModel> Items { get; }

    /// <summary>Hiçbir şey işaretli değilken onay düğmesi kapalı: boş sipariş yazılamaz.</summary>
    public bool CanConfirm => Items.Any(i => i.IsChecked);

    /// <summary>Onaylanan değerler; <b>her biri ayrı sipariş satırı</b> olur.</summary>
    public IReadOnlyList<string> SelectedValues =>
        Items.Where(i => i.IsChecked).Select(i => i.Value).ToList();
}
