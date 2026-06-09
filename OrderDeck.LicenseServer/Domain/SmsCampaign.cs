namespace OrderDeck.LicenseServer.Domain;

/// <summary>
/// Yayıncının toplu SMS gönderimi. Oluşturulduğunda alıcılar
/// (<see cref="SmsCampaignRecipient"/>) snapshot'lanır ve kredi rezerve edilir;
/// gönderim Hangfire job'ında arka planda yapılır.
/// </summary>
public sealed class SmsCampaign
{
    public Guid Id { get; set; }
    public Guid LicenseId { get; set; }
    public License License { get; set; } = null!;

    public string MessageBody { get; set; } = "";

    /// <summary>Mesajın segment sayısı (GSM-7 / UCS-2'ye göre hesaplanır).</summary>
    public int SegmentsPerMessage { get; set; }

    public int RecipientCount { get; set; }

    /// <summary>Oluşturulurken rezerve edilen kredi (RecipientCount × SegmentsPerMessage).</summary>
    public int ReservedCredits { get; set; }

    /// <summary>"pending" | "sending" | "completed" | "failed".</summary>
    public string Status { get; set; } = "pending";

    public Guid CreatedByCustomerId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}
