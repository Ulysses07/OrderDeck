using System.Text.Json.Serialization;

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

/// <param name="ReplicaId">v2 zarfı (spec 2026-10-13 §4.0, §7.1): kopya kimliği, <c>Protocol = 2</c> ve görülen kip.
/// Sunucu v2'de dosyanın boş <c>OriginReplicaId</c>'sini çağıranla doldurur ve yanıtı akış öğesi
/// (<see cref="SalesShipmentItem"/>) olarak verir. Boşken telde hiç yazılmaz — zarfsız gövde bugünkü gövdeyle aynı.</param>
public sealed record SyncShipmentsRequest(
    IReadOnlyList<SyncShipmentItem> Shipments,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? ReplicaId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Protocol = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? SeenMode = null);

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
