namespace OrderDeck.LicenseServer.Services.Catalog;

/// <summary>
/// Varyant kodunun <b>tek</b> kurucusu: ürün kodu + eksen kod parçaları, "-" ile
/// birleşik (A12-SIYA-M). Eksensiz üründe kod, ürün kodunun kendisidir.
///
/// Neden tek yer: <c>VariantCode</c> saklanan ama <b>türetilmiş</b> bir değer.
/// Girdilerinden biri (ürün kodu ya da eksen kod parçası) değişince yeniden
/// hesaplanmazsa bayatlar; bayat kod da varyant çakışma kontrolünü kör eder.
/// Bu yüzden kodu kuran her yazma yolu buradan geçmeli, kimse elle
/// birleştirmemeli.
///
/// DİKKAT — bu kod <b>barkot değildir</b>.
/// <see cref="OrderDeck.LicenseServer.Domain.ProductVariant.Barcode"/> ayrı ve
/// değişmez bir fiziksel kimlik (Faz 1c): ürün kodu değişince VariantCode
/// yenilenir, ama rafta duran ürüne yapıştırılmış etiket geçerli kalmalı.
/// Faz 1c'de barkot yükü olarak VariantCode basılmamalı.
/// </summary>
public static class VariantCodeBuilder
{
    public const char Separator = '-';

    /// <summary>
    /// Ürün kodu ile eksen kod parçalarından varyant kodunu kurar. Boş/null
    /// parça "parça yok" demektir; sarkan tire ("A1-") asla üretilmez.
    /// </summary>
    public static string Build(string productCode, string? axis1Code, string? axis2Code)
    {
        if (string.IsNullOrEmpty(axis1Code))
            return productCode;

        return string.IsNullOrEmpty(axis2Code)
            ? $"{productCode}{Separator}{axis1Code}"
            : $"{productCode}{Separator}{axis1Code}{Separator}{axis2Code}";
    }
}
