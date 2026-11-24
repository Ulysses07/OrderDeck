namespace OrderDeck.Chat.Ingestors.YouTube;

/// <summary>YouTube okuyucusunun neden çıktığı.</summary>
public enum YouTubeIngestorEnd
{
    /// <summary>Okuyucu dışarıdan kesildi (yenileme, oturum bitişi, uygulama kapanışı) ya da henüz
    /// çıkmadı.</summary>
    Cancelled,

    /// <summary>Platform yayının bittiğini ya da sohbetin kapandığını bildirdi.</summary>
    BroadcastEnded,

    /// <summary>Akış hatayla koptu; döngü bekleyip yeniden dener.</summary>
    Failed,
}

/// <summary>
/// Servis döngüsünün YouTube okuyucusundan beklediği sözleşme. Arayüz, döngünün gerçek ağ olmadan
/// sınanabilmesi için var; ürün kodundaki tek uygulaması <see cref="YouTubeOfficialChatIngestor"/>.
/// </summary>
public interface IYouTubeChatIngestor : IDisposable
{
    /// <summary>Okuyucuyu başlatır. Yayının sohbeti yoksa <see cref="YouTubeNoLiveChatException"/>
    /// fırlatır.</summary>
    Task StartAsync(CancellationToken ct);

    /// <summary>Okuyucuyu durdurur ve çıkmasını bekler.</summary>
    Task StopAsync(CancellationToken ct);

    /// <summary>Okuyucu çıkınca (yayın bitti, hata, iptal) tamamlanır.</summary>
    Task Completion { get; }

    /// <summary>Okuyucunun çıkış sebebi; <see cref="Completion"/> tamamlandıktan sonra anlamlıdır.</summary>
    YouTubeIngestorEnd EndReason { get; }
}

/// <summary>
/// Verilen videonun etkin bir canlı sohbeti yok: video canlı değil ya da sohbeti kapalı. Ayarda sabit
/// video bağlantısı varken görülür; yeniden denemek kendiliğinden düzeltmez (yeni yayının bağlantısı
/// farklıdır), o yüzden hata sayılıp geri çekilme büyütülmez.
/// </summary>
public sealed class YouTubeNoLiveChatException : Exception
{
    public YouTubeNoLiveChatException(string message) : base(message) { }
}
