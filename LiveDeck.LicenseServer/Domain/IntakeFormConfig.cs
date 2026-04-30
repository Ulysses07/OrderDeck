namespace LiveDeck.LicenseServer.Domain;

/// <summary>
/// 1:1 Customer mapping. Yayıncı'nın kişisel form linki konfigürasyonu.
/// Slug unique, lowercase. WhatsAppPhone E.164 format (leading +).
/// </summary>
public sealed class IntakeFormConfig
{
    public Guid Id { get; set; }
    public Guid CustomerId { get; set; }
    public Customer Customer { get; set; } = null!;
    public string Slug { get; set; } = "";
    public string WhatsAppPhone { get; set; } = "";
    public string? CustomTitle { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
