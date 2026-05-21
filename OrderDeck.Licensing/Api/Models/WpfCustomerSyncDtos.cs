namespace OrderDeck.Licensing.Api.Models;

/// <summary>Single customer item for WPF → server bulk sync (Faz 0c-1).</summary>
public sealed record WpfCustomerSyncItem(
    Guid Id,
    string Platform,
    string Username,
    string? FullName,
    string? Phone,
    string? Address,
    DateTimeOffset UpdatedAt);

public sealed record WpfCustomerSyncRequest(IReadOnlyList<WpfCustomerSyncItem> Customers);

public sealed record WpfCustomerSyncResponse(int Synced, int RetroactiveMatches);
