using System.Globalization;
using Dapper;
using OrderDeck.Core.Catalog;

namespace OrderDeck.Core.Storage.Repositories;

/// <summary>
/// Sunucu stok defterinin yerel bakiye replikası. Tek yazarı
/// <c>StockSyncService</c>; kullanıcı arayüzü buraya asla yazmaz.
/// </summary>
public sealed class StockBalanceRepository
{
    private readonly IDbConnectionFactory _factory;

    public StockBalanceRepository(IDbConnectionFactory factory) => _factory = factory;

    /// <summary>
    /// Kaldığımız yer. Satır göç 029 tarafından tohumlandığı için burada
    /// "yoksa" hâli yok — tablo her zaman tek satırlıdır.
    /// </summary>
    public StockCursor GetCursor()
    {
        using var conn = _factory.Open();
        var row = conn.QuerySingle<CursorRow>(
            "SELECT CursorCreatedAt, CursorId FROM CatalogStockCursor WHERE Id = 1");

        return new StockCursor(
            DateTimeOffset.Parse(row.CursorCreatedAt, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind),
            Guid.Parse(row.CursorId));
    }

    /// <summary>
    /// Bir sayfayı yazar ve imleci ilerletir — <b>ikisi tek transaction'da</b>.
    /// Ayrılırlarsa çökme anında ya bakiyesiz ilerlemiş ya da aynı sayfayı
    /// tekrar işleyen bir imleç kalırdı.
    ///
    /// <para>Yazma <b>sil-ve-ekle</b>: sunucu mutlak bakiye gönderiyor, üstüne
    /// toplamak aynı sayfa iki kez işlendiğinde bakiyeyi bozardı.</para>
    ///
    /// <para>Silmede <c>IS</c> kullanılıyor, <c>=</c> değil: SQLite'ta
    /// <c>NULL = NULL</c> sonucu NULL'dur (yani "eşleşmedi"), ürün-seviyesi
    /// satırlar hiç silinmez ve her turda bir kopya daha birikirdi.</para>
    ///
    /// <para>Boş sayfa da imleci yazar. Sunucu boş sayfada imleci geri sarmaz,
    /// aynen iade eder — yani bu bir no-op'tur; ama imlecin tek yazma yolu
    /// olmasını sağlar.</para>
    /// </summary>
    public void ApplyPage(IReadOnlyList<CatalogStockBalance> balances, StockCursor cursor)
    {
        using var conn = _factory.Open();
        using var tx = conn.BeginTransaction();

        foreach (var b in balances)
            conn.Execute(
                "DELETE FROM CatalogStockBalance "
              + "WHERE ProductId = @productId AND ProductVariantId IS @variantId",
                new { productId = b.ProductId, variantId = b.ProductVariantId }, tx);

        if (balances.Count > 0)
            conn.Execute(
                """
                INSERT INTO CatalogStockBalance (ProductId, ProductVariantId, Quantity)
                VALUES (@ProductId, @ProductVariantId, @Quantity)
                """,
                balances.Select(b => new { b.ProductId, b.ProductVariantId, b.Quantity })
                        .ToList(), tx);

        conn.Execute(
            "UPDATE CatalogStockCursor SET CursorCreatedAt = @createdAt, CursorId = @id "
          + "WHERE Id = 1",
            new { createdAt = cursor.CreatedAt.ToString("O"), id = cursor.Id.ToString("N") },
            tx);

        tx.Commit();
    }

    /// <summary>Tek ürünün tüm bakiye satırları (varyantlar + ürün seviyesi).</summary>
    public IReadOnlyList<CatalogStockBalance> GetForProduct(string productId)
    {
        using var conn = _factory.Open();
        return conn.Query<BalanceRow>(
            "SELECT ProductId, ProductVariantId, Quantity FROM CatalogStockBalance "
          + "WHERE ProductId = @productId",
            new { productId })
            .Select(r => new CatalogStockBalance(r.ProductId, r.ProductVariantId, (int)r.Quantity))
            .ToList();
    }

    private sealed class CursorRow
    {
        public string CursorCreatedAt { get; init; } = "";
        public string CursorId { get; init; } = "";
    }

    // SQLite INTEGER -> Int64 döner; Dapper bunu record kurucusunun int
    // parametresine bağlayamaz. Daraltma bu ara sınıfta yapılıyor
    // (bkz. CatalogReplicaRepository.ProductRow — repodaki yerleşik kural).
    private sealed class BalanceRow
    {
        public string ProductId { get; init; } = "";
        public string? ProductVariantId { get; init; }
        public long Quantity { get; init; }
    }
}
