namespace OrderDeck.LicenseServer.Domain;

/// <summary>
/// Bir yayıncının (lisansın) bağladığı WhatsApp Business hesabı. Embedded Signup
/// sonunda oluşur: WABA id + Phone Number ID + o tenant'a ait kalıcı Access Token.
///
/// <para><b>Çok-tenant yönlendirme:</b> Meta webhook'ları tek uygulama uç noktasına
/// gelir; payload'daki <c>metadata.phone_number_id</c> ile bu tablodan tenant
/// bulunur — bu yüzden <see cref="PhoneNumberId"/> global unique'tir.</para>
/// </summary>
public sealed class WhatsAppAccount
{
    public Guid Id { get; set; }
    public Guid LicenseId { get; set; }
    public License License { get; set; } = null!;

    /// <summary>WhatsApp Business Account (WABA) id.</summary>
    public string WabaId { get; set; } = "";

    /// <summary>Graph gönderim hedefi: <c>/{PhoneNumberId}/messages</c>. Webhook
    /// yönlendirmesinde de anahtar — global unique.</summary>
    public string PhoneNumberId { get; set; } = "";

    /// <summary>Görüntülenen numara (E.164), yalnız UI için.</summary>
    public string DisplayPhoneNumber { get; set; } = "";

    /// <summary>Meta'da onaylı işletme adı (UI).</summary>
    public string? VerifiedName { get; set; }

    /// <summary>Tenant'ın kalıcı Access Token'ı — <c>IDataProtector</c> ile şifreli
    /// saklanır, asla düz metin dönmez.</summary>
    public string AccessTokenProtected { get; set; } = "";

    /// <summary>Numaranın iki adımlı doğrulama PIN'i — <c>IDataProtector</c> ile
    /// şifreli. Register sırasında biz belirliyoruz; Meta yeniden register'da
    /// AYNI PIN'i istiyor, saklamazsak yayıncı kendi numarasından kilitlenir.
    /// Elle bağlanan hesaplarda null (PIN'i biz belirlememişiz).</summary>
    public string? TwoStepPinProtected { get; set; }

    /// <summary>"active" | "disabled" | "revoked" (token geçersizleşti / Meta kapattı).</summary>
    public string Status { get; set; } = "active";

    /// <summary>Son gönderim/webhook hatası (ör. token expired) — panelde uyarı.</summary>
    public string? LastError { get; set; }

    public DateTimeOffset ConnectedAt { get; set; }
    public DateTimeOffset? DisconnectedAt { get; set; }
}
