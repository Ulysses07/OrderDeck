namespace OrderDeck.Licensing.Api.Models;

/// <summary>
/// WPF → LicenseServer WhatsApp template push request (2026-05-15).
/// </summary>
public sealed record WhatsAppTemplatesRequest(
    string PaymentTemplate,
    string ShippingWonTemplate);

/// <summary>Server response — echo + server timestamp.</summary>
public sealed record WhatsAppTemplatesDto(
    string PaymentTemplate,
    string ShippingWonTemplate,
    DateTimeOffset UpdatedAt);
