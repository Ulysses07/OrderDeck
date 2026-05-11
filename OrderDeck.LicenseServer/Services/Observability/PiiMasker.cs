namespace OrderDeck.LicenseServer.Services.Observability;

/// <summary>
/// PII (kişisel veri) masking helpers for log statements. KVKK perspectifinden,
/// hata log'larında müşteri email/telefon/isim ham olarak görünmemeli — log
/// dosyaları yedeklenince hassas veri kopyalanır, üçüncü kişiler tarafından
/// (destek ekibi vs.) görülebilir.
///
/// Bu helper'ı kullanım kuralı: structured logging parametresi olarak ham
/// değeri vermek yerine masked versiyon ver:
///
///   <c>_log.LogWarning("SMTP failed for {Email}", PiiMasker.MaskEmail(toEmail));</c>
///
/// Templates (ILogger context provider'lar tarafından enriched) zaten
/// structured — Mask helper return değeri direkt string olduğu için
/// log analytics tooling'i still kullanışlı kalır.
/// </summary>
public static class PiiMasker
{
    /// <summary>
    /// "ahmet@example.com" → "a***@e***.com". Boş/null veya invalid format
    /// için "***" döner. KVKK incident response'unda email pattern'i hâlâ
    /// audit edilebilir (domain TLD görünür) ama tam adres rebuild edilemez.
    /// </summary>
    public static string MaskEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email)) return "***";

        var at = email.IndexOf('@');
        if (at <= 0 || at == email.Length - 1) return "***";

        var local = email[..at];
        var domain = email[(at + 1)..];

        var localMasked = local.Length == 1
            ? local + "***"
            : local[0] + "***";

        var dot = domain.LastIndexOf('.');
        string domainMasked;
        if (dot <= 0 || dot == domain.Length - 1)
        {
            domainMasked = domain[0] + "***";
        }
        else
        {
            domainMasked = domain[0] + "***" + domain[dot..];
        }

        return $"{localMasked}@{domainMasked}";
    }

    /// <summary>
    /// "+90 555 123 45 67" → "***45 67" (son 4 hane). Boş/null → "***".
    /// Operatör destek vakasında müşteriyi son 4 hane ile eşleştirmek
    /// (banka standart pratiği) için yeterli; tam telefon rebuild edilemez.
    /// </summary>
    public static string MaskPhone(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone)) return "***";

        var digits = new System.Text.StringBuilder();
        foreach (var c in phone)
        {
            if (char.IsDigit(c)) digits.Append(c);
        }

        if (digits.Length < 4) return "***";

        var last4 = digits.ToString()[^4..];
        return "***" + last4;
    }

    /// <summary>
    /// "Ahmet Yıldız" → "A*** Y***". Tek kelime ise "A***".
    /// Destek vakasında "ilk harf eşleşmesi" için yeterli, gizlilik korur.
    /// </summary>
    public static string MaskName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "***";

        var parts = name.Trim().Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return "***";

        var masked = new string[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            masked[i] = parts[i].Length > 0 ? parts[i][0] + "***" : "***";
        }
        return string.Join(' ', masked);
    }
}
