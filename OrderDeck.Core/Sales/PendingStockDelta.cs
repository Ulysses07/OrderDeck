namespace OrderDeck.Core.Sales;

/// <summary>
/// Yerelde yazılmış ama sunucuya <b>henüz gitmemiş</b> etiketlerin bir
/// (ürün, varyant) anahtarındaki adedi. Sunucu bakiyesinden düşülür:
/// <c>gösterilen = sunucu bakiyesi − bekleyen</c>.
/// </summary>
/// <param name="PendingCount">
/// Her etiket 1 adet — miktar alanı yok, bu yüzden sayım <c>COUNT(*)</c>.
/// </param>
public sealed record PendingStockDelta(
    string ProductId,
    string? ProductVariantId,
    int PendingCount);
