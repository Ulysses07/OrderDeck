using System;
using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using OrderDeck.App.Services;
using OrderDeck.Core.Catalog;

namespace OrderDeck.App.ViewModels;

/// <summary>
/// Sağ paneldeki ürün kartı. Kaynağı replikanın üstündeki
/// <see cref="BroadcastCodeResolver"/>; yazma yolu YOK. Kod kutusu bir
/// <b>yayın kodu</b> kutusudur: operatörün yazdığı kod hem ürünü hem satıcı
/// ekseninin değerini birlikte belirler, kart da ikisini birlikte gösterir.
///
/// Neden salt okunur: katalogun tek sahibi panel. Operatör burada ürün
/// tanımlayabilseydi aynı ürünün iki ayrı gerçeği olurdu (yerelde tanımlı,
/// sunucuda yok) ve stok hareketi hangi ürüne yazılacağı belirsizleşirdi.
///
/// Üç durum: kod yok (boş kart) · kod çözülemedi
/// (<see cref="IsUnknown"/>) · kod çözüldü (<see cref="HasProduct"/>).
/// Bilinmeyen kod bir <b>hata değil</b>: operatör kodu yazarken her ara tuş
/// vuruşu tanınmayan bir koddur, akış kesilmez.
/// </summary>
public sealed partial class ProductCardViewModel : ObservableObject
{
    private readonly BroadcastCodeResolver _resolver;
    private readonly CatalogPhotoCache _photos;

    /// <summary>
    /// Kartı yaratan iş parçacığının dispatcher'ı — üretimde UI thread'i
    /// (<c>MainShellViewModel</c> aynı kalıbı kullanıyor ve bu kartı kendi
    /// bağımlılığı olarak orada örnekletiyor). Fotoğraf haberi senkronun arka
    /// plan iş parçacığından geldiği için bağ güncellemesi buraya taşınmalı.
    /// </summary>
    private readonly Dispatcher _dispatcher;

    public ProductCardViewModel(BroadcastCodeResolver resolver, CatalogPhotoCache photos)
    {
        _resolver = resolver;
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

    // IsUnknown Code'u okuyor: doğrudan bir Code ataması haber vermeseydi
    // "katalogda yok" bloğunun görünürlüğü bayat kalırdı.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUnknown))]
    private string _code = string.Empty;

    [ObservableProperty]
    private string _name = string.Empty;

    // Alan DEĞİL, hesaplanan: iki bayrak da tek gerçekten (_resolution)
    // türüyor, birbirinden ya da çözümlemeden ayrışmaları imkânsız.
    public bool HasProduct => _resolution is not null;
    public bool IsUnknown => Code.Length > 0 && _resolution is null;

    /// <summary>Kartta ürün adının ardına eklenen " · Siyah" son eki; yoksa boş.</summary>
    [ObservableProperty]
    private string _sellerAxisSuffix = string.Empty;

    /// <summary>Çözülmüş kod; sipariş akışı bunu kart üzerinden okur.</summary>
    public BroadcastCodeResolution? Resolution => _resolution;
    private BroadcastCodeResolution? _resolution;

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
    /// Kod kutusundaki metni <b>yayın kodu</b> olarak çözer. Stok kodu burada
    /// bilerek aranmaz: stok kodu depo/raf dilidir, yayın kodu yayın dilidir;
    /// ikisini aynı kutuda kabul etmek "SK00001" yazan operatöre yanlış ürünü
    /// açabilirdi.
    /// </summary>
    public void Load(string? code)
    {
        var trimmed = (code ?? string.Empty).Trim();

        _resolution = trimmed.Length == 0 ? null : _resolver.Resolve(trimmed);

        // Kod çözüldüyse kartta KATALOĞUN yazımı durur ("ates" yazana "Ateş"
        // gösterilir), çözülmediyse operatörün yazdığı ham metin. Kanonik hâli
        // göstermek onayın kendisi: operatör kodun tanındığını yazımdan anlar,
        // ekrandan okuyup panele/kargo formuna geçirdiğinde de doğru olan gider.
        Code = _resolution?.Code ?? trimmed;

        if (_resolution is null)
        {
            Name = string.Empty;
            SellerAxisSuffix = string.Empty;
            CoverPhotoKey = null;
            Variants.Clear();
        }
        else
        {
            Name = _resolution.Product.Name;
            SellerAxisSuffix = string.IsNullOrWhiteSpace(_resolution.SellerAxisValue)
                ? string.Empty
                : $" · {_resolution.SellerAxisValue!.Trim()}";
            CoverPhotoKey = _resolution.Product.CoverPhotoKey;

            Variants.Clear();
            // Süzme ve pasif eleme çözümleyicide yapıldı; kart yalnız gösteriyor.
            foreach (var v in _resolution.Variants)
                Variants.Add(new CatalogVariantViewModel(v, _resolution.Product.Code));
        }

        OnPropertyChanged(nameof(HasProduct));
        OnPropertyChanged(nameof(IsUnknown));
    }
}
