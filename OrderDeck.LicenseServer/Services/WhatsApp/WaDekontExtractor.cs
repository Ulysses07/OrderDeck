using Microsoft.Extensions.Logging;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.ShopperPayments;
using OrderDeck.PdfParsing;

namespace OrderDeck.LicenseServer.Services.WhatsApp;

/// <summary>
/// WhatsApp'tan gelen PDF dekontu mevcut <see cref="IPdfDekontParser"/>'dan
/// geçirip panelde etiketin yanında gösterilecek satırı üretir.
///
/// <para><b>Neden hiç fırlatmıyor:</b> çağıran <c>WhatsAppInboundJob</c> —
/// bir Hangfire job'ı. Bozuk/şifreli/taranmış bir PDF exception atarsa job
/// retry'a girer ve <i>mesajın kendisi</i> tekrar tekrar işlenir. Ayrıştırma
/// ikincil veri: etiket zaten yapıştı, operatör sohbeti açıp PDF'i kendi
/// okuyabilir.</para>
///
/// <para><b>Kapsam dışı:</b> görsel dekontlar (AI gerektirir, ayrı faz) ve
/// mükerrer dekont tespiti. <c>PdfHash</c> bugün yalnız teşhis için saklanır.</para>
/// </summary>
public sealed class WaDekontExtractor
{
    /// <summary>Türkiye 2016'dan beri kalıcı UTC+3, yaz saati uygulaması yok —
    /// dolayısıyla dekonttaki yerel saati sabit offset ile çevirmek güvenli.</summary>
    private static readonly TimeSpan TurkeyOffset = TimeSpan.FromHours(3);

    private readonly IPdfDekontParser _parser;
    private readonly ILogger<WaDekontExtractor> _log;

    public WaDekontExtractor(IPdfDekontParser parser, ILogger<WaDekontExtractor> log)
    {
        _parser = parser;
        _log = log;
    }

    /// <summary>Ayrıştırılamayan her durumda <c>null</c> döner.</summary>
    public WaDekontExtraction? TryExtract(Guid licenseId, Guid waMessageId, byte[]? pdfBytes)
    {
        if (pdfBytes is null || pdfBytes.Length == 0) return null;

        PdfDekontParser.ParseResult result;
        try
        {
            result = _parser.Parse(pdfBytes);
        }
        catch (Exception ex)
        {
            _log.LogWarning(
                ex, "WhatsApp dekontu ayrıştırılamadı: mesaj {MessageId}", waMessageId);
            return null;
        }

        return new WaDekontExtraction
        {
            WaMessageId = waMessageId,
            LicenseId = licenseId,
            PayerName = result.PayerName,
            Amount = result.Amount,
            PaidAt = ToTurkeyOffset(result.PaidAt),
            ReferansNo = result.ReferansNo,
            PdfHash = result.PdfHash,
            ParserConfidence = ParserConfidenceCalculator.Compute(result),
            CreatedAt = DateTimeOffset.UtcNow,
        };
    }

    private static DateTimeOffset? ToTurkeyOffset(DateTime? value)
        => value is null
            ? null
            : new DateTimeOffset(
                DateTime.SpecifyKind(value.Value, DateTimeKind.Unspecified), TurkeyOffset);
}
