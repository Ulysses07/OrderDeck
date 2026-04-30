namespace LiveDeck.LicenseServer.Domain;

/// <summary>
/// Müşterinin doldurduğu form. Polling endpoint bu kayıtları cursor (SubmittedAt) ile döndürür.
/// IpAddress + UserAgent audit için.
/// </summary>
public sealed class IntakeFormSubmission
{
    public Guid Id { get; set; }
    public Guid IntakeFormConfigId { get; set; }
    public IntakeFormConfig Config { get; set; } = null!;
    public string Username { get; set; } = "";
    public string FullName { get; set; } = "";
    public string Address { get; set; } = "";
    public DateTimeOffset SubmittedAt { get; set; }
    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }
}
