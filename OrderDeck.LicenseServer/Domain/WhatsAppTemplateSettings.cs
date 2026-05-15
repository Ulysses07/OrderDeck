namespace OrderDeck.LicenseServer.Domain;

/// <summary>
/// Yayıncının WhatsApp mesaj şablonları — WPF AppSettings.PaymentSettings'in
/// server-side replikası. Mobile WhatsAppTemplatesScreen bu kayıttan
/// preview yapar; yoksa varsayılan template gösterir.
///
/// Bir License başına en fazla bir satır (License.Id unique). WPF
/// PaymentSettings değişince outbox üzerinden push edilir. Server
/// authoritative değil, sadece replika — WPF tek yazar.
/// </summary>
public sealed class WhatsAppTemplateSettings
{
    public Guid Id { get; set; }
    public Guid LicenseId { get; set; }
    public License License { get; set; } = null!;

    /// <summary>WPF AppSettings.PaymentSettings.WhatsAppMessageTemplate replikası.</summary>
    public string PaymentTemplate { get; set; } = "";

    /// <summary>WPF AppSettings.PaymentSettings.ShippingWonTemplate replikası.</summary>
    public string ShippingWonTemplate { get; set; } = "";

    public DateTimeOffset UpdatedAt { get; set; }
}
