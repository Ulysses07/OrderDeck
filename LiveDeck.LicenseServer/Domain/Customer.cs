namespace LiveDeck.LicenseServer.Domain;

public sealed class Customer
{
    public Guid Id { get; set; }
    public string Email { get; set; } = "";
    public string Name { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public DateTimeOffset? EmailConfirmedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string? Notes { get; set; }

    public ICollection<License> Licenses { get; } = new List<License>();
}
