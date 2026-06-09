namespace OrderDeck.LicenseServer.Services.Sms;

/// <summary>
/// Bir SMS metninin kaç segmente böleceğini hesaplar (kredi = segment).
///
/// GSM-7 (03.38 temel alfabe): tek mesaj ≤160 septet, çok parçalıda 153/parça
/// (7 septet UDH başlığa gider). Genişletme tablosu karakterleri (^{}[]~|\€)
/// 2 septet sayılır.
///
/// Metin GSM-7 temel/genişletme alfabesi dışında bir karakter içeriyorsa
/// (ör. Türkçe ş, ğ, ı, ç) UCS-2'ye düşer: tek mesaj ≤70, çok parçalıda 67.
/// Bu, Netgsm "TR" encoding davranışıyla uyumludur (Türkçe → 70 hane).
/// </summary>
public static class SmsSegmentCalculator
{
    // GSM 03.38 temel alfabe (her biri 1 septet). Boşluk ve \n/\r dahil.
    private const string Gsm7Basic =
        "@£$¥èéùìòÇ\nØø\rÅåΔ_ΦΓΛΩΠΨΣΘΞ\u001bÆæßÉ !\"#¤%&'()*+,-./0123456789:;<=>?"
        + "¡ABCDEFGHIJKLMNOPQRSTUVWXYZÄÖÑÜ§¿abcdefghijklmnopqrstuvwxyzäöñüà";

    // Genişletme tablosu (her biri 2 septet). ESC zaten Basic içinde sayılıyor.
    private const string Gsm7Extension = "^{}\\[~]|€";

    public enum Encoding { Gsm7, Ucs2 }

    public readonly record struct Result(Encoding Encoding, int Segments);

    /// <summary>Mesaj → (encoding, segment sayısı). Boş/null → 1 segment (min).</summary>
    public static Result Calculate(string? message)
    {
        if (string.IsNullOrEmpty(message))
            return new Result(Encoding.Gsm7, 1);

        if (TryCountGsm7Septets(message, out var septets))
        {
            var segments = septets <= 160 ? 1 : CeilDiv(septets, 153);
            return new Result(Encoding.Gsm7, Math.Max(1, segments));
        }

        // UCS-2: UTF-16 kod birimi sayısı (surrogate çiftleri 2 sayılır).
        var units = message.Length;
        var ucsSegments = units <= 70 ? 1 : CeilDiv(units, 67);
        return new Result(Encoding.Ucs2, Math.Max(1, ucsSegments));
    }

    /// <summary>Yalnızca segment sayısı (kredi hesabı için kısayol).</summary>
    public static int Segments(string? message) => Calculate(message).Segments;

    private static bool TryCountGsm7Septets(string message, out int septets)
    {
        septets = 0;
        foreach (var ch in message)
        {
            if (Gsm7Basic.IndexOf(ch) >= 0) septets += 1;
            else if (Gsm7Extension.IndexOf(ch) >= 0) septets += 2;
            else { septets = 0; return false; } // GSM-7 dışı → UCS-2
        }
        return true;
    }

    private static int CeilDiv(int a, int b) => (a + b - 1) / b;
}
