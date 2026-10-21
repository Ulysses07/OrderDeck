namespace OrderDeck.LicenseServer.Domain.Bank;

/// <summary>Obifin'deki banka hesabının yerel görüntüsü; Obifin hesap listesinden tazelenir
/// (<see cref="RefreshedAt"/>). IBAN yalnız hash + maske (spec §7).</summary>
public sealed class BankAccount
{
    public Guid Id { get; set; }
    public Guid LicenseId { get; set; }
    public License License { get; set; } = null!;
    public Guid? BankConnectionId { get; set; }
    public long ObifinAccountId { get; set; }
    public string BankaKodu { get; set; } = "";
    public string IbanMasked { get; set; } = "";
    public string? IbanHash { get; set; }
    public string Currency { get; set; } = "TL";
    public bool Active { get; set; }
    /// <summary>Obifin `GuncellemeTarihi` — bankadan son çekim; gecikme ölçümü.</summary>
    public DateTimeOffset? LastBankSyncAt { get; set; }
    public string? NotificationNote { get; set; }
    public DateTimeOffset RefreshedAt { get; set; }
    // KVKK Ö-7: bu hesaba yazılan gelen hareketlerde karşı IBAN'ın niteliği (değer yazılmaz). Maskeli sayısı sıfır değilse o
    // bankanın IBAN'ları özetlenemiyor demektir; Faz 2'deki "tek kişiye bağlı IBAN" koşulu o bankada kurulamaz.
    public int IbanFullCount { get; set; }
    public int IbanMaskedCount { get; set; }
    public int IbanEmptyCount { get; set; }
    /// <summary>Yöneticinin bu hesabı lisansa bağladığı an (KVKK Ö-10). Null = onaysız: hareketi hiçbir lisansa yazılmaz, yalnız
    /// <see cref="UnapprovedSkippedCount"/> artar. Yalnız yayıncının müşteri tahsilatı hesabı onaylanır. Göç mevcut hesapları
    /// onaylamaz (fail-closed); Obifin'den gelen yeni hesap (yer tutucu ya da hesap yenilemesi) onaysız doğar.</summary>
    public DateTimeOffset? ApprovedAt { get; set; }
    /// <summary>Onaylayan yöneticinin kimliği (<c>DataDestruction.ActorOf</c>: <c>sub</c>), kullanıcı adı DEĞİL (KVKK Ö-14): satır
    /// budanmaz, ad bir gerçek kişiyi gösterir; adı aynı işlemin 24 aylık denetim satırında.</summary>
    public string? ApprovedBy { get; set; }
    /// <summary>Onaysızken atlanan hareket sayısı (yönetim sayfasındaki uyarı). Çekim turunun sonunda imleçle aynı kayıtta yazılır:
    /// yarıda kalıp aynı imleçten yeniden çekilen hareket iki kez sayılmaz.</summary>
    public int UnapprovedSkippedCount { get; set; }
}
