using OrderDeck.Core.Storage.Repositories;

namespace OrderDeck.Core.Catalog;

/// <summary>
/// Tek ürünün gösterilecek bakiyeleri: <c>sunucu bakiyesi − yerel bekleyen</c>.
///
/// <para>Neden ayrı bir tür ve düz sözlük değil: anahtarın null olabilmesi
/// (varyantsız satış) <c>Dictionary&lt;string?, int&gt;</c> ile ifade
/// edilemiyor — .NET sözlüğü null anahtar kabul etmez.</para>
/// </summary>
public sealed class ProductStockSnapshot
{
    private readonly IReadOnlyDictionary<string, int> _byVariant;

    internal ProductStockSnapshot(IReadOnlyDictionary<string, int> byVariant, int productLevel)
    {
        _byVariant = byVariant;
        ProductLevel = productLevel;
    }

    /// <summary>Varyanta bağlanmamış (ürün düzeyindeki) bakiye.</summary>
    public int ProductLevel { get; }

    /// <summary>
    /// Varyantın bakiyesi. Bilinmeyen varyant <b>0</b> döner, istisna değil:
    /// sunucu sıfır bakiyeli anahtar için hiç satır göndermiyor, "yok" ile
    /// "sıfır" bu modelde aynı şey.
    /// </summary>
    public int For(string? variantId)
        => variantId is null
            ? ProductLevel
            : _byVariant.TryGetValue(variantId, out var q) ? q : 0;
}

/// <summary>
/// Ürün kartının bakiye kaynağı. Sunucudan çekilmiş replikayı yerelde henüz
/// senkronlanmamış etiketlerle mahsuplaştırır.
///
/// <para>Sorgu <b>her çağrıda</b> tazeleniyor — önbellek yok. Ürün kartı
/// yalnız kod çözümlemesinde ve sipariş yazıldığında soruyor, yani saniyede
/// birkaç kez; iki indeksli SQLite sorgusu bu hız için fazlasıyla yeterli.
/// Önbellek eklemek "ne zaman geçersiz kılınır" sorusunu getirirdi.</para>
/// </summary>
public sealed class StockBalanceProvider
{
    private readonly StockBalanceRepository _balances;
    private readonly LabelRepository _labels;

    public StockBalanceProvider(StockBalanceRepository balances, LabelRepository labels)
    {
        _balances = balances;
        _labels = labels;
    }

    /// <summary>
    /// Senkron turu replikaya yazdığında tetiklenir; arayüz bunu dinleyip
    /// tazeleniyor. Olayı <b>tetikleyen</b> taraf (senkron servisi) UI iş
    /// parçacığında değil — abonelerin dispatcher'a geçmesi kendi sorumluluğu.
    /// </summary>
    public event EventHandler? BalancesChanged;

    public void RaiseBalancesChanged() => BalancesChanged?.Invoke(this, EventArgs.Empty);

    public ProductStockSnapshot ForProduct(string productId)
    {
        var server = _balances.GetForProduct(productId);
        var pending = _labels.GetPendingStockDeltas(productId);

        var byVariant = new Dictionary<string, int>(StringComparer.Ordinal);
        var productLevel = 0;

        foreach (var b in server)
        {
            if (b.ProductVariantId is null) productLevel += b.Quantity;
            else byVariant[b.ProductVariantId] = b.Quantity;
        }

        foreach (var p in pending)
        {
            if (p.ProductVariantId is null) productLevel -= p.PendingCount;
            else byVariant[p.ProductVariantId] =
                (byVariant.TryGetValue(p.ProductVariantId, out var q) ? q : 0) - p.PendingCount;
        }

        return new ProductStockSnapshot(byVariant, productLevel);
    }
}
