namespace OrderDeck.Licensing.Api.Models;

/// <summary>WPF → LicenseServer push: tek shipment item (Kümülatif kargo PR-D).</summary>
public sealed record SyncShipmentItem(
    Guid Id,
    string CustomerId,
    string Status,         // "pending" | "held" | "recipientpays" | "shipped"
    decimal CumulativeAmount,
    DateTimeOffset CreatedAt,
    DateTimeOffset? HeldAt,
    DateTimeOffset? ShippedAt);

public sealed record SyncShipmentsRequest(IReadOnlyList<SyncShipmentItem> Shipments);

/// <summary>Server-authoritative shipment echo (sync response + since pull aynı şekil).</summary>
public sealed record SyncedShipmentDto(
    Guid Id,
    string CustomerId,
    string Status,
    decimal CumulativeAmount,
    DateTimeOffset CreatedAt,
    DateTimeOffset? HeldAt,
    DateTimeOffset? ShippedAt,
    DateTimeOffset UpdatedAt);
