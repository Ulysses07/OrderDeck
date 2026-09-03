using OrderDeck.LicenseServer.Services.IntakeForm;

namespace OrderDeck.LicenseServer.Tests.TestHelpers;

/// <summary>
/// Sayfa testlerinde YouTube API'sinin yerine geçer. Handle ya da kanal kimliği
/// bazında senaryo tanımlanır; tanımsız girdi "bulunamadı" sayılır.
/// </summary>
public sealed class FakeYouTubeChannelResolver : IYouTubeChannelResolver
{
    public Dictionary<string, YouTubeChannel> ByHandle { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Kanal kimliği senaryoları. <see cref="StringComparer.Ordinal"/> bilerek:
    /// kanal kimlikleri büyük/küçük harf DUYARLI, gerçek API "ucabc…"yi bulmaz.
    /// </summary>
    public Dictionary<string, YouTubeChannel> ById { get; } = new(StringComparer.Ordinal);

    /// <summary>true ise her çağrı Available:false döner (kota/ağ arızası benzetimi).</summary>
    public bool ForceUnavailable { get; set; }

    /// <summary>
    /// Yapılan çağrılar. Kanal kimliği çağrıları "id:" önekiyle kaydedilir ki
    /// testler yanlış metodun çağrıldığını yakalayabilsin.
    /// </summary>
    public List<string> Calls { get; } = [];

    public Task<YouTubeChannel> ResolveHandleAsync(string? handle, CancellationToken ct)
    {
        var h = (handle ?? "").Trim().TrimStart('@').Trim();
        Calls.Add(h);

        if (ForceUnavailable)
            return Task.FromResult(new YouTubeChannel(false, false, null, null, null));

        return Task.FromResult(ByHandle.TryGetValue(h, out var ch)
            ? ch
            : new YouTubeChannel(true, false, null, null, null));
    }

    public Task<YouTubeChannel> ResolveChannelIdAsync(string? channelId, CancellationToken ct)
    {
        // TrimStart('@') ve küçük harfe indirme YOK — kanal kimliği harf duyarlı.
        var id = (channelId ?? "").Trim();
        Calls.Add("id:" + id);

        if (ForceUnavailable)
            return Task.FromResult(new YouTubeChannel(false, false, null, null, null));

        return Task.FromResult(ById.TryGetValue(id, out var ch)
            ? ch
            : new YouTubeChannel(true, false, null, null, null));
    }
}
