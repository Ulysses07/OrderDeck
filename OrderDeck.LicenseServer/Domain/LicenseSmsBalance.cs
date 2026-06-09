namespace OrderDeck.LicenseServer.Domain;

/// <summary>
/// Lisans (yayıncı) başına SMS kredi bakiyesi. Lisans başına tek satır.
/// <see cref="CreditsRemaining"/> her zaman SUM(<see cref="LicenseSmsTransaction"/>.Amount)
/// ile tutar (ledger invariant'ı) — bu satır cache amaçlı, query'leri hızlandırır.
///
/// Kredi birimi = SMS segmenti (alıcı × mesaj segment sayısı). Krediyi biz
/// satıyoruz (admin elle yükler); toplu gönderimde düşülür, başarısız alıcılar
/// için iade edilir.
/// </summary>
public sealed class LicenseSmsBalance
{
    public Guid Id { get; set; }
    public Guid LicenseId { get; set; }
    public License License { get; set; } = null!;

    /// <summary>Anlık kredi (segment). Negatif olamaz (server validation).</summary>
    public int CreditsRemaining { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
