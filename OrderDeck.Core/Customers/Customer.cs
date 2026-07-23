namespace OrderDeck.Core.Customers;

public sealed record Customer(
    string Id,
    string Platform,
    string Username,
    string? DisplayName,
    string? AvatarUrl,
    long FirstSeenAt,
    long LastSeenAt,
    bool IsBlacklisted,
    string? BlacklistReason,
    string? Notes,
    int TotalLabelsPrinted,
    decimal TotalAmount,
    long? BlacklistedAt,
    string? Address,
    string? Phone,   // Phase 4g
    // Kargo PR F (2026-05-11): vendor "Alıcı Ödemeli" seçimi sonrası true.
    // Print template etikete "ALICI ÖDEMELİ" kırmızı yazı render eder.
    // Sticky flag — vendor müşterinin sevkıyatı bitince Customer detail
    // dialog'tan (gelecek) veya direkt SQL ile clear eder. MVP compromise.
    bool RecipientPaysActive = false,
    // Intake form çoklu-platform (2026-07-20): aynı kişinin farklı platform
    // satırlarını bağlayan grup kimliği. Null = gruplanmamış (tekil kimlik).
    // Kara liste/çekiliş grup-bazlı kontrol için kullanılır.
    string? GroupId = null,
    string? Email = null,
    string? Tckn = null,
    bool WhatsAppConsent = false,
    bool SmsConsent = false,
    // Kayıt formundaki gerçek Ad Soyad. DisplayName'den ayrı: chat satırlarında
    // DisplayName platform takma adıdır; bu alan formun verdiği gerçek ismi tutar.
    string? FullName = null)
{
    /// <summary>Operatöre gösterilecek ad: DisplayName varsa o, yoksa Username'e
    /// düşer. YouTube'da Username = channelId (kalıcı kimlik); listede okunabilir
    /// ad (DisplayName, ör. @handle) gösterilir.</summary>
    public string Display => string.IsNullOrWhiteSpace(DisplayName) ? Username : DisplayName!;
}
