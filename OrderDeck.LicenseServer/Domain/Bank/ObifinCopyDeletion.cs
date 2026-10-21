namespace OrderDeck.LicenseServer.Domain.Bank;

/// <summary>
/// Obifin'deki hareket kopyasının silinmesi için yazılı teyit bekleyen istek (KVKK Ö-9 öneri 3, K-6; plan karar 21). "Kaldır ve
/// sil" açar (<c>BankConnectionTerminator.RemoveAndDeleteAsync</c>); yönetici Obifin'in yazılı teyidi gelince admin/obifin'de
/// "Teyit alındı" ile kapatır (<c>BankConnectionTerminator.ConfirmObifinCopyDeletedAsync</c>). Kişisel veri İÇERMEZ: lisans,
/// anlar, yöneticinin takma kimliği, Obifin'de elle yapılacak iş (<see cref="ManualAction"/>). Lisansa ve bağlantıya FK YOK:
/// ikisi de silinse kayıt yaşar; hiçbir iş budamaz. Kapanana kadar hizmet sonu işi (Görev 14) her gün hatırlatır.
/// </summary>
public sealed class ObifinCopyDeletion
{
    /// <summary><see cref="ManualAction"/> sütununun uzunluğu; daha uzun metin kırpılarak yazılır.</summary>
    public const int ManualActionMaxLength = 1000;

    public Guid Id { get; set; }
    public Guid LicenseId { get; set; }
    /// <summary>"Kaldır ve sil" anı: Obifin'den yazılı silme bu andan beklenir.</summary>
    public DateTimeOffset RequestedAt { get; set; }
    /// <summary>Yöneticinin kimliği (<c>DataDestruction.ActorOf</c>), kullanıcı adı değil (KVKK Ö-14).</summary>
    public string RequestedBy { get; set; } = "";
    /// <summary>"Kaldır ve sil"in Obifin'de elle yapılacak işi (<c>BankRemovalResult.ManualActionText</c>): kaldırılamayan ya da
    /// bu lisansa ait olduğu doğrulanamayan banka bağlantılarının Obifin numaraları, Obifin'e ulaşılamadı, paylaşılan Obifin
    /// kullanıcısı. Kimlik bilgisi ve kişisel veri yok. <c>null</c> = elle iş yok, yalnız yazılı teyit bekleniyor. Obifin kimliği
    /// ve sahiplik kanıtı silindiği için bildirimi kaçıran yönetici bunu yalnız buradan görür: bekleyenler tablosu ve hizmet
    /// sonu hatırlatması gösterir (karar 21).</summary>
    public string? ManualAction { get; set; }
    public DateTimeOffset? ConfirmedAt { get; set; }
    /// <summary>Teyidi kaydeden yöneticinin kimliği (<c>DataDestruction.ActorOf</c>).</summary>
    public string? ConfirmedBy { get; set; }
}
