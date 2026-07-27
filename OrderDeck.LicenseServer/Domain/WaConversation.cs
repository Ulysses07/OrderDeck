namespace OrderDeck.LicenseServer.Domain;

/// <summary>
/// Bir yayıncı ile bir müşteri numarası arasındaki WhatsApp sohbeti (thread).
/// Tenant başına numara benzersiz: (LicenseId, CustomerPhone).
///
/// <para><b>24 saat kuralı:</b> serbest-metin (service) mesajı yalnız müşterinin
/// son mesajından itibaren 24 saat içinde gönderilebilir. Pencere
/// <see cref="LastInboundAt"/> üzerinden hesaplanır; kapalıysa onaylı template
/// gerekir. Elle Business App'ten atılan mesajlar da <c>smb_message_echoes</c>
/// ile buraya düşer ama pencereyi <b>açmaz</b> (pencereyi yalnız inbound açar).</para>
/// </summary>
public sealed class WaConversation
{
    public Guid Id { get; set; }
    public Guid LicenseId { get; set; }
    public License License { get; set; } = null!;

    /// <summary>Müşteri numarası E.164 ('+' olmadan, Meta'nın <c>wa_id</c> formatı).</summary>
    public string CustomerPhone { get; set; } = "";

    /// <summary>WhatsApp profil adı (contacts[].profile.name) — değişebilir.</summary>
    public string? ProfileName { get; set; }

    /// <summary>Bu sohbetin geldiği/gittiği tenant numarası — hesap değişirse iz kalır.</summary>
    public string PhoneNumberId { get; set; } = "";

    /// <summary>Müşteriden gelen SON mesajın zamanı. 24s service penceresi bundan sayılır.</summary>
    public DateTimeOffset? LastInboundAt { get; set; }

    /// <summary>Yön farketmeksizin son mesaj zamanı — listede sıralama için.</summary>
    public DateTimeOffset? LastMessageAt { get; set; }

    /// <summary>Listede rozet: okunmamış gelen mesaj sayısı.</summary>
    public int UnreadCount { get; set; }

    /// <summary>"open" | "closed" (operatör kapattı; yeni inbound tekrar açar).</summary>
    public string Status { get; set; } = "open";

    /// <summary>Faz 2 (AI): asistan bu sohbete yanıt versin mi. AI devri alındığında
    /// (<see cref="HandedOffAt"/>) false'a çekilir.</summary>
    public bool AiEnabled { get; set; }

    /// <summary>AI'nın "kontrol sağlanacak" deyip insana devrettiği an. Null = devir yok.</summary>
    public DateTimeOffset? HandedOffAt { get; set; }

    /// <summary>Varsa eşleşen WPF müşteri kaydı (telefon üzerinden) — sipariş bağlamı için.</summary>
    public Guid? WpfCustomerId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
