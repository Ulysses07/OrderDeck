namespace OrderDeck.LicenseServer.Domain;

/// <summary>
/// Ürünün tek bir eksen kombinasyonu. Eksensiz üründe de TEK bir satır oluşur
/// (iki değer de null) — böylece stok her zaman aynı yapıdan okunur, özel
/// durum kodu yazılmaz.
///
/// <para>Varyantın <b>kodu yoktur</b>: kimliği <see cref="Id"/>, kullanıcıya
/// görünen adı eksen değerleridir ("Siyah · M"). Yayında söylenen kod ürün +
/// satıcı ekseni seviyesinde ve ayrı bir kaynakta
/// (<c>ProductBroadcastCode</c>) yaşıyor.</para>
/// </summary>
public sealed class ProductVariant
{
    public Guid Id { get; set; }

    /// <summary>Product üzerinden türetilebilir; kiracı filtresini tek kural
    /// tutmak için denormalize edildi (spec: varyant da LicenseId alır).</summary>
    public Guid LicenseId { get; set; }

    public Guid ProductId { get; set; }
    public Product Product { get; set; } = null!;

    /// <summary>Görünen değer — serbest, Türkçe karakter içerebilir ("Yeşil").</summary>
    public string? Axis1Value { get; set; }

    public string? Axis2Value { get; set; }

    /// <summary>
    /// <c>SearchNormalizer.Normalize(Axis1Value)</c>. Türetilmiş — elle YAZILMAZ,
    /// <c>LicenseDbContext.SyncDerivedColumns</c> dolduruyor.
    ///
    /// <para>Varyantın kimliği bu iki kolon. Ham değerlerin üstüne benzersizlik
    /// kurmak iki yerde sessizce bozulurdu: harf duyarlılığı veritabanının
    /// collation'ına kalırdı (SQL Server duyarsız, PostgreSQL duyarlı) ve
    /// NULL'lar indekste birbirinden farklı sayılırdı. Eksen yoksa değer
    /// <b>boş dize</b>, null değil.</para>
    /// </summary>
    public string Axis1ValueNorm { get; set; } = string.Empty;

    /// <summary><see cref="Axis1ValueNorm"/>'un ikinci eksen karşılığı.</summary>
    public string Axis2ValueNorm { get; set; } = string.Empty;

    /// <summary>
    /// Fiziksel kimlik. Yük <b>varyant yaratılırken</b> atanır ve bir daha
    /// türetilmez: lisans başına 10 haneli bir sayaçtan gelen opak numara
    /// (bkz. <see cref="BarcodeCounter"/>), ya da paneldeki elle girilen değer.
    ///
    /// <para><b>Neden türetilmiyor:</b> eksen değerinden ya da Id'den
    /// hesaplansaydı, bir yazım düzeltmesi ("Siyah" → "Siyah ") basılı
    /// etiketleri geçersiz kılardı. Atanmış numara, yazıldığı andan sonra
    /// hiçbir düzenlemeden etkilenmez.</para>
    ///
    /// <para><b>Neden boş olamaz:</b> kural "kullanıcı barkod yazsın" değil,
    /// "barkodsuz varyant var olmasın" — sunucu üç yazma yolunda da boşluğu
    /// kendisi dolduruyor. Benzersizlik <c>(LicenseId, Barcode)</c>
    /// indeksinde.</para>
    ///
    /// <para>Numara türetilmediği için varyant yaratılır yaratılmaz belli;
    /// katalog senkronuyla WPF replikasına iniyor ve <b>çevrimdışı okutma</b>
    /// çalışıyor.</para>
    /// </summary>
    public string Barcode { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
