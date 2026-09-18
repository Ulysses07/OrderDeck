namespace OrderDeck.LicenseServer.Domain;

public enum IysConsentEventType
{
    /// <summary>Kişi yerelde onay verdi.</summary>
    LocalConsent = 0,
    /// <summary>Kişi yerelde onayı geri çekti.</summary>
    LocalRevoke = 1,
    /// <summary>/iys/add denemesi ve ham yanıtı.</summary>
    PushAttempt = 2,
    /// <summary>/iys/search sonucu.</summary>
    SearchResult = 3
}

/// <summary>
/// İzin geçmişi — <b>ekle-only</b>. Hiç silinmez, hiç güncellenmez.
///
/// <para>İspat burada yaşamak zorunda: <c>/iys/search</c> bize
/// <c>consentDate</c> ve <c>source</c> alanlarını BOŞ döndürüyor
/// (2026-09-17'de ölçüldü). Denetimde "bu kişi izni ne zaman, nereden, hangi
/// IP'den verdi" sorusunun cevabı İYS'den geri okunamaz.</para>
/// </summary>
public class IysConsentEvent
{
    public Guid Id { get; set; }

    /// <summary>Olayın ait olduğu yayıncı. Kanıtın kiracısı — <see cref="Recipient"/>
    /// tek başına yetmez: aynı telefon A markasında ONAY, B markasında RET olabilir
    /// (2026-09-17'de ölçüldü) ve denetimde ikisi ayrıştırılabilmeli.
    ///
    /// <para><b>FK DEĞİL ve olmayacak.</b> Bu depoda <c>LicenseId</c> taşıyan
    /// varlıkların neredeyse tamamı <c>HasOne(...).HasForeignKey(...).OnDelete(Cascade)</c>
    /// kuruyor; buradaki eksiklik unutulmuş değil, bilinçli. FK eklenirse lisans silindiğinde
    /// o yayıncının 6563 ispat olayları da silinir — tablo ekle-only olduğu için bu,
    /// kaydın kaybolabileceği TEK yoldur. Lisans ölse bile ispat yaşamalı.
    /// Aynı nedenle <b>navigasyon özelliği de eklemeyin</b>
    /// (ör. <c>public License? License { get; set; }</c>): EF, adı konvansiyona uyan
    /// bu sütun üzerinden sessizce cascade'li bir FK kurar ve karar hiçbir derleme
    /// hatası vermeden bozulur. Karar <c>IysConsentEventTenantColumnsTests</c>
    /// içindeki "hiç yabancı anahtar yok" testiyle çivilenmiştir.</para>
    ///
    /// <para>Toplayıcı artık bu sütunu DOLDURUYOR. Null kalması yalnız iki hâlde
    /// normaldir: (a) prod'daki eski olaylar — geriye dönük doldurulamıyorlar,
    /// (b) çağıranın lisansı çözemediği durum (olay <c>ErrorCode="no-brand"</c>
    /// ile yazılır). Yani <c>LicenseId IS NULL</c> tek başına bir hata filtresi
    /// DEĞİLDİR; hata sorgusu <see cref="ErrorCode"/>'a bakmalı.</para>
    /// </summary>
    public Guid? LicenseId { get; set; }

    /// <summary>Olayın ait olduğu İYS markası (Netgsm marka kodu). Toplayıcı
    /// bunu yayıncının doğrulanmış Netgsm hesabından çözüp yazıyor; null kalması
    /// prod'daki eski satırlar ile markanın hiç çözülemediği (<c>no-brand</c>)
    /// olayları işaretler — ikincisinde kayıt satırı da açılmamıştır.</summary>
    public string? BrandCode { get; set; }

    /// <summary>İlgili <see cref="IysConsent"/> durum satırına bağ.
    /// <b>FK DEĞİL</b> — durum satırı silinse bile olay kalmalı (tablo ekle-only ve
    /// hiç silinmez). Ad EF'in FK konvansiyonuna birebir uyduğu için risk gerçek:
    /// <b>navigasyon özelliği eklemeyin</b>
    /// (ör. <c>public IysConsent? IysConsent { get; set; }</c>) — EF o an sessizce
    /// cascade'li bir FK kurar. Toplayıcı artık dolduruyor; durum satırının hiç
    /// açılmadığı hâllerde (geçersiz numara, <c>no-brand</c>) null kalır.</summary>
    public Guid? IysConsentId { get; set; }

    /// <summary>E.164. <see cref="IysConsent.Recipient"/> ile eşleşir (FK değil — kayıt satırı silinse bile olay kalır).</summary>
    public string Recipient { get; set; } = "";

    public DateTimeOffset OccurredAt { get; set; }
    public IysConsentEventType EventType { get; set; }

    /// <summary>Olayın taşıdığı durum (push/search için de dolu).</summary>
    public IysConsentStatus Status { get; set; }

    /// <summary>Hangi tablo: "IntakeFormSubmission" / "Shopper" / null (API olayı).</summary>
    public string? SourceTable { get; set; }
    public Guid? SourceId { get; set; }

    // 6563 ispat yükü — yalnız yerel onay olaylarında dolu.
    public string? ProofIp { get; set; }
    public string? ProofUserAgent { get; set; }

    // Ham API yanıtı LOG'A DEĞİL buraya yazılır (telefon log'da maskeli).
    public string? ApiResponseCode { get; set; }
    public string? ApiResponseBody { get; set; }
    public string? ErrorCode { get; set; }
}
