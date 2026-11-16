using OrderDeck.Core.Customers;

namespace OrderDeck.App.Formatting;

/// <summary>
/// Müşteri satırının kimlik metinleri — tek tanım. Müşteri kartının kimlik rozeti ("Instagram: kullanici")
/// ve Ayır onayının hesap satırı ("Instagram @kullanici: 70,00 TL", ortak bakiye spec 2026-10-11 §6.1-e)
/// aynı platform adını ve aynı tanıtıcıyı gösterir. <c>CustomerDetailViewModel</c>'den taşındı.
/// </summary>
public static class CustomerIdentityText
{
    /// <summary>Platform kodunu okunabilir etikete çevirir (instagram → Instagram).</summary>
    public static string PlatformLabel(string? platform) => platform?.ToLowerInvariant() switch
    {
        "instagram" => "Instagram",
        "youtube" => "YouTube",
        "facebook" => "Facebook",
        "tiktok" => "TikTok",
        "twitch" => "Twitch",
        _ => string.IsNullOrWhiteSpace(platform) ? "—" : platform,
    };

    /// <summary>
    /// Kimlikte gösterilecek kullanıcı adı. Kayıt formundan gelen müşterilerde <c>DisplayName</c>'e
    /// Ad Soyad yazılıyor (arama listesinde isim görünsün diye), o yüzden <c>Display</c> kullanılamaz —
    /// "Instagram: Ayşe Önal" gibi, handle'ı hiç göstermeyen bir satır çıkar. YouTube istisna: orada
    /// <c>Username</c> = channelId, okunabilir @handle <c>DisplayName</c>'de durur.
    /// </summary>
    public static string Handle(Customer customer)
        => customer.Platform?.ToLowerInvariant() == "youtube" ? customer.Display : customer.Username;
}
