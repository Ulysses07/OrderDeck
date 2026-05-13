namespace OrderDeck.LicenseServer.Domain;

/// <summary>
/// Yayın oturumu (WPF lokalde "StreamSession"). Mobile Panel "Siparişler"
/// ekranında yayın bazlı sipariş listesi için server'a replica edilir.
///
/// WPF authoritative — kararlar (yayın başlat/bitir) WPF'te verilir, server
/// pasif replika. Tenant izolasyonu LicenseId üzerinden.
/// </summary>
public sealed class StreamSession
{
    public Guid Id { get; set; }
    public Guid LicenseId { get; set; }
    public License License { get; set; } = null!;

    public string? Title { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }

    /// <summary>Comma-separated platform list: "instagram,youtube,tiktok"</summary>
    public string Platforms { get; set; } = "";

    public string? Notes { get; set; }

    /// <summary>Server-side last update — reverse-sync cursor için.</summary>
    public DateTimeOffset UpdatedAt { get; set; }
}
