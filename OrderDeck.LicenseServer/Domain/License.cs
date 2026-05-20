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

    // Müşteri (shopper) app entegrasyonu — Faz 0a, 2026-05-20.
    // ShopperCode: yayıncının müşterilerine paylaştığı davet kodu (lowercase
    // case-insensitive unique). Düzenleme 7 gün cooldown'a tabi (Faz 0b'de).
    // PaymentIban / PaymentAccountHolder: WPF Settings sync sonucu — dekont
    // fraud kontrolünde RecipientIban karşılaştırması için.
    // ShopperAppEnabled: feature flag — public rollout başlamadan kapalı kalır.
    public string? ShopperCode { get; set; }
    public DateTimeOffset? ShopperCodeUpdatedAt { get; set; }
    public string? PaymentIban { get; set; }
    public string? PaymentAccountHolder { get; set; }
    public bool ShopperAppEnabled { get; set; }
}
