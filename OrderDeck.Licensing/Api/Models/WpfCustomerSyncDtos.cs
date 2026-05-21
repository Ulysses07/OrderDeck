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

/// <summary>Single item returned by the server-side /wpf-customers/since pull
/// endpoint (Faz 0c-3). Auto-created on shopper register/join.</summary>
public sealed record WpfCustomerPullItem(
    Guid Id,
    string Platform,
    string Username,
    string? FullName,
    string? Phone,
    string? Address,
    DateTimeOffset UpdatedAt);
