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
