using Dapper;
using OrderDeck.Core.Catalog;

namespace OrderDeck.Core.Storage.Repositories;

/// <summary>
/// Ürün kartının yerel deposu (arayüz Faz 1, spec §9.1). Yalnız SQLite —
/// sunucuya hiç yazmıyor.
/// </summary>
public sealed class ProductRepository
{
    private readonly IDbConnectionFactory _factory;

    public ProductRepository(IDbConnectionFactory factory) => _factory = factory;

    public Product? Get(string code)
    {
        using var conn = _factory.Open();
        return conn.QuerySingleOrDefault<Product>(
            "SELECT Code, Name, PhotoPath, UpdatedAt FROM Product WHERE Code = @code",
            new { code });
    }

    public IReadOnlyList<ProductSize> GetSizes(string code)
    {
        using var conn = _factory.Open();
        return conn.Query<SizeRow>(
            """
            SELECT Code, Size, Quantity, SortOrder
            FROM ProductSize
            WHERE Code = @code
            ORDER BY SortOrder
            """,
            new { code })
            .Select(r => new ProductSize(r.Code, r.Size, r.Quantity, r.SortOrder))
            .ToList();
    }

    /// <summary>
    /// Ürünü ve beden setini birlikte yazar. Beden seti TAMAMEN değiştirilir:
    /// operatör bir bedeni kaldırdığında satır kalıntı bırakmasın diye önce
    /// silinir. Tek transaction — yarım yazılmış bir kart bırakmaz.
    /// </summary>
    public void Save(Product product, IReadOnlyList<ProductSize> sizes)
    {
        using var conn = _factory.Open();
        using var tx = conn.BeginTransaction();

        conn.Execute(
            """
            INSERT INTO Product (Code, Name, PhotoPath, UpdatedAt)
            VALUES (@Code, @Name, @PhotoPath, @UpdatedAt)
            ON CONFLICT(Code) DO UPDATE SET
                Name      = excluded.Name,
                PhotoPath = excluded.PhotoPath,
                UpdatedAt = excluded.UpdatedAt
            """,
            product, tx);

        conn.Execute("DELETE FROM ProductSize WHERE Code = @Code",
                     new { product.Code }, tx);

        if (sizes.Count > 0)
        {
            conn.Execute(
                """
                INSERT INTO ProductSize (Code, Size, Quantity, SortOrder)
                VALUES (@Code, @Size, @Quantity, @SortOrder)
                """,
                sizes, tx);
        }

        tx.Commit();
    }

    /// <summary>
    /// SQLite INTEGER'ı Int64 döndürüyor; Dapper bunu record'un int ctor
    /// parametresine bağlayamıyor (ctor eşleşmesi tam tip istiyor). Depodaki
    /// diğer repository'lerdeki gibi ara bir Row sınıfıyla daraltılıyor
    /// (bkz. ShipmentRepository.Row).
    /// </summary>
    private sealed class SizeRow
    {
        public string Code { get; init; } = "";
        public string Size { get; init; } = "";
        public int Quantity { get; init; }
        public int SortOrder { get; init; }
    }
}
