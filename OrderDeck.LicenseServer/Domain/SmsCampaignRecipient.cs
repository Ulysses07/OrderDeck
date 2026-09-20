namespace OrderDeck.LicenseServer.Domain;

/// <summary>
/// Bir <see cref="SmsCampaign"/> içindeki tek alıcı + gönderim sonucu (audit).
/// Kampanya oluşturulurken o anki izinli müşterilerden snapshot'lanır.
/// </summary>
public sealed class SmsCampaignRecipient
{
    public Guid Id { get; set; }
    public Guid CampaignId { get; set; }
    public SmsCampaign Campaign { get; set; } = null!;

    /// <summary>Kaynak WpfCustomerProjection.Id (audit). Manuel numarada null.</summary>
    public Guid? WpfCustomerId { get; set; }

    public string Phone { get; set; } = "";

    /// <summary>
    /// "pending" | "sending" | "sent" | "failed" | "skipped".
    ///
    /// <para><c>sending</c> = "gitmiş OLABİLİR, bilmiyoruz". Gönderim işi
    /// satırı fiziksel gönderimden ÖNCE bu duruma çeker (Görev 16); süreç
    /// gönderim ile sonuç yazımı arasında ölürse satır burada kalır ve
    /// BİLİNÇLİ olarak kurtarılmaz — bkz. <see cref="ClaimedAt"/>.</para>
    ///
    /// <para><c>skipped</c> = İYS kapısı eledi (onay yok/ret) — altyapı
    /// arızası DEĞİL, sistem doğru çalıştı (§3.3). <c>failed</c>'dan ayrı
    /// tutulur: yayıncıya "47 başarısız" göstermek arıza sandırır; skipped
    /// oranı ayrıca kötüye kullanımın tek erken göstergesi.</para>
    /// </summary>
    public string Status { get; set; } = "";

    /// <summary>
    /// Alıcıyı hangi işçi ne zaman talep etti. <b>Eşzamanlılık jetonu</b>:
    /// talep yazımı "UPDATE ... WHERE ClaimedAt = okunan değer" olarak gider,
    /// yani iki işçi aynı alıcıyı asla birlikte üstlenemez. Kaybeden
    /// <c>DbUpdateConcurrencyException</c> alır ve gönderim YAPMADAN geçer.
    ///
    /// <para>Kampanya düzeyindeki <c>SmsCampaign.ClaimedAt</c> bunu kapatmıyor:
    /// o yoklama gönderimden ÖNCE koşuyor, yarış ise gönderim ile sonuç yazımı
    /// ARASINDA. Alıcı satırı gönderimden önce talep edilmezse devralan işçi
    /// aynı kişiye ikinci ticari SMS gönderir (para + 6563). Gerekçe: Görev 16.
    /// </para>
    ///
    /// <para><b><c>sending</c>'de takılı satır neden kurtarılmaz.</b>
    /// <c>pending</c>'e geri çevirmek gitmiş olabilecek bir SMS'i ikinci kez
    /// göndermek; <c>failed</c> saymak ise gitmiş olabilecek bir SMS'in
    /// kredisini iade etmek olurdu. İkisi de yanlış yönde hata. Bu yüzden
    /// satır olduğu gibi bırakılır: bir daha gönderilmez, iade edilmez — hem
    /// para hem hukuk yönünde fail-closed.</para>
    /// </summary>
    public DateTimeOffset? ClaimedAt { get; set; }

    /// <summary>failed VE skipped durumunda sebep; CampaignPause sınıfı bir
    /// hatada (§3.4) pending'e geri dönen satıra son ret kodu yazılabilir.</summary>
    public string? Error { get; set; }

    /// <summary>Netgsm'in kabul yanıtındaki <c>jobid</c> (§3.4 karar 3).
    /// Rapor-aşaması mutabakatı bugün YOK (§9.3 doğrulanmamış); sütun ileride
    /// "gitti mi" sorusuna ham veri bırakır. Gönderim kabul edilmediyse null.</summary>
    public string? ProviderJobId { get; set; }

    public DateTimeOffset? SentAt { get; set; }
}
