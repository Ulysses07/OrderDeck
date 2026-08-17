namespace OrderDeck.LicenseServer.Domain;

/// <summary>
/// Katalog kolonlarının uzunluk sınırları — <b>şemanın da doğrulamanın da tek
/// kaynağı</b>.
///
/// Neden burada: sayı iki yerde elle tutulursa (bir <c>HasMaxLength</c>'te, bir
/// controller'daki <c>if</c>'te) sessizce ayrışır. Ayrışmayı da testler
/// yakalayamaz: testler EF <b>InMemory</b> üstünde koşuyor ve InMemory
/// <c>HasMaxLength</c>'i tamamen yok sayıyor — yani sınırı aşan girdi testte
/// yeşil, prod'da (SQL Server) <c>DbUpdateException</c> → 500 oluyordu.
/// Bu yüzden hem <c>LicenseDbContext.OnModelCreating</c> hem de istek DTO'ları
/// aynı sabitleri okur; <c>Data.CatalogModelTests</c> ikisinin eşitliğini
/// EF model metadata'sından doğrular.
///
/// DİKKAT: buradaki bir sayıyı değiştirmek ŞEMAYI değiştirir → yeni migration
/// gerekir. Sayılar bugünkü migration ile birebir aynı olmak zorunda.
/// </summary>
public static class CatalogLimits
{
    public const int CategoryName = 120;

    /// <summary>
    /// <c>/{id:N}/{id:N}/…</c> biçimindeki yol. Genişletmek serbest değil:
    /// <c>Path</c>, <c>(LicenseId, Path)</c> indeksinin parçası ve SQL Server'ın
    /// kümelenmemiş indeks anahtarı 1700 bayt ile sınırlı — <c>nvarchar(512)</c>
    /// zaten 1024 bayt tutuyor.
    /// </summary>
    public const int CategoryPath = 512;

    public const int ProductCode = 32;
    public const int ProductName = 200;

    /// <summary>
    /// Raf/konum etiketi — serbest metin ("A-3 / 2", "Depo arka raf").
    /// 40 karakter: rozet olarak ürün ızgarasında tek satırda görünmeli.
    /// </summary>
    public const int ShelfLocation = 40;

    /// <summary>
    /// Türetilmiş: <c>Normalize(Name)</c>. Katlama 1:1, boşluk sıkıştırma
    /// yalnız kısaltır — yani <c>Name</c>'i asla aşamaz.
    /// </summary>
    public const int NameSearch = ProductName;
    public const int AxisName = 40;
    public const int PhotoObjectKey = 512;
    public const int PhotoContentType = 100;

    /// <summary>
    /// Ürün başına azami fotoğraf. 4: ürün kartında tek sırada sığıyor, R2
    /// maliyetini ve yükleme süresini öngörülebilir tutuyor. Sınır sunucuda
    /// zorlanır — panel yalnız butonu gizler.
    /// </summary>
    public const int MaxProductPhotos = 4;
    public const int AxisValue = 60;

    /// <summary>
    /// Tek toplu istekte yazılabilecek azami varyant. Gerçek ihtiyaç 12–20
    /// (3 renk × 4 beden); 200, elle kurulan uç senaryolara bile fazlasıyla
    /// yeterken tek isteğin veritabanını kilitleyerek kötüye kullanılmasını
    /// engelliyor.
    /// </summary>
    public const int MaxBulkVariants = 200;

    /// <summary>
    /// Barkod alanının SAKLAMA sınırı — basılabilirlik sınırı DEĞİL.
    ///
    /// <para>Sistemin kendi ayırdığı barkod 10 hane; alan bu kadar cömert
    /// çünkü elle geçirilen tedarikçi barkodları (EAN-13, UPC) buraya
    /// yazılıyor. 64, o senaryolara fazlasıyla yeterken alanı da tek bir
    /// indeks sayfasına sığacak kadar küçük tutuyor.</para>
    ///
    /// <para><b>Bilinçli boşluk:</b> 60 mm etikete Code128 ile ancak 8
    /// ALFANÜMERİK karakter giriyor (sayı dizileri Code128-C ile çift çift
    /// sıkıştığı için 10 hane ≈ 44 mm, EAN-13 ≈ 57 mm — ikisi de sığar).
    /// Sunucu bunu zorlayamaz: etiket ölçüsü operatörün masaüstü ayarında ve
    /// 10-200 mm arası değişebiliyor, sunucu o değeri hiç görmüyor. Zorlarsa
    /// ya 100 mm etiketi olan yayıncıyı boşuna keser ya da 20 mm'liye yalan
    /// söyler. Bu yüzden sınır basım anında, kâğıt hiç hareket etmeden ve ne
    /// yapılacağını söyleyerek zorlanıyor — bkz. <c>BarcodeLabelDocument
    /// .EncodeForLabel</c> ve <c>EnsureLabelHeightFits</c>.</para>
    /// </summary>
    public const int Barcode = 64;

    /// <summary>
    /// Yayın kodu (<c>ProductBroadcastCode.Code</c>) ve normalize hâli.
    /// 32 karakter: operatör bunu canlı yayında sesli söylüyor ve izleyici
    /// yoruma yazıyor — pratikte 3-8 karakter. Tavan cömert bırakıldı ki
    /// "ATEŞ KIRMIZI" gibi iki kelimelik kodlar da sığsın.
    ///
    /// Normalize hâlin sınırı ham hâlle AYNI olmalı: <c>SearchNormalizer</c>
    /// karakter atmıyor (yalnız büyütüp katlıyor, boşlukları sadeleştiriyor),
    /// yani normalize hâl asla ham hâlden uzun olamaz.
    /// </summary>
    public const int BroadcastCode = 32;

    /// <summary>
    /// Stok hareketi notu ("mal kabul irsaliye 4412", "sayım farkı"). Serbest
    /// metin; 200 karakter panelde tek satırda okunabilir kalıyor.
    /// </summary>
    public const int MovementNote = 200;

    /// <summary>
    /// Kategori ağacının azami seviye sayısı (kök = 1).
    ///
    /// Neden gerekli: <c>Path</c> seviye başına 33 karakter büyüyor
    /// (<c>{guid:N}</c> + "/"), artı baştaki "/". Yani 16 seviye 529 karakter
    /// eder ve <c>nvarchar(512)</c>'yi taşırır → SQL Server kesme hatası → 500.
    /// Kolonu büyütmek çıkış yolu değil (bkz. <see cref="CategoryPath"/>),
    /// o yüzden derinlik API'de kapatılıyor.
    ///
    /// Neden 12: 1 + 33*12 = 397 karakter, 512'nin rahatça içinde; perakende
    /// hiyerarşisi (Erkek &gt; Üst Giyim &gt; Tişört) üç seviye — 12 fazlasıyla
    /// yetiyor.
    /// </summary>
    public const int CategoryMaxDepth = 12;
}
