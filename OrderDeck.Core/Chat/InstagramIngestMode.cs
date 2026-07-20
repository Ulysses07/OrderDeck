using System.Text.Json.Serialization;

namespace OrderDeck.Core.Chat;

/// <summary>
/// Instagram canlı yorum çekme yöntemi (feature-flag). Resmi Graph API ingestor
/// tam doğrulanana kadar varsayılan <see cref="Scraper"/> kalır; geçiş tek ayarla.
/// settings.json'da string olarak yazılır ("Scraper"/"OfficialApi"); int de kabul edilir.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum InstagramIngestMode
{
    /// <summary>Chrome extension DOM scraper (bridge üzerinden). Kotasız ama
    /// kırılgan ve IG ToS'a aykırı. Varsayılan.</summary>
    Scraper = 0,

    /// <summary>Resmi Instagram Graph API (<c>live_media</c> → <c>{media}/comments</c>
    /// polling). Read-only (moderasyon yok) ama stabil ve ToS-uyumlu. Bağlı FB
    /// Page token'ını kullanır (ayrı OAuth gerekmez).</summary>
    OfficialApi = 1,
}
