namespace LiveDeck.LicenseServer.Domain;

public sealed class License
{
    public Guid Id { get; set; }
    public string LicenseKey { get; set; } = "";
    public Guid CustomerId { get; set; }
    public Customer Customer { get; set; } = null!;
    public string SkuCode { get; set; } = "";
    public Sku Sku { get; set; } = null!;
    public int ActivationSlots { get; set; }
    public DateTimeOffset IssuedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public string? RevokeReason { get; set; }

    public ICollection<Activation> Activations { get; } = new List<Activation>();
}
