namespace OrderDeck.Core.Catalog;

/// <summary>
/// Yayın ekranındaki ürün kartının yerel kaydı. Kod (A12) birincil anahtar —
/// operatör zaten kodla çalışıyor, ayrı bir kimlik üretmek yapay olurdu.
/// </summary>
/// <param name="Code">Ürün kodu, harf duyarsız (SQLite COLLATE NOCASE).</param>
/// <param name="Name">Kartta ve hero'da görünen ad.</param>
/// <param name="PhotoPath">
/// %LOCALAPPDATA%\OrderDeck\products\ altına göreceli dosya yolu; fotoğraf yoksa
/// null. Mutlak yol saklanmıyor — kullanıcı profili taşınınca kırılmasın.
/// </param>
/// <param name="UpdatedAt">Unix saniye; son düzenleme.</param>
public sealed record Product(
    string Code,
    string Name,
    string? PhotoPath,
    long UpdatedAt);

/// <summary>
/// Bir ürünün tek bedeni ve elde kalan adedi.
/// </summary>
/// <param name="SortOrder">
/// Görüntüleme sırası. Beden alfabetik sıralanamaz (L &lt; M &lt; S &lt; XL
/// yanlış olur), bu yüzden sıra açıkça saklanıyor.
/// </param>
public sealed record ProductSize(
    string Code,
    string Size,
    int Quantity,
    int SortOrder);
