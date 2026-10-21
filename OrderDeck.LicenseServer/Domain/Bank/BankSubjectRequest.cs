namespace OrderDeck.LicenseServer.Domain.Bank;

public enum BankSubjectRequestKind { Access = 0, Erasure = 1 }

/// <summary>
/// Başvuranın kimliğinin ve IBAN'ın ona ait olduğunun nasıl doğrulandığı (KVKK Ö-11; Başvuru Tebliği m. 5'teki kanallar). Her
/// yöntemde IBAN sahipliği banka belgesiyle (dekont, hesap cüzdanı, banka ekstresi) görülür. Kişisel veri İÇERMEZ — yalnız
/// yöntem; belgenin kendisi kaydedilmez. Serbest metin BİLEREK yok.
/// </summary>
public enum BankSubjectVerification
{
    None = 0,
    /// <summary>Islak imzalı dilekçe + kimlik belgesi; IBAN banka belgesiyle.</summary>
    WetSignatureAndBankDocument = 1,
    /// <summary>KEP, güvenli elektronik imza ya da mobil imza; IBAN banka belgesiyle.</summary>
    ElectronicSignatureAndBankDocument = 2,
    /// <summary>Veri sorumlusunda önceden kayıtlı iletişim kanalı (ör. Shopper hesabının telefonu/e-postası); IBAN banka
    /// belgesiyle.</summary>
    RegisteredContactAndBankDocument = 3,
}

/// <summary>
/// Talebin nasıl kapandığı (KVKK m. 13/3: talep kabul edilir ya da gerekçesi açıklanarak reddedilir; Silme Yönetmeliği md.
/// 12/1-c gerekçeli ret). Kategoridir; sonuç metni koddan üretilir (<c>BankSubjectRequestService.OutcomeText</c>), serbest metin
/// YOK. Kişisel veri içermez.
/// </summary>
public enum BankSubjectResolution
{
    None = 0,
    /// <summary>Silme talebi uygulandı (yalnız <c>EraseAsync</c>).</summary>
    Erased = 1,
    /// <summary>Erişim talebi yanıtlandı, dışa aktarım başvurana iletildi (yalnız doğrulaması kayıtlı erişim talebi).</summary>
    Answered = 2,
    /// <summary>Kimlik ya da IBAN sahipliği doğrulanamadı; reddedildi (yalnız doğrulama kaydı OLMAYAN talep).</summary>
    RejectedNotVerified = 3,
    /// <summary>Kanuni gerekçeyle tamamen ya da kısmen reddedildi; gerekçe başvurana yazılı bildirildi.</summary>
    RejectedLegalGround = 4,
    /// <summary>Başvuran talebini geri çekti.</summary>
    Withdrawn = 5,
    /// <summary>Lisansın banka verisi silindiği için kapandı (bağlantı kaldırma, hizmet sonu, yayıncı silmesi; yalnız
    /// <c>CloseOpenForLicensesAsync</c>). Yönetici başvurana yine yanıt verir.</summary>
    DataDeleted = 6,
}

/// <summary>
/// Banka hareketi karşı tarafının (Shopper hesabı olsun olmasın) erişim ya da silme talebi (KVKK m. 11, m. 13/2; ön inceleme
/// K-7). Son tarih alış + 30 gün. IBAN düz saklanmaz: yalnız özet (talep kapanınca boşaltılır) ve kısaltılmış hâli (kapanıştan 30
/// gün sonra boşaltılır — SecurityDataRetentionJob). Lisansa FK BİLEREK yok (ShopperDeletionRequest kalıbı): talebin belgesi
/// lisans silinse de yaşar. Sonuç ve doğrulama yöntemi kişisel veri içermez.
/// </summary>
public sealed class BankSubjectRequest
{
    public Guid Id { get; set; }
    public Guid LicenseId { get; set; }
    public BankSubjectRequestKind Kind { get; set; }
    public string? IbanHash { get; set; }
    /// <summary>Kısaltılmış IBAN; kapanıştan 30 gün sonra "" olur.</summary>
    public string IbanMasked { get; set; } = "";
    /// <summary>Dışa aktarma ve silmeden önce zorunlu (<see cref="BankSubjectVerification"/>).</summary>
    public BankSubjectVerification Verification { get; set; }
    public DateTimeOffset? VerifiedAt { get; set; }
    /// <summary>Yönetici sütunları (<see cref="VerifiedBy"/>, <see cref="CreatedBy"/>, <see cref="ClosedBy"/>): yöneticinin kimliği
    /// (<c>DataDestruction.ActorOf</c>: <c>sub</c>) ya da <c>system</c>; kullanıcı adı DEĞİL (KVKK Ö-14: satır süresiz, karar 22).</summary>
    public string? VerifiedBy { get; set; }
    /// <summary>Talebin OrderDeck'e ulaştığı gün (TR gün başı, UTC).</summary>
    public DateTimeOffset ReceivedAt { get; set; }
    public DateTimeOffset DueAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string CreatedBy { get; set; } = "";
    /// <summary>Kapanış anı. Eşzamanlılık jetonu (şema değişmez): talep bir kez kapanır — aynı anda iki silme ya da kapatma
    /// (çift tık, iki yönetici) olursa ikincisinin kaydı düşer, sonucu ve imha kayıtlarını ikinci kez yazmaz.</summary>
    public DateTimeOffset? ClosedAt { get; set; }
    public string? ClosedBy { get; set; }
    /// <summary>Nasıl kapandığı (kategori); açıkken <see cref="BankSubjectResolution.None"/>.</summary>
    public BankSubjectResolution Resolution { get; set; }
    /// <summary>Sonucun metni: koddan üretilir (kategoriden ya da silmenin sayılarından), yönetici yazmaz. Kişisel veri yok.</summary>
    public string? Outcome { get; set; }
}
