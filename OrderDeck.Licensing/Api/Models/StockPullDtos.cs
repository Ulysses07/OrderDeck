namespace OrderDeck.Licensing.Api.Models;

/// <summary>
/// Bir (ürün, varyant) anahtarının sunucudaki <b>toplam</b> bakiyesi.
/// Fark değil, mutlak değer — istemci bunu üstüne toplamaz, yerine yazar.
/// </summary>
public sealed record StockBalancePullItem(
    Guid ProductId,
    Guid? ProductVariantId,
    int Quantity);

/// <summary>
/// Tek sayfa. <c>CursorCreatedAt</c>/<c>CursorId</c> bir sonraki isteğin
/// <c>since</c>/<c>sinceId</c>'si olur.
///
/// <para>Sunucu boş sayfada imleci <b>geri sarmaz</b>, gönderileni aynen iade
/// eder — yani boş sayfada imleci yazmak zararsızdır.</para>
///
/// <para><c>HasMore</c> "sayfa doldu" demektir; false görünce döngü biter.</para>
/// </summary>
public sealed record StockBalancePullResponse(
    List<StockBalancePullItem> Balances,
    DateTimeOffset CursorCreatedAt,
    Guid CursorId,
    bool HasMore);
