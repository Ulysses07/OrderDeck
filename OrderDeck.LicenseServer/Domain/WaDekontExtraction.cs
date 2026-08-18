namespace OrderDeck.LicenseServer.Domain;

/// <summary>
/// WhatsApp'tan gelen PDF dekontun <c>PdfDekontParser</c> ile çıkarılmış
/// alanları. Panelde etiketin yanında gönderen/tutar/tarih/referans görünsün
/// diye tutulur — yayıncı PDF'i açmadan karar verebilsin.
///
/// <para>Bu satır bir <c>Payment</c> DEĞİL ve otomatik ödeme kaydı üretmez:
/// gelenin gerçekten dekont olduğu bilinmiyor, karar insanın.</para>
///
/// <para>Görsel dekontlar kapsam dışı (AI gerektirir, ayrı faz).</para>
/// </summary>
public sealed class WaDekontExtraction
{
    /// <summary>PK ve FK aynı: bir mesajın en fazla bir ayrıştırması olur.</summary>
    public Guid WaMessageId { get; set; }
    public WaMessage WaMessage { get; set; } = null!;

    /// <summary>Sorguları mesaja join'lemeden tenant'a kapatmak için denormalize
    /// — <c>WaMessage.LicenseId</c> ile aynı gerekçe. Gezinme özelliği YOK:
    /// silme <c>WaMessage</c> üzerinden akıyor, License'tan ikinci bir cascade
    /// yolu açılmasın.</summary>
    public Guid LicenseId { get; set; }

    public string? PayerName { get; set; }
    public decimal? Amount { get; set; }
    public DateTimeOffset? PaidAt { get; set; }
    public string? ReferansNo { get; set; }

    /// <summary>PDF'in SHA-256'sı. Bugün yalnız teşhis için tutuluyor;
    /// mükerrer dekont tespiti KAPSAM DIŞI.</summary>
    public string PdfHash { get; set; } = "";

    /// <summary>"High" | "Medium" | "Low" — <c>ParserConfidenceCalculator</c>.</summary>
    public string ParserConfidence { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; }
}
