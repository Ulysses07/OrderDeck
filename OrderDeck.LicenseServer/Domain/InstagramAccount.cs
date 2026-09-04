namespace OrderDeck.LicenseServer.Domain;

/// <summary>
/// Yayıncının (lisansın) "!kayıt → DM" botu için bağlanan Instagram professional
/// hesabı. FB OAuth exchange'i sırasında, müşterinin IntakeFormConfig'inde
/// <c>InstagramDmBotEnabled</c> açıksa oluşur/güncellenir (opt-in — exchange ucu
/// varsayılan davranışında token SAKLAMAZ, bkz. FacebookOAuthController).
///
/// <para><b>Webhook yönlendirme:</b> Meta live_comments olayı entry.id'de IG
/// professional hesap kimliğini taşır; <see cref="IgUserId"/> bu yüzden global
/// unique'tir (WhatsAppAccount.PhoneNumberId deseni).</para>
/// </summary>
public sealed class InstagramAccount
{
    public Guid Id { get; set; }
    public Guid LicenseId { get; set; }
    public License License { get; set; } = null!;

    /// <summary>IG hesabının bağlı olduğu Facebook Sayfası — private reply
    /// <c>/{PageId}/messages</c> ucuna gider, webhook aboneliği de bu sayfaya yapılır.</summary>
    public string PageId { get; set; } = "";

    /// <summary>instagram_business_account.id — webhook route anahtarı, global unique.</summary>
    public string IgUserId { get; set; } = "";

    /// <summary>Yalnız UI/log için.</summary>
    public string IgUsername { get; set; } = "";

    /// <summary>Page access token — <c>IDataProtector</c> ile şifreli, asla düz metin dönmez.</summary>
    public string PageTokenProtected { get; set; } = "";

    /// <summary>"active" | "revoked" (token geçersizleşti — DM gönderimi hata verdi).</summary>
    public string Status { get; set; } = "active";

    /// <summary>Son gönderim/abonelik hatası — teşhis için.</summary>
    public string? LastError { get; set; }

    public DateTimeOffset ConnectedAt { get; set; }
}
