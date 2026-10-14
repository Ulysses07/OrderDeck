namespace OrderDeck.LicenseServer.Domain;

/// <summary>
/// Protokolsüz (≤0.9.10) satış isteğinin lisans başına izi — yayın ve etiket
/// senkronu spec'i 2026-10-13 §4.0. İki kipte de yazılır, davranışı
/// değiştirmez; mod kapısının "7 gün eski sürüm izi yok" koşulu (§4.10-3)
/// bununla ölçülür.
///
/// <para><b>Neden <see cref="License"/> satırında değil.</b> Licenses'ın
/// <c>RowVersion</c>'ı eşzamanlılık jetonudur ve satıra yapılan her UPDATE onu
/// ilerletir — izlenmeyen <c>ExecuteUpdate</c> dahil. Eski sürümün push'u
/// saatte bir iz yazınca, License'ı okuyup kaydeden ve çakışmayı yakalamayan
/// bugünkü yollar (WPF'in payment-account'u, panelin shopper-code'u) seyrek
/// de olsa 500 alırdı. İz ayrı, jetonsuz bir satırda olunca hiçbir License
/// yazımıyla yarışmaz.</para>
/// </summary>
public sealed class SalesLegacyTrace
{
    public Guid LicenseId { get; set; }
    public License License { get; set; } = null!;

    /// <summary>Protokolsüz satış isteğinin son (saat hassasiyetinde) anı.</summary>
    public DateTimeOffset LastLegacySalesPushAt { get; set; }
}
