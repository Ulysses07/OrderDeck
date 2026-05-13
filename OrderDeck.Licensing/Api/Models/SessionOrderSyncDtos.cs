namespace OrderDeck.Licensing.Api.Models;

/// <summary>WPF → LicenseServer push: tek StreamSession item (PR siparis-sync 2026-05-13).</summary>
public sealed record SyncSessionItem(
    System.Guid Id,
    string? Title,
    System.DateTimeOffset StartedAt,
    System.DateTimeOffset? EndedAt,
    string Platforms,         // comma-separated: "instagram,youtube"
    string? Notes);

public sealed record SyncSessionsRequest(System.Collections.Generic.IReadOnlyList<SyncSessionItem> Sessions);

public sealed record SyncedSessionDto(
    System.Guid Id, string? Title,
    System.DateTimeOffset StartedAt, System.DateTimeOffset? EndedAt,
    string Platforms, string? Notes,
    System.DateTimeOffset UpdatedAt);

/// <summary>WPF → LicenseServer push: tek Order (Label) item.</summary>
public sealed record SyncOrderItem(
    System.Guid Id,
    System.Guid? SessionId,
    string CustomerId,
    string Platform,
    string Username,
    string? DisplayName,
    string MessageText,
    string? Code,
    decimal Price,
    System.DateTimeOffset AddedAt,
    System.DateTimeOffset? PrintedAt,
    System.DateTimeOffset? CancelledAt,
    string? CancelReason,
    bool IsShippingFee,
    bool IsBackupPromoted,
    bool IsTentativeBackup);

public sealed record SyncOrdersRequest(System.Collections.Generic.IReadOnlyList<SyncOrderItem> Orders);

public sealed record SyncedOrderDto(
    System.Guid Id, System.Guid? SessionId, string CustomerId,
    string Platform, string Username, string? DisplayName,
    string MessageText, string? Code, decimal Price,
    System.DateTimeOffset AddedAt, System.DateTimeOffset? PrintedAt,
    System.DateTimeOffset? CancelledAt, string? CancelReason,
    bool IsShippingFee, bool IsBackupPromoted, bool IsTentativeBackup,
    System.DateTimeOffset UpdatedAt);
