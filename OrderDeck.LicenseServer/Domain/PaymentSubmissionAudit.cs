namespace OrderDeck.LicenseServer.Domain;

/// <summary>
/// Shopper-tarafından upload edilen dekontun fraud denetim izi. 90 gün
/// retention (yayıncı approval kararından sonra). FraudFlags + ParserConfidence
/// karar gerekçesini tarihselleştirir.
/// </summary>
public sealed class PaymentSubmissionAudit
{
    public Guid Id { get; set; }
    public Guid PaymentId { get; set; }
    public Guid ShopperId { get; set; }
    public Guid LicenseId { get; set; }   // Faz 0b-4: rate limit by license needs this
    public string IpAddress { get; set; } = "";
    public string UserAgent { get; set; } = "";
    public string FraudFlags { get; set; } = "";
    public string ParserConfidence { get; set; } = "";
    public string? ParserRawText { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
