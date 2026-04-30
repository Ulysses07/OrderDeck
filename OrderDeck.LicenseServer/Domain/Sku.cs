namespace LiveDeck.LicenseServer.Domain;

public sealed class Sku
{
    public string Code { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public int DefaultDurationDays { get; set; }
    public int DefaultActivationSlots { get; set; }
    public string? Description { get; set; }
}
