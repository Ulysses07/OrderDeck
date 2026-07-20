namespace OrderDeck.LicenseServer.Domain;

/// <summary>
/// Müşterinin doldurduğu form. Polling endpoint bu kayıtları cursor (SubmittedAt) ile döndürür.
/// IpAddress + UserAgent audit için.
/// </summary>
public sealed class IntakeFormSubmission
{
    public Guid Id { get; set; }
    public Guid IntakeFormConfigId { get; set; }
    public IntakeFormConfig Config { get; set; } = null!;

    /// <summary>Legacy tek kullanıcı adı (Faz 4f). Çoklu-platform geçişinden
    /// sonra yeni gönderimlerde ilk dolu platform adına set edilir; eski WPF
    /// sync'i (yalnız Username okuyan) bozulmasın diye backward-compat.</summary>
    public string Username { get; set; } = "";

    // Çoklu-platform kullanıcı adları — her biri opsiyonel, formda en az 1 zorunlu.
    // Aynı kişinin farklı platform kimlikleri; WPF tarafında GroupId ile bağlanır.
    public string? YouTubeUsername { get; set; }
    public string? InstagramUsername { get; set; }
    public string? FacebookUsername { get; set; }
    public string? TikTokUsername { get; set; }

    public string FullName { get; set; } = "";
    public string Address { get; set; } = "";
    public string? Phone { get; set; }   // Phase 4g — E.164 format

    /// <summary>Formda zorunlu. DB'de nullable (eski satırlar null).</summary>
    public string? Email { get; set; }
    /// <summary>Fatura için opsiyonel TCKN (11 hane).</summary>
    public string? Tckn { get; set; }

    // Mesaj izinleri — şimdilik yalnız toplanır, gönderim ileride (Faz 4).
    public bool WhatsAppConsent { get; set; }
    public bool SmsConsent { get; set; }

    public DateTimeOffset SubmittedAt { get; set; }
    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }
}
