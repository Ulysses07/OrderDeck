using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using OrderDeck.LicenseServer.Services.IntakeForm;

namespace OrderDeck.LicenseServer.Controllers;

/// <summary>
/// Public (anonim) YouTube handle doğrulama — intake formundaki canlı geri bildirim.
/// İşin tamamını <see cref="IYouTubeChannelResolver"/> yapar; burada yalnız IP başına
/// rate-limit ve JSON biçimi var.
///
/// DİKKAT: Bu uç yalnız GÖSTERİM içindir. Kaydedilen channelId'yi sunucu gönderim
/// anında KENDİSİ yeniden çözecek (IntakeForm.cshtml.cs'e bir sonraki adımda
/// bağlanacak) — buradan dönen değere istemci üzerinden güvenilmez.
/// </summary>
[ApiController]
[AllowAnonymous]
public sealed class YouTubeVerifyController : ControllerBase
{
    private readonly IYouTubeChannelResolver _resolver;

    public YouTubeVerifyController(IYouTubeChannelResolver resolver) => _resolver = resolver;

    public sealed record VerifyResult(bool Available, bool Exists, string? Title, string? Thumbnail, string? ChannelId);

    [HttpGet("api/public/verify/youtube")]
    [EnableRateLimiting("youtube-verify")]
    public async Task<IActionResult> Verify([FromQuery] string? handle, CancellationToken ct)
    {
        var ch = await _resolver.ResolveHandleAsync(handle, ct);
        return Ok(new VerifyResult(ch.Available, ch.Exists, ch.Title, ch.Thumbnail, ch.ChannelId));
    }
}
