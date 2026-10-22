namespace OrderDeck.LicenseServer.Domain;

/// <summary>
/// Müşterinin doldurduğu form. Polling endpoint bu kayıtları cursor (SubmittedAt) ile döndürür.
/// IpAddress + UserAgent audit için.
/// </summary>
public sealed class IntakeFormSubmission
{
    public Guid Id { get; set; }
    public Guid IntakeFormConfigId { get; set; }
    public IntakeFormConfig Config { get; set; } = null!;

    /// <summary>Legacy tek kullanıcı adı (Faz 4f). Çoklu-platform geçişinden
    /// sonra yeni gönderimlerde ilk dolu platform adına set edilir; eski WPF
    /// sync'i (yalnız Username okuyan) bozulmasın diye backward-compat.</summary>
    public string Username { get; set; } = "";

    // Çoklu-platform kullanıcı adları — her biri opsiyonel, formda en az 1 zorunlu.
    // Aynı kişinin farklı platform kimlikleri; WPF tarafında GroupId ile bağlanır.
    public string? YouTubeUsername { get; set; }
    public string? InstagramUsername { get; set; }
    public string? FacebookUsername { get; set; }
    public string? TikTokUsername { get; set; }

    /// <summary>YouTube handle doğrulanınca channels.list'ten çekilen channelId
    /// (UCxxx). Chat müşteri kaydı da channelId ile tutulduğundan, WPF bunu
    /// birebir eşleştirme için kullanır (handle→DisplayName tahmininden kesin).</summary>
    public string? YouTubeChannelId { get; set; }

    public string FullName { get; set; } = "";

    /// <summary>Adresin serbest metin kısmı — mahalle/cadde/sokak/no.
    /// İl ve ilçe ayrı kolonlarda. Bu ayrım e-Fatura toplu yükleme şablonu
    /// "Alıcı Şehir"/"Alıcı İlçe"/"Alıcı Sokak" kolonlarını ayrı istediği için
    /// var. 2026-08 öncesi kayıtlarda tüm adres bu alanda, City/District null.</summary>
    public string Address { get; set; } = "";

    /// <summary>İl — <see cref="Services.IntakeForm.TurkeyRegions"/> listesinden.
    /// Eski kayıtlarda null.</summary>
    public string? City { get; set; }

    /// <summary>İlçe — seçilen ilin ilçe listesinden. Eski kayıtlarda null.</summary>
    public string? District { get; set; }

    public string? Phone { get; set; }   // Phase 4g — E.164 format

    /// <summary>Formda zorunlu. DB'de nullable (eski satırlar null).</summary>
    public string? Email { get; set; }
    /// <summary>Fatura için opsiyonel TCKN. Yazma artık ŞİFRELİ
    /// (TcknProtector.Protect — PR-0b/2. sürüm); kolon yine de eski düz
    /// metin bir satır taşıyabilir (TcknBackfillJob o satırı şifreleyene
    /// kadar) — her okuma Unprotect ile ikisini de çözer. Bkz. TcknProtector
    /// sınıf dokümanı.</summary>
    public string? TcknProtected { get; set; }

    // Mesaj izinleri — şimdilik yalnız toplanır, gönderim ileride (Faz 4).
    public bool WhatsAppConsent { get; set; }
    public bool SmsConsent { get; set; }

    /// <summary>KVKK aydınlatma metninin okunduğu beyanının sunucu saati (UTC)
    /// — açık rıza DEĞİL, "okudum" onayı. Formda zorunlu; değer gönderimin
    /// kabul edildiği an (<see cref="SubmittedAt"/> ile aynı damga). 2026-10-13
    /// öncesi satırlarda null: o tarihten önce kutu yoktu.</summary>
    public DateTimeOffset? KvkkAcknowledgedAt { get; set; }

    /// <summary>Onaylanan metnin sürümü: sayfanın müşteriye gösterdiği
    /// <see cref="Services.IntakeForm.KvkkNotice.TextVersion"/> (gizli alandan
    /// gelir, yalnız o anki sabite eşitse kabul edilir). "Hangi metni okudu"
    /// sorusunun cevabı; metnin kendisi git geçmişinde o sürümün commit'inde. <see cref="KvkkAcknowledgedAt"/> ile birlikte dolu ya da
    /// birlikte null.</summary>
    public string? KvkkTextVersion { get; set; }

    /// <summary>Kayıt anında formda gösterilen veri sorumlusu bloğunun sürümü (<see cref="DataControllerProfileVersion"/>; KVKK
    /// m. 10/1-a ispatı, plan karar 24). Null: blok gösterilmedi (yayıncı bilgi girmemişti) ya da sayfanın bildirdiği sürüm o
    /// yayıncıda yok.</summary>
    public int? DataControllerVersion { get; set; }

    public DateTimeOffset SubmittedAt { get; set; }
    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }
}
