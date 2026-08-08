using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrderDeck.App.Services;
using OrderDeck.Core.Catalog;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Core.Time;

namespace OrderDeck.App.ViewModels;

/// <summary>
/// Sağ paneldeki ürün kartı: fotoğraf, ad, beden stoğu.
///
/// Hero'daki kod kutusu her değiştiğinde <see cref="Load"/> çağrılır. Kod
/// tanınmıyorsa kart satır-içi TANIMLAMA moduna düşer — pop-up açılmaz
/// (spec §6: hiçbir şey pop-up değil).
///
/// Kartta FİYAT ALANI YOK: karttaki fiyat hero'daki aktif fiyat girişinin
/// aynısıdır, view onu MainShellViewModel'den bağlar (spec §9.1).
/// </summary>
public sealed partial class ProductCardViewModel : ObservableObject
{
    private readonly ProductRepository _repo;
    private readonly ProductPhotoStore _photos;
    private readonly IClock _clock;

    public ProductCardViewModel(ProductRepository repo, ProductPhotoStore photos, IClock clock)
    {
        _repo = repo;
        _photos = photos;
        _clock = clock;
    }

    public ObservableCollection<ProductSizeViewModel> Sizes { get; } = new();

    [ObservableProperty] private string _code = "";
    [ObservableProperty] private bool _hasProduct;
    [ObservableProperty] private bool _isEditing;

    /// <summary>Beden seti düzenleme kutusu: "S, M, L, XL".</summary>
    [ObservableProperty] private string _sizesText = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private string _name = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PhotoAbsolutePath))]
    private string? _photoPath;

    /// <summary>
    /// Image kaynağı. Dosya silinmişse null → view placeholder gösterir.
    /// </summary>
    public string? PhotoAbsolutePath => _photos.ResolveAbsolute(PhotoPath);

    /// <summary>
    /// Hero'daki kod değişince çağrılır. Boş kod = kart temizlenir; tanınmayan
    /// kod = tanımlama modu; tanınan kod = kayıtlı ürün.
    /// </summary>
    public void Load(string? code)
    {
        var trimmed = (code ?? "").Trim();
        Code = trimmed;

        if (trimmed.Length == 0) { Reset(hasProduct: false, editing: false); return; }

        var product = _repo.Get(trimmed);
        if (product is null)
        {
            Reset(hasProduct: false, editing: true);
            return;
        }

        Name = product.Name;
        PhotoPath = product.PhotoPath;
        LoadSizes(_repo.GetSizes(trimmed));
        HasProduct = true;
        IsEditing = false;
    }

    /// <summary>
    /// <see cref="SizesText"/>'i ızgaraya uygular. Hayatta kalan bedenlerin
    /// adedi korunur — operatör "S,M" → "M,L" düzeltmesi yaparken M'nin
    /// adedini yeniden yazmak zorunda kalmamalı.
    /// </summary>
    public void ApplySizesText()
    {
        var existing = Sizes.ToDictionary(s => s.Size, StringComparer.OrdinalIgnoreCase);

        var wanted = SizesText
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        Sizes.Clear();
        for (var i = 0; i < wanted.Count; i++)
        {
            existing.TryGetValue(wanted[i], out var prev);
            Sizes.Add(new ProductSizeViewModel(wanted[i], prev?.Quantity ?? 0, i));
        }
    }

    /// <summary>Seçilen dosyayı depoya kopyalar (dosya seçme diyaloğu view'da).</summary>
    public void SetPhoto(string sourcePath)
    {
        if (Code.Length == 0) return;
        PhotoPath = _photos.Save(Code, sourcePath);
    }

    [RelayCommand]
    private void BeginEdit()
    {
        SizesText = string.Join(", ", Sizes.Select(s => s.Size));
        IsEditing = true;
    }

    private bool CanSave() => Code.Length > 0 && !string.IsNullOrWhiteSpace(Name);

    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Save()
    {
        // Unix SANİYE — repo'daki her zaman damgası IClock ile aynı birimde
        // (bkz. OrderDeck.Core/Time/IClock.cs).
        _repo.Save(
            new Product(Code, Name.Trim(), PhotoPath, _clock.UnixNow()),
            Sizes.Select((s, i) => new ProductSize(Code, s.Size, s.Quantity, i)).ToList());

        HasProduct = true;
        IsEditing = false;
    }

    /// <summary>Düzenlemeyi at, diskteki hâle dön.</summary>
    [RelayCommand]
    private void CancelEdit() => Load(Code);

    private void Reset(bool hasProduct, bool editing)
    {
        Name = "";
        PhotoPath = null;
        SizesText = "";
        Sizes.Clear();
        HasProduct = hasProduct;
        IsEditing = editing;
    }

    private void LoadSizes(IReadOnlyList<ProductSize> sizes)
    {
        Sizes.Clear();
        foreach (var s in sizes) Sizes.Add(new ProductSizeViewModel(s.Size, s.Quantity, s.SortOrder));
        SizesText = string.Join(", ", sizes.Select(s => s.Size));
    }
}
