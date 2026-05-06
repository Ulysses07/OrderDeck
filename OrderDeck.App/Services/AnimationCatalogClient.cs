using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace OrderDeck.App.Services;

public sealed record AnimationCatalogEntry(
    string Id, string Name, string Description, string Category, string Thumbnail)
{
    public string? OverlayBase { get; init; }
    public string ThumbnailUri =>
        $"{OverlayBase ?? ""}/animations/{Thumbnail}".TrimStart('/');
}

public sealed class AnimationCatalogClient
{
    private readonly HttpClient _http;
    private readonly string _manifestUrl;
    private readonly string _overlayBase;

    public AnimationCatalogClient(HttpClient http, int overlayPort)
    {
        _http = http;
        _overlayBase = $"http://localhost:{overlayPort}";
        _manifestUrl = $"{_overlayBase}/animations/manifest.json";
    }

    public async Task<IReadOnlyList<AnimationCatalogEntry>> LoadAsync()
    {
        var json = await _http.GetStringAsync(_manifestUrl);
        var doc = JsonDocument.Parse(json);
        var list = new List<AnimationCatalogEntry>();
        foreach (var el in doc.RootElement.GetProperty("animations").EnumerateArray())
        {
            list.Add(new AnimationCatalogEntry(
                el.GetProperty("id").GetString() ?? "",
                el.GetProperty("name").GetString() ?? "",
                el.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "",
                el.TryGetProperty("category", out var c) ? c.GetString() ?? "" : "",
                el.TryGetProperty("thumbnail", out var t) ? t.GetString() ?? "" : "")
            { OverlayBase = _overlayBase });
        }
        return list;
    }
}
