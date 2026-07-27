namespace OrderDeck.LicenseServer.Domain;

/// <summary>
/// Tek bir WhatsApp mesajı (gelen veya giden).
///
/// <para><b>Idempotency:</b> Meta webhook'ları <b>en az bir kez</b> teslim eder ve
/// aynı olayı tekrar gönderebilir. <see cref="WamId"/> (Meta'nın <c>wamid...</c>
/// değeri) global unique index'lidir; tekrar gelen olay yazılamaz → sohbette
/// çift mesaj oluşmaz. Giden mesajlarda da wamid Graph yanıtından yazılır, böylece
/// aynı mesajın status webhook'ları aynı satırı günceller.</para>
/// </summary>
public sealed class WaMessage
{
    public Guid Id { get; set; }

    public Guid ConversationId { get; set; }
    public WaConversation Conversation { get; set; } = null!;

    /// <summary>Sorguları sohbete join'lemeden tenant'a kapatmak için denormalize.</summary>
    public Guid LicenseId { get; set; }

    /// <summary>Meta mesaj id'si (<c>wamid.*</c>). Unique — idempotency anahtarı.
    /// Gönderim henüz Graph'a ulaşmadıysa geçici yerel id tutulur.</summary>
    public string WamId { get; set; } = "";

    /// <summary>"in" (müşteriden) | "out" (yayıncıdan).</summary>
    public string Direction { get; set; } = "in";

    /// <summary>Giden mesajın kaynağı: "panel" | "ai" | "automation" | "echo"
    /// ("echo" = yayıncı telefondan/Business App'ten attı, smb_message_echoes ile geldi).</summary>
    public string? Origin { get; set; }

    /// <summary>"text" | "image" | "video" | "audio" | "document" | "sticker" |
    /// "location" | "contacts" | "button" | "interactive" | "template" | "unsupported".</summary>
    public string Type { get; set; } = "text";

    /// <summary>Metin gövdesi (medyada caption).</summary>
    public string? Body { get; set; }

    /// <summary>Medya mesajlarında R2 nesne anahtarı (Meta CDN linki kısa ömürlü,
    /// bu yüzden indirilip R2'ye kopyalanır).</summary>
    public string? MediaR2Key { get; set; }

    public string? MediaMimeType { get; set; }
    public long? MediaSizeBytes { get; set; }

    /// <summary>Template gönderimlerinde kullanılan onaylı template adı.</summary>
    public string? TemplateName { get; set; }

    /// <summary>Giden: "queued" | "sent" | "delivered" | "read" | "failed".
    /// Gelen: "received".</summary>
    public string Status { get; set; } = "received";

    /// <summary>Meta hata kodu (ör. 131047 = pencere kapalı, 131026 = teslim edilemez).</summary>
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }

    /// <summary>Meta'nın verdiği mesaj zamanı (unix → UTC). Gecikmeli webhook'ta
    /// <see cref="CreatedAt"/>'ten eski olabilir — sıralama bununla yapılır.</summary>
    public DateTimeOffset Timestamp { get; set; }

    /// <summary>Satırın bizde oluştuğu an.</summary>
    public DateTimeOffset CreatedAt { get; set; }
}
