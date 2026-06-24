using System.Text.Json.Serialization;

namespace OrderDeck.Core.Chat;

/// <summary>
/// YouTube canlı sohbet çekme yöntemi (feature-flag). Resmi API ingestor tam
/// doğrulanana kadar varsayılan <see cref="Scraper"/> kalır; geçiş tek ayarla.
/// settings.json'da string olarak yazılır ("Scraper"/"OfficialApi"); int de kabul edilir.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum YouTubeIngestMode
{
    /// <summary>Mevcut InnerTube <c>get_live_chat</c> scraper — kotasız,
    /// tarayıcı gerektirmez. Varsayılan.</summary>
    Scraper = 0,

    /// <summary>Resmi gRPC <c>liveChatMessages.streamList</c> (push). Eksiksiz +
    /// stabil ama günlük kota tüketir ve <c>AppSettings.YouTubeApiKey</c> ister.</summary>
    OfficialApi = 1,
}
