using System.ComponentModel.DataAnnotations;

namespace OrderDeck.LicenseServer.Domain;

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

    /// <summary>Bumped by ActivationManager on every activation lifecycle change so the
    /// adjacent RowVersion fires a concurrency conflict if two slot-claims race.</summary>
    public DateTimeOffset? LastActivationAt { get; set; }

    /// <summary>SQL Server rowversion — EF treats it as a concurrency token; UPDATE
    /// without matching version throws DbUpdateConcurrencyException.</summary>
    [Timestamp]
    public byte[] RowVersion { get; set; } = Array.Empty<byte>();

    public ICollection<Activation> Activations { get; } = new List<Activation>();
}
