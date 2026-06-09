namespace OrderDeck.LicenseServer.Domain;

/// <summary>
/// Bir <see cref="SmsCampaign"/> içindeki tek alıcı + gönderim sonucu (audit).
/// Kampanya oluşturulurken o anki izinli müşterilerden snapshot'lanır.
/// </summary>
public sealed class SmsCampaignRecipient
{
    public Guid Id { get; set; }
    public Guid CampaignId { get; set; }
    public SmsCampaign Campaign { get; set; } = null!;

    /// <summary>Kaynak WpfCustomerProjection.Id (audit). Manuel numarada null.</summary>
    public Guid? WpfCustomerId { get; set; }

    public string Phone { get; set; } = "";

    /// <summary>"sent" | "failed" | "skipped".</summary>
    public string Status { get; set; } = "";

    /// <summary>Hata mesajı (failed durumunda).</summary>
    public string? Error { get; set; }

    public DateTimeOffset? SentAt { get; set; }
}
