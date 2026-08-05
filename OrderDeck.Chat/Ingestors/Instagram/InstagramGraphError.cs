using System;
using System.Text.Json;

namespace OrderDeck.Chat.Ingestors.Instagram;

/// <summary>Bir Graph hatasına nasıl tepki verileceği.</summary>
public enum InstagramErrorKind
{
    /// <summary>Geri çekilip tekrar dene; oturumu öldürme.</summary>
    Transient,

    /// <summary>Token süresi doldu/iptal edildi. Döngüyü durdur, kullanıcıdan
    /// yeniden bağlanmasını iste. Sonsuz retry yok.</summary>
    TokenExpired,

    /// <summary>İzin verilmemiş. Döngüyü durdur, farklı mesaj göster.</summary>
    PermissionDenied,

    /// <summary>Kota aşıldı. <see cref="InstagramGraphError.TryGetRetryAfter"/>
    /// ile başlıktan gelen süre kadar bekle; sabit backoff uydurma.</summary>
    RateLimited,

    /// <summary>Yayın bitti — media artık okunamıyor. Bu bir hata değil,
    /// normal yaşam döngüsü sonu.</summary>
    BroadcastEnded,
}

/// <summary>
/// Graph hata gövdesini ve kota başlığını yorumlayan saf yardımcılar.
/// Ağ erişimi yok, tamamen test edilebilir.
/// </summary>
public static class InstagramGraphError
{
    /// <summary>
    /// Hata gövdesini sınıflandırır. Gövde ayrıştırılamazsa
    /// <see cref="InstagramErrorKind.Transient"/> döner — bilinmeyende
    /// oturumu öldürmüyoruz.
    /// </summary>
    /// <param name="httpStatus">Bilerek kullanılmıyor: Graph hataları HTTP 400
    /// altında da 190/200/613 gibi ayrı anlamlar taşıyor, tek doğru sinyal
    /// gövdedeki <c>error.code</c>. İmzada duruyor ki çağıran taraf statüyü
    /// elinde tutmaya devam etsin ve ileride gövdesiz (5xx) ayrımı gerekirse
    /// çağıranlar değişmesin.</param>
    public static InstagramErrorKind Classify(int httpStatus, string? body)
    {
        int? code = null, subcode = null;

        if (!string.IsNullOrWhiteSpace(body))
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("error", out var err))
                {
                    if (err.TryGetProperty("code", out var c) && c.TryGetInt32(out var ci))
                        code = ci;
                    if (err.TryGetProperty("error_subcode", out var s) && s.TryGetInt32(out var si))
                        subcode = si;
                }
            }
            catch (JsonException) { /* HTML hata sayfası vb. → Transient */ }
        }

        if (subcode == 2446079) return InstagramErrorKind.RateLimited;

        return code switch
        {
            190 or 463 => InstagramErrorKind.TokenExpired,
            200 or 10 => InstagramErrorKind.PermissionDenied,
            4 or 17 or 32 or 613 or 80002 => InstagramErrorKind.RateLimited,
            100 => InstagramErrorKind.BroadcastEnded,
            _ => InstagramErrorKind.Transient,
        };
    }

    /// <summary>
    /// <c>X-Business-Use-Case-Usage</c> başlığından beklenmesi gereken süreyi
    /// çıkarır. Meta <c>estimated_time_to_regain_access</c> değerini
    /// <b>dakika</b> cinsinden verir. Birden çok kova varsa en uzunu alınır.
    /// </summary>
    public static bool TryGetRetryAfter(string? headerValue, out TimeSpan wait)
    {
        wait = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(headerValue)) return false;

        int maxMinutes = 0;
        try
        {
            using var doc = JsonDocument.Parse(headerValue);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;

            foreach (var appEntry in doc.RootElement.EnumerateObject())
            {
                if (appEntry.Value.ValueKind != JsonValueKind.Array) continue;
                foreach (var bucket in appEntry.Value.EnumerateArray())
                {
                    if (bucket.ValueKind != JsonValueKind.Object) continue;
                    // TryGetDouble: Meta bu alanı bazen `12.0` gibi kesirli
                    // yazıyor; TryGetInt32 onu sessizce atlar ve kota
                    // aşımında backoff hiç uygulanmaz. Yukarı yuvarlıyoruz.
                    if (bucket.TryGetProperty("estimated_time_to_regain_access", out var e) &&
                        e.ValueKind == JsonValueKind.Number &&
                        e.TryGetDouble(out var raw))
                    {
                        int minutes = (int)Math.Ceiling(raw);
                        if (minutes > maxMinutes) maxMinutes = minutes;
                    }
                }
            }
        }
        catch (JsonException) { return false; }

        if (maxMinutes <= 0) return false;
        wait = TimeSpan.FromMinutes(maxMinutes);
        return true;
    }
}
