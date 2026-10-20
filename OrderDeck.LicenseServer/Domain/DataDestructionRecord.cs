namespace OrderDeck.LicenseServer.Domain;

/// <summary>
/// Kişisel verinin silinmesi, boşaltılması ya da maskelenmesinin kalıcı kaydı (Silme Yönetmeliği md. 5/3, 7/3: en az üç yıl;
/// KVKK ön inceleme Ö-13). Kişisel veri İÇERMEZ: ne, ne zaman, kaç satır, hangi ölçütle, ne tetikledi, kim. Lisansa FK YOK:
/// lisans silinse de kayıt yaşar. Hiçbir iş bu tabloyu budamaz.
/// <para><b>Geri yükleme:</b> tablo aynı veritabanındadır; yedekten geri yükleme onu da yedek anına sarar. Geri yüklemeden
/// sonra silmeleri yeniden uygulamanın dayanağı bu tablo DEĞİL, veritabanı dışındaki kopyasıdır
/// (<c>DataDestructionDigestJob</c>'ın saatlik e-postası; prosedür <c>deploy/README.md</c> "Geri yükleme").</para>
/// </summary>
public sealed class DataDestructionRecord
{
    public Guid Id { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public Guid? LicenseId { get; set; }
    /// <summary>Silmenin konusu olan kaydın takma kimliği (ShopperId, CustomerId): kişinin kendi verisi silinmiş ya da
    /// anonimleştirilmiştir, kimlik tek başına kimseyi göstermez. Geri yüklemeden sonra silme bununla yeniden uygulanır.</summary>
    public Guid? SubjectId { get; set; }
    /// <summary>Tablo ya da tablo.alan, ör. <c>BankTransactions.RawJson</c>.</summary>
    public string Target { get; set; } = "";
    /// <summary><c>DataDestruction.Actions</c> değerlerinden biri.</summary>
    public string Action { get; set; } = "";
    public int RowCount { get; set; }
    /// <summary>Ölçüt, ör. <c>OccurredAt+30g</c>. Kişisel veri yazılmaz.</summary>
    public string Criterion { get; set; } = "";
    /// <summary><c>DataDestruction.Triggers</c> değerlerinden biri.</summary>
    public string Trigger { get; set; } = "";
    /// <summary>Talep üzerine yapıldıysa talebin kimliği (banka veri talebi).</summary>
    public Guid? RequestId { get; set; }
    /// <summary><c>system</c> ya da yönetici kullanıcı adı.</summary>
    public string Actor { get; set; } = "";
}
