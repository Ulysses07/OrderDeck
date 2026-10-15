namespace OrderDeck.LicenseServer.Domain;

/// <summary>
/// Ödeme isteği kaydının kapsadığı bir sipariş — spec 2026-10-13 §3.1 (S3), §6.4. "Bu istek bu etiketi içeriyor mu"
/// sorusu (<c>Covers</c>) uzak kayıtta iki bilgisayarın saatini karşılaştırmak yerine bu üyelikle cevaplanır.
///
/// <para>Orders'a FK yoktur: geri doldurulan geçmiş kaydın siparişi sunucuda olmayabilir. Licenses'a da yoktur (kayıt
/// üzerinden cascade; SQL Server çoklu cascade yolunu reddeder).</para>
/// </summary>
public sealed class PaymentRequestRecordOrder
{
    public Guid RecordId { get; set; }
    public PaymentRequestRecord Record { get; set; } = null!;

    /// <summary>Siparişin (etiketin) Id'si.</summary>
    public Guid OrderId { get; set; }

    public Guid LicenseId { get; set; }
}
