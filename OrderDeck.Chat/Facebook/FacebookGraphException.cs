using System;
using System.Net;
using System.Net.Http;

namespace OrderDeck.Chat.Facebook;

/// <summary>
/// Graph API 2xx dışı yanıt verdi. HTTP durumu taban sınıfın
/// <see cref="HttpRequestException.StatusCode"/> alanındadır; <see cref="Code"/> ve
/// <see cref="Subcode"/> hata gövdesindeki <c>error.code</c> / <c>error.error_subcode</c>
/// değerleridir (gövde okunamadıysa null).
///
/// <para>NEDEN ayrı tip: Graph, HTTP 400 altında 190 (token), 200 (izin), 4 (kota) gibi
/// ayrı anlamlar taşır; tek doğru sinyal gövdedeki koddur. Çağıran kodu mesaj metnini
/// ayrıştırmadan okuyabilsin diye istisna onu alan olarak taşır. Taban sınıf
/// <see cref="HttpRequestException"/> kaldı: onu yakalayan mevcut kod aynen çalışır.</para>
/// </summary>
public sealed class FacebookGraphException : HttpRequestException
{
    public FacebookGraphException(string message, HttpStatusCode statusCode, int? code, int? subcode)
        : base(message, inner: null, statusCode)
    {
        Code = code;
        Subcode = subcode;
    }

    /// <summary>Graph <c>error.code</c>; gövde okunamadıysa null.</summary>
    public int? Code { get; }

    /// <summary>Graph <c>error.error_subcode</c>; yoksa null.</summary>
    public int? Subcode { get; }
}

/// <summary>
/// Bağlanabilecek Sayfa sayısı sınırına ulaşıldı
/// (<see cref="FacebookOAuthService.MaxInstagramPages"/>). Ayrı tip: arayüz bu durumu
/// mesaj metnine bakmadan öteki <see cref="InvalidOperationException"/>'lardan ayırır.
/// </summary>
public sealed class FacebookPageLimitException : InvalidOperationException
{
    public FacebookPageLimitException(int limit)
        : base($"En çok {limit} Sayfa bağlanabilir.")
    {
    }
}
