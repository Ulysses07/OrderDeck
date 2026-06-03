using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace OrderDeck.LicenseServer.Controllers;

/// <summary>
/// WPF uygulamasının "yeni sürüm var mı" kontrolü için en son sürüm bilgisini
/// servis eder. Sürüm config'den (AppRelease:LatestVersion) okunur; release
/// sonrası prod'da VPS .env'den (AppRelease__LatestVersion) bump'lanır.
/// LatestVersion boşsa null döner → client kontrolü atlar (yanlış pozitif yok).
/// </summary>
[ApiController]
[Route("api/app")]
public sealed class AppReleaseController : ControllerBase
{
    private readonly IConfiguration _config;

    public AppReleaseController(IConfiguration config) => _config = config;

    public sealed record AppVersionResponse(string? LatestVersion, string DownloadUrl);

    [AllowAnonymous]
    [HttpGet("version")]
    public IActionResult GetVersion()
    {
        var latest = _config["AppRelease:LatestVersion"];
        if (string.IsNullOrWhiteSpace(latest)) latest = null;

        var url = _config["AppRelease:DownloadUrl"];
        if (string.IsNullOrWhiteSpace(url)) url = "https://orderdeckapp.com/indir";

        return Ok(new AppVersionResponse(latest, url));
    }
}
