namespace OrderDeck.Licensing.Api.Models;

/// <summary>
/// Server /api/app/version yanıtı. LatestVersion null/boş ise güncelleme
/// kontrolü atlanır (yanlış pozitif yok).
/// </summary>
public sealed record AppVersionInfo(string? LatestVersion, string DownloadUrl);
