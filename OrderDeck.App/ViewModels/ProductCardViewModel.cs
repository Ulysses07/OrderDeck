using System;
using System.Collections.ObjectModel;
using System.Windows.Threading;
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

    /// <summary>
    /// Kartı yaratan iş parçacığının dispatcher'ı — üretimde UI thread'i
    /// (<c>MainShellViewModel</c> aynı kalıbı kullanıyor ve bu kartı kendi
    /// bağımlılığı olarak orada örnekletiyor). Fotoğraf haberi senkronun arka
    /// plan iş parçacığından geldiği için bağ güncellemesi buraya taşınmalı.
    /// </summary>
    private readonly Dispatcher _dispatcher;

    public ProductCardViewModel(CatalogReplicaRepository repo, CatalogPhotoCache photos)
    {
        _repo = repo;
        _photos = photos;
        _dispatcher = Dispatcher.CurrentDispatcher;

        // Abonelikten ÇIKILMIYOR: ikisi de DI'da singleton (AppHost:
        // CatalogPhotoCache ve ProductCardViewModel) ve kart uygulama boyunca
        // yaşıyor, yani sızacak bir şey yok. Kart bir gün transient olursa bu
        // satır IDisposable ister.
        _photos.PhotoCached += OnPhotoCached;
    }

    /// <summary>
    /// Senkron bir fotoğrafı diske yerleştirdi. Kartın gösterdiği ürünün
    /// fotoğrafıysa yalnız <see cref="PhotoAbsolutePath"/> için haber ver.
    ///
    /// Karşılaştırma dispatcher'a geçmeden ÖNCE, çağıran iş parçacığında:
    /// soğuk önbellekte ilk tur bütün katalogu indiriyor, yüzlerce anahtarın
    /// hepsini UI kuyruğuna atmak yayın sırasında kuyruğu bedavaya şişirirdi.
    /// Bedeli, <c>CoverPhotoKey</c>'i UI thread'inin dışından okumak: değer bir
    /// dize referansı, yarım okunamaz; sonucun bayat olması da zararsız —
    /// kaçırılan haber bir sonraki <see cref="Load"/>'da zaten telafi olur,
    /// fazladan gelen haber ise yalnız getter'ı yeniden hesaplatır.
    /// </summary>
    private void OnPhotoCached(object? sender, string objectKey)
    {
        // Ordinal: önbellek dosya adı anahtarın SHA-256'sı, yani harf farkı
        // BAŞKA bir dosya demek. Harf duyarsız karşılaştırmak, karta ait
        // olmayan bir dosya için haber verirdi.
        if (!string.Equals(objectKey, CoverPhotoKey, StringComparison.Ordinal)) return;

        // InvokeAsync (Invoke değil): senkron turu UI'yı beklemeden devam
        // etsin, kilitlenecek bir yol da kalmasın.
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.InvokeAsync(RaisePhotoPathChanged);
            return;
        }

        RaisePhotoPathChanged();
    }

    // Yalnız fotoğraf yolu: Variants'a ya da başka bir koleksiyona dokunmak,
    // yayın ortasında kartı gereksiz yere yeniden kurardı.
    private void RaisePhotoPathChanged() => OnPropertyChanged(nameof(PhotoAbsolutePath));

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
    /// bozulmaz. Senkron o fotoğrafı indirdiği anda
    /// <see cref="CatalogPhotoCache.PhotoCached"/> haberi geliyor ve kart
    /// yalnız bu özellik için değişiklik duyurusu yapıyor
    /// (<see cref="OnPhotoCached"/>): kutu KENDİLİĞİNDEN dolar, operatörün
    /// başka bir koda gidip geri dönmesi gerekmez. Yeniden <see cref="Load"/>
    /// beklemek yetmezdi — <c>MainShellViewModel</c> Load'u yalnız aktif kod
    /// DEĞİŞİNCE çağırıyor.
    /// </summary>
    public string? PhotoAbsolutePath => _photos.ResolveAbsolute(CoverPhotoKey);

    /// <summary>
    /// Kartı verilen ürün koduna göre tazeler. Kod büyük/küçük harf ve Türkçe
    /// harf farkından bağımsız aranır; normalleştirmeyi bu sınıf DEĞİL,
    /// <see cref="CatalogReplicaRepository.FindByCode"/> yapıyor (iğneyi de
    /// saklanan kolonu da aynı <c>SearchNormalizer</c>'dan geçiriyor).
    /// Buradan giden tek şey kırpılmış ham metin.
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
            if (v.IsActive) Variants.Add(new CatalogVariantViewModel(v, product.Code));
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
