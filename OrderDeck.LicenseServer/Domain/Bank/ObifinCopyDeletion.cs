namespace OrderDeck.LicenseServer.Domain.Bank;

/// <summary>
/// Obifin'deki hareket kopyasının silinmesi için yazılı teyit bekleyen istek (KVKK Ö-9 öneri 3, K-6; plan karar 21). "Kaldır ve
/// sil" açar (<c>BankConnectionTerminator.RemoveAndDeleteAsync</c>); yönetici Obifin'in yazılı teyidi gelince admin/obifin'de
/// "Teyit alındı" ile kapatır (<c>BankConnectionTerminator.ConfirmObifinCopyDeletedAsync</c>). Kişisel veri İÇERMEZ: lisans,
/// anlar, yöneticinin takma kimliği. Lisansa ve bağlantıya FK YOK: ikisi de silinse kayıt yaşar; hiçbir iş budamaz. Kapanana
/// kadar hizmet sonu işi (Görev 14) her gün hatırlatır.
/// </summary>
public sealed class ObifinCopyDeletion
{
    public Guid Id { get; set; }
    public Guid LicenseId { get; set; }
    /// <summary>"Kaldır ve sil" anı: Obifin'den yazılı silme bu andan beklenir.</summary>
    public DateTimeOffset RequestedAt { get; set; }
    /// <summary>Yöneticinin kimliği (<c>DataDestruction.ActorOf</c>), kullanıcı adı değil (KVKK Ö-14).</summary>
    public string RequestedBy { get; set; } = "";
    public DateTimeOffset? ConfirmedAt { get; set; }
    /// <summary>Teyidi kaydeden yöneticinin kimliği (<c>DataDestruction.ActorOf</c>).</summary>
    public string? ConfirmedBy { get; set; }
}
