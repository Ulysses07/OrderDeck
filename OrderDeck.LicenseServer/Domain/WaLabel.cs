namespace OrderDeck.LicenseServer.Domain;

/// <summary>
/// Yayıncının kendi tanımladığı WhatsApp sohbet etiketi. Meta'da sohbet
/// etiketi API'si YOK — etiket tamamen bizim tarafımızda yaşıyor.
///
/// <para>Sistem hiçbir etiketi önceden tanımlamaz: her yayıncı kendi işine
/// göre ("Dekont geldi", "Kargoya verilecek", "İnsan baksın") yazar.</para>
/// </summary>
public sealed class WaLabel
{
    public Guid Id { get; set; }
    public Guid LicenseId { get; set; }
    public License License { get; set; } = null!;

    /// <summary>Yayıncının yazdığı ad. (LicenseId, Name) benzersiz.</summary>
    public string Name { get; set; } = "";

    /// <summary>Sabit paletten hex renk — <c>WaLabelColors.Palette</c>.</summary>
    public string Color { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; }
}
