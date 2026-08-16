namespace OrderDeck.Core.Catalog;

/// <summary>
/// Bir (ürün, varyant) anahtarının sunucudaki <b>toplam</b> bakiyesi.
/// <para><c>ProductVariantId</c> null ise bakiye ürünün kendisine ait
/// (varyantsız satış / varyanta bağlanmamış hareket).</para>
/// <para><c>Quantity</c> <b>negatif olabilir</b>: stok bitince satış
/// engellenmiyor, bakiye eksiye düşüyor.</para>
/// </summary>
public sealed record CatalogStockBalance(
    string ProductId,
    string? ProductVariantId,
    int Quantity);

/// <summary>
/// Stok hareket defterindeki çekme imleci. <b>Bileşik</b>: tek sipariş
/// senkronunda doğan hareketlerin hepsi aynı <c>CreatedAt</c>'i taşıdığı için
/// yalnız zaman yetmez, kimlik ikincil anahtar olarak şart.
/// </summary>
public sealed record StockCursor(DateTimeOffset CreatedAt, Guid Id);
