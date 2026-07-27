namespace OrderDeck.LicenseServer.Services.WhatsApp;

/// <summary>
/// WhatsApp 24 saat "customer service window" kuralı.
///
/// <para>Müşteri bize yazdığında 24 saatlik bir pencere açılır; bu pencere içinde
/// serbest-metin (service) mesajı gönderilebilir ve <b>ücretlendirilmez</b>.
/// Pencere kapalıyken yalnız Meta'da onaylı <b>template</b> gönderilebilir ve
/// bu ücretlidir (business-initiated).</para>
///
/// <para>Pencereyi <b>yalnız müşteriden gelen mesaj</b> açar. Bizim gönderdiğimiz
/// mesaj (panel, AI ya da Business App'ten elle atılıp echo ile gelen) pencereyi
/// açmaz veya uzatmaz.</para>
///
/// <para>Saf fonksiyon — <c>now</c> dışarıdan verilir ki testler saat kaydırabilsin.</para>
/// </summary>
public static class WhatsAppServiceWindow
{
    public static readonly TimeSpan Duration = TimeSpan.FromHours(24);

    /// <summary>Son gelen mesajdan bu yana 24 saatten az geçtiyse pencere açıktır.</summary>
    public static bool IsOpen(DateTimeOffset? lastInboundAt, DateTimeOffset now) =>
        lastInboundAt is { } t && now - t < Duration;

    /// <summary>Pencerenin kapanacağı an (hiç inbound yoksa null).</summary>
    public static DateTimeOffset? ExpiresAt(DateTimeOffset? lastInboundAt) =>
        lastInboundAt is { } t ? t + Duration : null;

    /// <summary>Pencere kapanana kadar kalan süre; kapalıysa <see cref="TimeSpan.Zero"/>.</summary>
    public static TimeSpan Remaining(DateTimeOffset? lastInboundAt, DateTimeOffset now)
    {
        if (lastInboundAt is not { } t) return TimeSpan.Zero;
        var remaining = t + Duration - now;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }
}
