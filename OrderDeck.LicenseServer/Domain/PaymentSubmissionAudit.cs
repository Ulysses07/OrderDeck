namespace OrderDeck.LicenseServer.Domain;

/// <summary>
/// <see cref="PaymentSubmissionAudit.Outcome"/> için sabitler. Redler zaten
/// istemciye dönen hata kodunun kendisi olduğu için burada yalnız başarı hâli
/// var — ikinci bir sözlük tutmak, iki listenin sessizce ayrışması demekti.
/// </summary>
public static class SubmissionOutcomes
{
    public const string Ok = "ok";
}

/// <summary>
/// Shopper tarafından denenen her dekont gönderiminin izi. 90 gün retention
/// (yayıncı approval kararından sonra). FraudFlags + ParserConfidence karar
/// gerekçesini tarihselleştirir.
///
/// <para><b>Neden reddedilen denemeler de burada:</b> tablo aynı zamanda
/// <c>ShopperPaymentRateLimiter</c>'ın saydığı defter. Yalnız başarı
/// yazılırken oran sınırı hiç bağlamıyordu — geçersiz PDF, mükerrer dekont ya
/// da başka bir redde uğrayan istek sayaca dokunmadığı için "saatte 5" sınırı
/// sonsuz kez denenebiliyordu. Oysa reddedilen istek de PDF ayrıştırma ve bazı
/// yollarda R2 yazması demek; sınırın koruduğu iş tam olarak o.</para>
///
/// <para>Reddedilen denemenin izi ayrıca <i>kendi başına</i> dolandırıcılık
/// sinyali: arka arkaya çapraz-kiracı mükerrer denemesi, başarılı tek bir
/// gönderimden daha çok şey anlatır.</para>
/// </summary>
public sealed class PaymentSubmissionAudit
{
    public Guid Id { get; set; }

    /// <summary>
    /// Doğan ödeme; deneme reddedildiyse <c>null</c>. KVKK silmesi ve saklama
    /// işi bu tabloyu <c>ShopperId</c>/<c>CreatedAt</c> üzerinden buluyor,
    /// dolayısıyla ödemesiz satırlar temizlikten kaçmıyor.
    /// </summary>
    public Guid? PaymentId { get; set; }

    /// <summary>
    /// Denemenin sonucu: başarıda <c>"ok"</c>, aksi hâlde reddin hata kodu
    /// (<c>duplicate-dekont</c>, <c>invalid-pdf</c>, …).
    /// </summary>
    public string Outcome { get; set; } = "";

    public Guid ShopperId { get; set; }
    public Guid LicenseId { get; set; }   // Faz 0b-4: rate limit by license needs this
    public string IpAddress { get; set; } = "";
    public string UserAgent { get; set; } = "";
    public string FraudFlags { get; set; } = "";
    public string ParserConfidence { get; set; } = "";
    public string? ParserRawText { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
