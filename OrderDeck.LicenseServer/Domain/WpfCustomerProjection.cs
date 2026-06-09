namespace OrderDeck.LicenseServer.Domain;

/// <summary>
/// WPF lokal Customer kayıtlarının server-side hafif kopyası. Order/Payment
/// satırlarındaki CustomerId string'i bu tablodaki Id'yi (GUID hex) işaret
/// eder. WPF tarafı periyodik sync ile günceller (POST
/// /api/licenses/{id}/wpf-customers/sync — Faz 0c'de eklenir).
/// </summary>
public sealed class WpfCustomerProjection
{
    public Guid Id { get; set; }
    public Guid LicenseId { get; set; }
    public License License { get; set; } = null!;
    public string Platform { get; set; } = "";
    public string Username { get; set; } = "";
    public string? FullName { get; set; }
    public string? Phone { get; set; }
    public string? Address { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
