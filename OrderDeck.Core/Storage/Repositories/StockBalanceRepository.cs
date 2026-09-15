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
    /// R10-D03: imleci ve replikayı hedef lisansa bağlar. İmleç satırındaki
    /// sahip (<c>LicenseKey</c>) verilen anahtardan farklıysa — göç 040
    /// öncesinden kalan <c>NULL</c> dahil — replika ile imleç <b>tek
    /// transaction'da</b> sıfırlanır ve sahip yazılır: kontrollü tam yeniden
    /// kurulum. Yalnız imleci sıfırlamak yetmezdi; eski hedefin bakiye
    /// satırları yeni hedefinmiş gibi görünmeye devam ederdi.
    ///
    /// <para><c>NULL</c> sahip bilerek "bilinmiyor" sayılıyor: 040 öncesi
    /// imlecin hangi lisansla ilerletildiği kayıtlı değil — D03'ün tarif
    /// ettiği belirsizliğin ta kendisi. Yeniden kurulum ucuz (replika
    /// sunucudan türetilebilir), yanlış sahibe güvenmek sessiz veri kaybı.</para>
    /// </summary>
    /// <returns>
    /// Turun başlayacağı imleç + bayat satır atılıp atılmadığı.
    /// <c>DiscardedStale</c> true ise çağıran, bayat satırlar ekrandan düşsün
    /// diye bakiye-değişti bildirimini yazılan satır olmasa da tetiklemeli.
    /// Boş replikayı devralmak (ör. taze kurulumda göç 040 sonrası ilk tur)
    /// görünür hiçbir şeyi değiştirmediği için false döner — bildirim gereksiz.
    /// </returns>
    public (StockCursor Cursor, bool DiscardedStale) EnsureTarget(string licenseKey)
    {
        using var conn = _factory.Open();
        using var tx = conn.BeginTransaction();

        var owner = conn.ExecuteScalar<string?>(
            "SELECT LicenseKey FROM CatalogStockCursor WHERE Id = 1", transaction: tx);

        if (owner == licenseKey)
        {
            var current = ReadCursor(conn, tx);
            tx.Commit();
            return (current, false);
        }

        var discarded = conn.Execute("DELETE FROM CatalogStockBalance", transaction: tx);
        conn.Execute(
            "UPDATE CatalogStockCursor SET CursorCreatedAt = @createdAt, CursorId = @id, "
          + "LicenseKey = @licenseKey WHERE Id = 1",
            new
            {
                // Göç 029'un tohumuyla birebir aynı "her şeyi çek" imleci.
                createdAt = DateTimeOffset.MinValue.ToString("O"),
                id = Guid.Empty.ToString("N"),
                licenseKey
            }, tx);

        tx.Commit();
        return (new StockCursor(DateTimeOffset.MinValue, Guid.Empty), discarded > 0);
    }

    private static StockCursor ReadCursor(
        System.Data.IDbConnection conn, System.Data.IDbTransaction tx)
    {
        var row = conn.QuerySingle<CursorRow>(
            "SELECT CursorCreatedAt, CursorId FROM CatalogStockCursor WHERE Id = 1",
            transaction: tx);
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
    ///
    /// <para>R10-D03: <paramref name="expectedOwner"/> verilirse imleç
    /// güncellemesi <c>AND LicenseKey = @owner</c> koşulu taşır — sahip bu
    /// arada değiştiyse (ör. sayfa uçuştayken lisans anahtarı değişti ve yeni
    /// tur <c>EnsureTarget</c> ile replikayı sıfırladı) hiçbir satır eşleşmez,
    /// transaction geri alınır ve <c>false</c> döner: bayat sayfa yeni hedefin
    /// replikasına yazılamaz. Karar D02'deki desenle aynı — üstünlük yazımdan
    /// önce okumakla değil, UYGULANAN YAZIDA sağlanır. <c>null</c> = sahipsiz
    /// yazım (testler / eski çağıranlar), davranış değişmez.</para>
    /// </summary>
    public bool ApplyPage(
        IReadOnlyList<CatalogStockBalance> balances, StockCursor cursor,
        string? expectedOwner = null)
    {
        using var conn = _factory.Open();
        using var tx = conn.BeginTransaction();

        var cursorSql =
            "UPDATE CatalogStockCursor SET CursorCreatedAt = @createdAt, CursorId = @id "
          + "WHERE Id = 1"
          + (expectedOwner is null ? "" : " AND LicenseKey = @owner");
        var affected = conn.Execute(
            cursorSql,
            new
            {
                createdAt = cursor.CreatedAt.ToString("O"),
                id = cursor.Id.ToString("N"),
                owner = expectedOwner
            }, tx);

        if (affected == 0)
        {
            tx.Rollback();
            return false;
        }

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

        tx.Commit();
        return true;
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
