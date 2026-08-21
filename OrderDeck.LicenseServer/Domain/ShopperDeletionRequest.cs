namespace OrderDeck.LicenseServer.Domain;

/// <summary>
/// Shopper'ın KVKK silme talebi. Talep geldiği anda hesap soft-delete olur
/// (oturum kapanır), ama kişisel veri admin panelinden elle silinene kadar
/// durur.
///
/// Neden <see cref="ShopperSupportRequest"/> içine bir Kind olarak değil de
/// ayrı tablo: bu satır aynı zamanda KVKK kanıtı — "talep şu tarihte geldi,
/// şu tarihte şu admin işledi". Destek taleplerinin arasına karışıp
/// çözülmüş sayılması ya da destek tablosunun bekletme kuralıyla silinmesi
/// kabul edilemez.
///
/// Shopper'a FK YOK. Purge, Shopper satırını anonimleştiriyor ama silmiyor;
/// yine de ileride satır tamamen silinirse bu kayıt ayakta kalmalı, çünkü
/// talebi karşıladığımızın tek belgesi bu.
/// </summary>
public sealed class ShopperDeletionRequest
{
    public Guid Id { get; set; }

    /// <summary>Talebi yapan shopper. Navigation property bilerek yok.</summary>
    public Guid ShopperId { get; set; }

    /// <summary>
    /// Talep anındaki telefon. Purge sonrası Shopper satırında telefon
    /// kalmayacağı için, listeyi işleyen admin kimin talebine baktığını
    /// başka türlü göremez.
    /// </summary>
    public string PhoneAtRequest { get; set; } = "";

    public DateTimeOffset RequestedAt { get; set; }

    /// <summary>Null ise bekliyor. Doluysa veri silinmiş demektir.</summary>
    public DateTimeOffset? HandledAt { get; set; }

    /// <summary>İşlemi yapan admin (AdminCookie'deki kimlik).</summary>
    public string? HandledBy { get; set; }

    /// <summary>Silme sırasında ne yapıldığının özeti (kaç dekont silindi vb.).</summary>
    public string? Notes { get; set; }
}
