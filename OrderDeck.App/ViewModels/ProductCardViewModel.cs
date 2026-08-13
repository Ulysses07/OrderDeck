using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using OrderDeck.App.Services;
using OrderDeck.Core.Storage.Repositories;

namespace OrderDeck.App.ViewModels;

/// <summary>
/// Sağ paneldeki ürün kartı. Kaynağı sunucu kataloğunun yerel replikası
/// (<see cref="CatalogReplicaRepository"/>), yazma yolu YOK.
///
/// Neden salt okunur: katalogun tek sahibi panel. Operatör burada ürün
/// tanımlayabilseydi aynı ürünün iki ayrı gerçeği olurdu (yerelde tanımlı,
/// sunucuda yok) ve stok hareketi hangi ürüne yazılacağı belirsizleşirdi.
///
/// Üç durum: kod yok (boş kart) · kod var ama katalogda yok
/// (<see cref="IsUnknown"/>) · kod katalogda var (<see cref="HasProduct"/>).
/// Bilinmeyen kod bir <b>hata değil</b>: operatör kodu yazarken her ara tuş
/// vuruşu tanınmayan bir koddur, akış kesilmez.
/// </summary>
public sealed partial class ProductCardViewModel : ObservableObject
{
    private readonly CatalogReplicaRepository _repo;
    private readonly CatalogPhotoCache _photos;

    public ProductCardViewModel(CatalogReplicaRepository repo, CatalogPhotoCache photos)
    {
        _repo = repo;
        _photos = photos;
    }

    public ObservableCollection<CatalogVariantViewModel> Variants { get; } = new();

    [ObservableProperty]
    private string _code = string.Empty;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private bool _hasProduct;

    [ObservableProperty]
    private bool _isUnknown;

    /// <summary>R2 nesne anahtarı; dosya yolu değil.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PhotoAbsolutePath))]
    private string? _coverPhotoKey;

    /// <summary>
    /// Önbellekte dosya yoksa <c>null</c> — Image bağı boş kalır, kart
    /// bozulmaz. Senkron fotoğrafı indirince sonraki <see cref="Load"/>
    /// yolu doldurur.
    /// </summary>
    public string? PhotoAbsolutePath => _photos.ResolveAbsolute(CoverPhotoKey);

    /// <summary>
    /// Kartı verilen ürün koduna göre tazeler. Kod büyük/küçük harf ve Türkçe
    /// harf farkından bağımsız aranır (<c>SearchNormalizer</c> hem replikaya
    /// yazarken hem burada uygulanıyor).
    /// </summary>
    public void Load(string? code)
    {
        var trimmed = (code ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            Reset(string.Empty, unknown: false);
            return;
        }

        var product = _repo.FindByCode(trimmed);
        if (product is null)
        {
            Reset(trimmed, unknown: true);
            return;
        }

        Code = product.Code;
        Name = product.Name;
        CoverPhotoKey = product.CoverPhotoKey;
        HasProduct = true;
        IsUnknown = false;

        Variants.Clear();
        foreach (var v in _repo.GetVariants(product.Id))
        {
            // Pasif varyant gösterilmez: satılamayacak bir kırılım karta
            // girerse operatör onu okutmayı dener.
            if (v.IsActive) Variants.Add(new CatalogVariantViewModel(v));
        }
    }

    private void Reset(string code, bool unknown)
    {
        Code = code;
        Name = string.Empty;
        CoverPhotoKey = null;
        HasProduct = false;
        IsUnknown = unknown;
        Variants.Clear();
    }
}
