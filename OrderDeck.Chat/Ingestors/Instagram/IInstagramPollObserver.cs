using System;

namespace OrderDeck.Chat.Ingestors.Instagram;

/// <summary>Bir yoklama turunun (tek <c>live_media</c> isteğinin) sonucu.</summary>
public enum InstagramPollKind
{
    /// <summary>Canlı yayın var, yorum sayfası okundu.</summary>
    Ok,

    /// <summary>Şu an canlı yayın yok ya da yayın bitti.</summary>
    NoBroadcast,

    /// <summary>Geçici hata (ağ, zaman aşımı, sınıflandırılamayan Graph hatası); döngü yeniden
    /// dener, art arda yinelenirse çıkar.</summary>
    Transient,

    /// <summary>Kota aşıldı; döngü bildirilen süre kadar bekler.</summary>
    RateLimited,

    /// <summary>Kalıcı hata (token ya da izin); döngü durur.</summary>
    Fatal,
}

/// <summary>
/// <see cref="InstagramLiveCommentsPoller"/>'ın turlarını dışarıdan izler: hesap başına
/// sağlık ve kota ölçümü turun sonucunu poller'ın dışından görmek zorunda.
///
/// <para><b>Çağrı sözleşmesi:</b> her turda bir <see cref="RoundStarting"/>, ardından en
/// çok bir <see cref="RoundCompleted"/> (istek iptalle kesilirse ya da döngü çökerse
/// gelmez). Çağrılar poller döngüsünün içinden yapılır; gözlemcinin fırlattığı istisna
/// yutulur, döngüyü durduramaz.</para>
/// </summary>
public interface IInstagramPollObserver
{
    /// <summary>Tur başlıyor: Graph isteği birazdan gönderilecek.</summary>
    void RoundStarting();

    /// <summary>
    /// Tur sonuçlandı. <see cref="InstagramPollKind.Ok"/>'te o turun yorumları
    /// yayımlandıktan <b>sonra</b>, öbür sonuçlarda poller beklemeye girmeden ya da
    /// döngüden çıkmadan <b>önce</b> çağrılır.
    /// </summary>
    /// <param name="kind">Turun sonucu.</param>
    /// <param name="message">Yalnız <see cref="InstagramPollKind.Fatal"/>'da dolu: operatöre
    /// gösterilecek Türkçe mesaj (<see cref="InstagramLiveCommentsPoller.FatalReason"/> ile aynı
    /// metin). Öbür sonuçlarda null.</param>
    /// <param name="retryAfter">Yalnız <see cref="InstagramPollKind.RateLimited"/>'da dolu:
    /// poller'ın bekleyeceği süre. Öbür sonuçlarda <see cref="TimeSpan.Zero"/>.</param>
    /// <param name="usageHeader">Yanıtın <c>X-Business-Use-Case-Usage</c> başlığının ham metni
    /// (birden çok değer "," ile birleştirilir); başlık yoksa ya da istek düştüyse null.</param>
    void RoundCompleted(InstagramPollKind kind, string? message, TimeSpan retryAfter, string? usageHeader);
}
