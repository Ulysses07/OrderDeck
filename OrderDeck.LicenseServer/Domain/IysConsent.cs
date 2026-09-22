namespace OrderDeck.LicenseServer.Domain;

/// <summary>Bir alıcının ticari ileti izni. İYS'nin kendi sözlüğü.</summary>
public enum IysConsentStatus
{
    /// <summary>Henüz bilinmiyor / karar verilemiyor.</summary>
    Unknown = 0,
    Onay = 1,
    Ret = 2
}

/// <summary>Kaydın İYS'ye iletilme yolculuğu.</summary>
public enum IysPushState
{
    /// <summary>Yerelde yazıldı, henüz gönderilmedi.</summary>
    Pending = 0,
    /// <summary>/iys/add çağrıldı ve kuyruğa alındı — <b>kabul edildi demek değil</b>.</summary>
    Pushed = 1,
    /// <summary>/iys/search ONAY döndürdü. Gönderim yalnız bu durumda serbest.</summary>
    Confirmed = 2,
    /// <summary>Geçici hata; recovery deadline içinde yeniden dener.</summary>
    Failed = 3,
    /// <summary>3 iş günü doldu ya da kalıcı-veri hatası. Yeni onay olayı yeni pencere açar.</summary>
    Expired = 4
}

/// <summary>
/// Bir alıcının ticari ileti izninin <b>güncel</b> durumu — "şu an bu numaraya
/// ne yapabilirim?" sorusunun tek cevabı. Gönderim yolunda tek satır okunur.
///
/// <para><b><see cref="Status"/> ile <see cref="LastVerifiedStatus"/> neden ayrı
/// kolonlar.</b> İlki <i>bizim beyanımız</i>, ikincisi <i>İYS'nin cevabı</i>.
/// 2026-09-17'de 284 onayı kaybetmemizin sebebi ikisini bir sanmaktı: Netgsm'in
/// <c>code 0</c> yanıtı "kuyruğa alındı" demek, "kabul edildi" değil. Bu iki
/// alanı birleştiren her değişiklik o hatayı geri getirir.</para>
/// </summary>
public class IysConsent
{
    public Guid Id { get; set; }

    // (BrandCode, ChannelType, RecipientType, Recipient) = İYS'nin kendi
    // anahtarı; tekil index. İzin MARKA BAZINDA ayrıdır — aynı numara
    // 731734'te ONAY, 763208'de RET olabilir (2026-09-17'de ölçüldü).
    public string BrandCode { get; set; } = "";
    public string ChannelType { get; set; } = "MESAJ";
    public string RecipientType { get; set; } = "BIREYSEL";

    /// <summary>Daima E.164: <c>+905XXXXXXXXX</c>.</summary>
    public string Recipient { get; set; } = "";

    /// <summary>Bizim beyanımız — yerel olaylardan türer.</summary>
    public IysConsentStatus Status { get; set; } = IysConsentStatus.Unknown;

    /// <summary>İYS'ye beyan edilen ONAY tarihi. RET satırında eski onayın
    /// tarihi olarak kalır; reddin beyan tarihi <see cref="LastLocalEventAt"/>.</summary>
    public DateTimeOffset? ConsentDate { get; set; }

    /// <summary>İYS onay kaynağı kodu: HS_WEB / HS_MOBIL.</summary>
    public string? SourceCode { get; set; }

    public IysPushState PushState { get; set; } = IysPushState.Pending;

    /// <summary>ONAY: onay anı + 3 iş günü — sonrası hukuken geçersiz. RET:
    /// işlenme anı + 3 iş günü — sınırlı deneme penceresi (ret de 3 iş günü
    /// içinde İYS'de işlenmeli). Dolunca push işi <c>Expired</c> yazar.</summary>
    public DateTimeOffset? PushDeadline { get; set; }

    /// <summary>İYS'nin cevabı (/iys/search). Gönderim kapısı BUNU okur.</summary>
    public IysConsentStatus? LastVerifiedStatus { get; set; }
    public DateTimeOffset? LastVerifiedAt { get; set; }

    /// <summary>En güncel yerel olayın zamanı — RET'in diriltilmesini engelleyen sıra damgası.</summary>
    public DateTimeOffset LastLocalEventAt { get; set; }

    public DateTimeOffset? LastPushedAt { get; set; }

    /// <summary>Kaç kez doğrulama denendi (15dk → 1sa → 6sa → 24sa).</summary>
    public int VerifyAttempts { get; set; }
    public DateTimeOffset? NextVerifyAt { get; set; }

    /// <summary>Son hata özeti (admin listesi için; ham yanıt olay tablosunda).</summary>
    public string? LastError { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
