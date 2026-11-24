namespace OrderDeck.Chat.Ingestors.YouTube;

/// <summary>Sohbet döngüsünün "etkin yayın var mı" sorusunun cevabı.</summary>
public enum BroadcastLookupStatus
{
    /// <summary>Etkin yayın bulundu; video ve sohbet kimliği sonuçta.</summary>
    Found,

    /// <summary>Hesap bağlı ama şu an etkin yayın yok. Yayın birazdan açılabilir: hızlı aranır.</summary>
    NoBroadcast,

    /// <summary>Diskte YouTube yetkisi yok. Arama Google'a hiç gitmedi, tarayıcı açılmadı.</summary>
    NotConnected,

    /// <summary>Operatörün düzeltmesi gereken ya da bekleyerek geçmeyecek sorun (oturum, yetki, kota);
    /// ipucu <see cref="BroadcastLookup.Message"/>'da.</summary>
    NeedsAttention,

    /// <summary>Geçici hata (ağ, sunucu, hız sınırı): yeniden denenir. Ayrıntı günlükte, şeritte değil.</summary>
    Failed,
}

/// <summary>
/// Etkin yayın aramasının sonucu. Arama istisna fırlatmaz (iptal dışında): sohbet döngüsü üç sonucu
/// (yayın yok, elle düzeltilmeli, geçici hata) ayırt edip farklı davranır ve durumu şeride yazar.
/// </summary>
/// <param name="Status">Sonucun türü.</param>
/// <param name="VideoId">Yayının video kimliği; yalnız <see cref="BroadcastLookupStatus.Found"/>'da.</param>
/// <param name="LiveChatId">Yayının sohbet kimliği; yalnız <see cref="BroadcastLookupStatus.Found"/>'da.</param>
/// <param name="Message">Operatöre gösterilecek ipucu; yalnız <see cref="BroadcastLookupStatus.NotConnected"/>
/// ve <see cref="BroadcastLookupStatus.NeedsAttention"/>'da.</param>
public readonly record struct BroadcastLookup(
    BroadcastLookupStatus Status,
    string? VideoId = null,
    string? LiveChatId = null,
    string? Message = null);

/// <summary>
/// YouTube sohbet satırının "elle düzeltilmeli" ipuçları (tasarım: Sohbeti yenile §5.4). Tek yerde:
/// aramanın sınıflandırması da servis döngüsü de aynı metinleri kullanır.
/// </summary>
public static class YouTubeStatusMessages
{
    public const string NotConnected = "YouTube hesabı bağlı değil. Ayarlar > YouTube'dan bağla.";

    public const string SessionExpired = "YouTube oturumunun süresi doldu. Ayarlar > YouTube'dan yeniden bağlan.";

    public const string NoPermission =
        "Bu YouTube hesabının kanalın canlı yayınını okuma yetkisi yok. Ayarlar > YouTube'dan doğru hesapla bağlan.";

    public const string QuotaExhausted = "Günlük YouTube API kotası doldu; sohbet yarına kadar okunamayabilir.";
}
