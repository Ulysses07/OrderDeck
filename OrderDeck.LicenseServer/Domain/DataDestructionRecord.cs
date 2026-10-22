namespace OrderDeck.LicenseServer.Domain;

/// <summary>
/// Kişisel verinin silinmesi, boşaltılması ya da maskelenmesinin kalıcı kaydı (Silme Yönetmeliği md. 5/3, 7/3: en az üç yıl;
/// KVKK ön inceleme Ö-13). Kişisel veri İÇERMEZ: ne, ne zaman, kaç satır, hangi ölçütle, ne tetikledi, kim. Kimlikler takma
/// kimliktir (konu, talep, yapan yöneticinin <c>sub</c>'ı); ad, e-posta, IBAN, kullanıcı adı yazılmaz. Lisansa FK YOK: lisans
/// silinse de kayıt yaşar. Hiçbir iş bu tabloyu budamaz.
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
    /// <summary><c>system</c>, <c>cli</c>, <c>admin</c> ya da yöneticinin kimliği (<c>sub</c>,
    /// <c>DataDestruction.ActorOf</c>). Kullanıcı adı YAZILMAZ: tablo budanmaz, ad bir gerçek kişiyi gösterir (KVKK Ö-14);
    /// adı aynı işlemin denetim satırında (24 ay).</summary>
    public string Actor { get; set; } = "";
    /// <summary>Kaydın veritabanı dışındaki kopyaya (<c>DataDestructionDigestJob</c>'ın e-postası) gittiği an; <c>null</c> = henüz
    /// gitmedi. Yalnız gönderim başarılı olunca yazılır: düşen gönderim ya da kesinti sonraki koşuda telafi edilir.</summary>
    public DateTimeOffset? DigestedAt { get; set; }
}
