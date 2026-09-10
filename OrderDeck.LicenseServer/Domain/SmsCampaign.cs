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

    /// <summary>
    /// F08 (denetim 2026-09-09): gönderim job'ının sahiplik damgası
    /// (concurrency token). Job kampanyayı üstlenirken CAS ile yazar ve her
    /// alıcı kaydında tazeler (lease kalp atışı). Süreç ölürse damga bayatlar;
    /// <see cref="Services.Sms.SmsCampaignRecoveryJob"/> bayat "sending"
    /// kampanyayı yeniden kuyruğa alır — kaldığı yerden devam eder,
    /// gönderilmişi tekrar göndermez.
    /// </summary>
    public DateTimeOffset? ClaimedAt { get; set; }

    public Guid CreatedByCustomerId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}
