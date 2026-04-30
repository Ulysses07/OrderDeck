namespace OrderDeck.LicenseServer.Domain;

/// <summary>
/// Tracks email send attempts for dedup + audit. ContextKey scope is template-specific
/// (e.g. licenseKey for renewal/admin-action emails, tokenId for password-reset).
/// Error null = success; Error populated = failed (manual investigation).
/// </summary>
public sealed class EmailLog
{
    public Guid Id { get; set; }
    public Guid CustomerId { get; set; }
    public string TemplateKey { get; set; } = "";
    public string? ContextKey { get; set; }
    public DateTimeOffset SentAt { get; set; }
    public string? Error { get; set; }
}
