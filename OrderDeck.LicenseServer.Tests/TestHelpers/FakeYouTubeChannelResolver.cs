using OrderDeck.LicenseServer.Services.IntakeForm;

namespace OrderDeck.LicenseServer.Tests.TestHelpers;

/// <summary>
/// Sayfa testlerinde YouTube API'sinin yerine geçer. Handle bazında senaryo
/// tanımlanır; tanımsız handle "bulunamadı" sayılır.
/// </summary>
public sealed class FakeYouTubeChannelResolver : IYouTubeChannelResolver
{
    public Dictionary<string, YouTubeChannel> ByHandle { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>true ise her çağrı Available:false döner (kota/ağ arızası benzetimi).</summary>
    public bool ForceUnavailable { get; set; }

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
}
