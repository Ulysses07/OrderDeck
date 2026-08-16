using Dapper;
using OrderDeck.Core.Catalog;
using OrderDeck.Shared.Text;

namespace OrderDeck.Core.Storage.Repositories;

/// <summary>
/// Sunucu kataloğunun yerel salt-okunur replikası. Tek yazarı
/// <c>CatalogSyncService</c>; kullanıcı arayüzü buraya asla yazmaz.
/// </summary>
public sealed class CatalogReplicaRepository
{
    private readonly IDbConnectionFactory _factory;

    public CatalogReplicaRepository(IDbConnectionFactory factory) => _factory = factory;

    /// <summary>
    /// Replikayı baştan yazar. <b>Kısmi çağrılmamalı</b>: çağıran, sunucudan
    /// gelen TAM anlık görüntüyü elinde topladıktan sonra bir kez çağırır.
    /// Ağ yarıda koparsa hiç çağrılmaz ve replika eski hâliyle kullanılabilir
    /// kalır — yarım liste yazmak, silinmemiş ürünleri silmek olurdu.
    /// </summary>
    public void Replace(
        IReadOnlyList<CatalogProduct> products,
        IReadOnlyList<CatalogVariant> variants,
        IReadOnlyList<CatalogCategory> categories,
        IReadOnlyList<CatalogBroadcastCode> broadcastCodes)
    {
        using var conn = _factory.Open();
        using var tx = conn.BeginTransaction();

        // Silme sırası önemsiz (FK yok — replikada bütünlüğü sunucu garanti
        // ediyor, yerel cascade kurmak yanlış güven verirdi), ama hepsi AYNI
        // transaction'da: yarı silinmiş bir replika hiç yoktan kötüdür.
        conn.Execute("DELETE FROM CatalogBroadcastCode", transaction: tx);
        conn.Execute("DELETE FROM CatalogVariant", transaction: tx);
        conn.Execute("DELETE FROM CatalogProduct", transaction: tx);
        conn.Execute("DELETE FROM CatalogCategory", transaction: tx);

        // DefaultPrice decimal olarak doğrudan bağlanıyor (ShipmentRepository'deki
        // gibi ara (double) cast'i YOK). Microsoft.Data.Sqlite decimal'i invariant
        // kültürde TEXT olarak bağlar; kolonun REAL affinity'si bunu sayıya çevirir.
        // Çift dönüşüm (decimal→double→REAL) yuvarlama hatası üretebileceğinden bu
        // yaklaşım bilinçli olarak ShipmentRepository'den farklı tutuluyor.
        if (products.Count > 0)
            conn.Execute(
                """
                INSERT INTO CatalogProduct
                    (Id, CategoryId, Code, CodeNormalized, Name, DefaultPrice,
                     ShelfLocation, Axis1Name, Axis1Role, Axis2Name, Axis2Role,
                     CoverPhotoKey, UpdatedAt)
                VALUES
                    (@Id, @CategoryId, @Code, @CodeNormalized, @Name, @DefaultPrice,
                     @ShelfLocation, @Axis1Name, @Axis1Role, @Axis2Name, @Axis2Role,
                     @CoverPhotoKey, @UpdatedAt)
                """,
                products, tx);

        if (variants.Count > 0)
            conn.Execute(
                """
                INSERT INTO CatalogVariant
                    (Id, ProductId, Axis1Value, Axis2Value, Barcode, IsActive, SortOrder)
                VALUES
                    (@Id, @ProductId, @Axis1Value, @Axis2Value, @Barcode, @IsActive, @SortOrder)
                """,
                variants.Select(v => new
                {
                    v.Id, v.ProductId, v.Axis1Value, v.Axis2Value, v.Barcode,
                    IsActive = v.IsActive ? 1 : 0,
                    v.SortOrder
                }).ToList(), tx);

        if (categories.Count > 0)
            conn.Execute(
                """
                INSERT INTO CatalogCategory
                    (Id, ParentCategoryId, Name, Path, SortOrder, IsActive)
                VALUES
                    (@Id, @ParentCategoryId, @Name, @Path, @SortOrder, @IsActive)
                """,
                categories.Select(c => new
                {
                    c.Id, c.ParentCategoryId, c.Name, c.Path, c.SortOrder,
                    IsActive = c.IsActive ? 1 : 0
                }).ToList(), tx);

        if (broadcastCodes.Count > 0)
            conn.Execute(
                """
                INSERT INTO CatalogBroadcastCode
                    (ProductId, SellerAxisValue, Code, CodeNormalized, CreatedAt, SortOrder)
                VALUES
                    (@ProductId, @SellerAxisValue, @Code, @CodeNormalized, @CreatedAt, @SortOrder)
                """,
                broadcastCodes, tx);

        tx.Commit();
    }

    /// <summary>
    /// Operatörün yazdığı kodu bulur. İğne saklanan kolonla <b>aynı</b>
    /// fonksiyondan geçiyor: büyük/küçük harf ve Türkçe harf farkı önemsiz,
    /// ardışık boşluklar sadeleşiyor.
    ///
    /// <c>LIMIT 1</c> savunma amaçlı: indeks unique değil (bkz. göç 025), yani
    /// beklenmedik bir çakışmada arama patlamak yerine ilk satırı verir.
    /// İkincil <c>Id</c> anahtarı, aynı Code'u taşıyan iki satırın sırasını
    /// deterministik yapar.
    /// </summary>
    public CatalogProduct? FindByCode(string? code)
    {
        var needle = SearchNormalizer.Normalize(code);
        if (needle.Length == 0) return null;

        using var conn = _factory.Open();
        return conn.Query<ProductRow>(
            $"SELECT {ProductColumns} FROM CatalogProduct WHERE CodeNormalized = @needle "
          + "ORDER BY Code, Id LIMIT 1",
            new { needle })
            .Select(Map).FirstOrDefault();
    }

    /// <summary>
    /// Operatörün kod kutusuna yazdığı <b>yayın kodunu</b> bulur. İğne saklanan
    /// kolonla aynı normalizasyondan geçiyor: büyük/küçük harf ve Türkçe harf
    /// farkı önemsiz, ardışık boşluklar sadeleşir.
    ///
    /// <c>LIMIT 1</c> savunma amaçlı: indeks unique değil (bkz. göç 027).
    /// Beklenmedik bir çakışmada patlamak yerine sunucunun sırasındaki ilk
    /// (= en yeni) kodu verir; <c>ProductId</c> ikincil anahtarı sırayı
    /// deterministik yapar.
    /// </summary>
    public CatalogBroadcastCode? FindBroadcastCode(string? code)
    {
        var needle = SearchNormalizer.Normalize(code);
        if (needle.Length == 0) return null;

        using var conn = _factory.Open();
        return conn.Query<BroadcastCodeRow>(
            """
            SELECT ProductId, SellerAxisValue, Code, CodeNormalized, CreatedAt, SortOrder
            FROM CatalogBroadcastCode
            WHERE CodeNormalized = @needle
            ORDER BY SortOrder, ProductId LIMIT 1
            """,
            new { needle })
            .Select(r => new CatalogBroadcastCode(
                r.ProductId, r.SellerAxisValue, r.Code, r.CodeNormalized,
                r.CreatedAt, r.SortOrder))
            .FirstOrDefault();
    }

    /// <summary>Yayın kodundan gelen kimlikle ürünü çeker.</summary>
    public CatalogProduct? GetProductById(string productId)
    {
        using var conn = _factory.Open();
        return conn.Query<ProductRow>(
            $"SELECT {ProductColumns} FROM CatalogProduct WHERE Id = @productId LIMIT 1",
            new { productId })
            .Select(Map).FirstOrDefault();
    }

    public IReadOnlyList<CatalogVariant> GetVariants(string productId)
    {
        using var conn = _factory.Open();
        return conn.Query<VariantRow>(
            """
            SELECT Id, ProductId, Axis1Value, Axis2Value, Barcode, IsActive, SortOrder
            FROM CatalogVariant
            WHERE ProductId = @productId
            ORDER BY SortOrder
            """,
            new { productId })
            .Select(r => new CatalogVariant(
                r.Id, r.ProductId, r.Axis1Value, r.Axis2Value,
                r.Barcode, r.IsActive == 1, r.SortOrder))
            .ToList();
    }

    /// <summary>
    /// Barkodu birebir eşleşen varyant, yoksa <c>null</c>.
    ///
    /// <para><b>Normalize EDİLMEZ</b> (yayın kodunun aksine): barkod yükü opak
    /// bir dizgi, okutucu onu karakteri karakterine üretiyor. Normalize etmek
    /// "AB12" ile "ab12"yi aynı sayardı — bunlar farklı iki varyant olabilir
    /// ve okutma yanlış ürünü açardı. Yalnız baştaki/sondaki boşluk kırpılır:
    /// okutucular klavye taklidi yapıyor ve sonda bir Enter/boşluk bırakabiliyor.</para>
    ///
    /// <para><c>LIMIT 1</c>: benzersizliğin sahibi sunucu. Replikada indeks
    /// UNIQUE değil (gerekçesi göç 031'de), yani teorik bir çift satır
    /// sorguyu patlatmak yerine ilkini döndürsün.</para>
    /// </summary>
    public CatalogVariant? FindVariantByBarcode(string? barcode)
    {
        var needle = (barcode ?? string.Empty).Trim();
        if (needle.Length == 0) return null;

        using var conn = _factory.Open();
        return conn.Query<VariantRow>(
            """
            SELECT Id, ProductId, Axis1Value, Axis2Value, Barcode, IsActive, SortOrder
            FROM CatalogVariant
            WHERE Barcode = @needle
            LIMIT 1
            """,
            new { needle })
            .Select(r => new CatalogVariant(
                r.Id, r.ProductId, r.Axis1Value, r.Axis2Value,
                r.Barcode, r.IsActive == 1, r.SortOrder))
            .FirstOrDefault();
    }

    /// <summary>
    /// Ürünün yayın kodları, panelde verilen sırayla.
    ///
    /// <para>Barkod okutma yolu için: barkod varyanta, varyant ürüne çözülüyor
    /// ama akışın geri kalanı bir YAYIN KODU bekliyor. Sıra önemli — birden
    /// çok kod varsa operatörün panelde ilk sıraya koyduğu kod gösterilir.</para>
    /// </summary>
    public IReadOnlyList<CatalogBroadcastCode> GetBroadcastCodes(string productId)
    {
        using var conn = _factory.Open();
        return conn.Query<BroadcastCodeRow>(
            """
            SELECT ProductId, SellerAxisValue, Code, CodeNormalized, CreatedAt, SortOrder
            FROM CatalogBroadcastCode
            WHERE ProductId = @productId
            ORDER BY SortOrder, Code
            """,
            new { productId })
            .Select(r => new CatalogBroadcastCode(
                r.ProductId, r.SellerAxisValue, r.Code, r.CodeNormalized,
                r.CreatedAt, r.SortOrder))
            .ToList();
    }

    public IReadOnlyList<CatalogCategory> GetCategories()
    {
        using var conn = _factory.Open();
        return conn.Query<CategoryRow>(
            """
            SELECT Id, ParentCategoryId, Name, Path, SortOrder, IsActive
            FROM CatalogCategory ORDER BY Path
            """)
            .Select(r => new CatalogCategory(
                r.Id, r.ParentCategoryId, r.Name, r.Path, r.SortOrder, r.IsActive == 1))
            .ToList();
    }

    /// <summary>Önbellekte tutulması gereken canlı fotoğraf anahtarları.</summary>
    public IReadOnlyList<string> CoverPhotoKeys()
    {
        using var conn = _factory.Open();
        return conn.Query<string>(
            "SELECT DISTINCT CoverPhotoKey FROM CatalogProduct "
          + "WHERE CoverPhotoKey IS NOT NULL AND CoverPhotoKey <> '' ORDER BY CoverPhotoKey")
            .ToList();
    }

    private const string ProductColumns =
        "Id, CategoryId, Code, CodeNormalized, Name, DefaultPrice, ShelfLocation, "
      + "Axis1Name, Axis1Role, Axis2Name, Axis2Role, CoverPhotoKey, UpdatedAt";

    private static CatalogProduct Map(ProductRow r) => new(
        r.Id, r.CategoryId, r.Code, r.CodeNormalized, r.Name, r.DefaultPrice,
        r.ShelfLocation, r.Axis1Name, r.Axis1Role, r.Axis2Name, r.Axis2Role,
        r.CoverPhotoKey, r.UpdatedAt);

    // SQLite INTEGER -> Int64 döner; Dapper bunu record kurucusunun int
    // parametresine bağlayamaz. Daraltma bu ara sınıflarda yapılıyor
    // (bkz. ShipmentRepository.Row — repodaki yerleşik kural).
    private sealed class ProductRow
    {
        public string Id { get; init; } = "";
        public string? CategoryId { get; init; }
        public string Code { get; init; } = "";
        public string CodeNormalized { get; init; } = "";
        public string Name { get; init; } = "";
        public decimal DefaultPrice { get; init; }
        public string? ShelfLocation { get; init; }
        public string? Axis1Name { get; init; }
        public int? Axis1Role { get; init; }
        public string? Axis2Name { get; init; }
        public int? Axis2Role { get; init; }
        public string? CoverPhotoKey { get; init; }
        public long UpdatedAt { get; init; }
    }

    private sealed class VariantRow
    {
        public string Id { get; init; } = "";
        public string ProductId { get; init; } = "";
        public string? Axis1Value { get; init; }
        public string? Axis2Value { get; init; }
        public string? Barcode { get; init; }
        public int IsActive { get; init; }
        public int SortOrder { get; init; }
    }

    private sealed class BroadcastCodeRow
    {
        public string ProductId { get; init; } = "";
        public string? SellerAxisValue { get; init; }
        public string Code { get; init; } = "";
        public string CodeNormalized { get; init; } = "";
        public long CreatedAt { get; init; }
        public int SortOrder { get; init; }
    }

    private sealed class CategoryRow
    {
        public string Id { get; init; } = "";
        public string? ParentCategoryId { get; init; }
        public string Name { get; init; } = "";
        public string Path { get; init; } = "";
        public int SortOrder { get; init; }
        public int IsActive { get; init; }
    }
}
