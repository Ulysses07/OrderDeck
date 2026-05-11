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
    bool RecipientPaysActive = false);
