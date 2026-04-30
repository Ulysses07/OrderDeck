namespace OrderDeck.LicenseServer.Domain;

public sealed class EmailConfirmationToken
{
    public Guid Token { get; set; }
    public Guid CustomerId { get; set; }
    public Customer Customer { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UsedAt { get; set; }
}
