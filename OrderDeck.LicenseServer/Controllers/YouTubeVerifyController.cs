using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using OrderDeck.LicenseServer.Services.IntakeForm;

namespace OrderDeck.LicenseServer.Controllers;

/// <summary>
/// Public (anonim) YouTube kanal doğrulama — intake formundaki canlı geri bildirim.
/// İki giriş: <c>handle</c> (@ ile başlayan kullanıcı adı) ya da <c>channelId</c>
/// (<c>youtube.com/channel/UC…</c> adresinden çıkarılmış kimlik). İşin tamamını
/// <see cref="IYouTubeChannelResolver"/> yapar; burada yalnız IP başına rate-limit
/// ve JSON biçimi var.
///
/// DİKKAT: Bu uç yalnız GÖSTERİM içindir. Sunucu gönderim anında kanalı
/// IntakeForm.cshtml.cs içinde KENDİSİ yeniden çözüyor — buradan dönen değere
/// istemci üzerinden güvenilmez.
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
    public async Task<IActionResult> Verify(
        [FromQuery] string? handle, [FromQuery] string? channelId, CancellationToken ct)
    {
        // channelId doluysa o yol kazanır: kanal adresi yapıştıran müşteride
        // alan tam adresi taşıyor, handle olarak sorulsa hiçbir kanala denk gelmez.
        var ch = !string.IsNullOrWhiteSpace(channelId)
            ? await _resolver.ResolveChannelIdAsync(channelId, ct)
            : await _resolver.ResolveHandleAsync(handle, ct);
        return Ok(new VerifyResult(ch.Available, ch.Exists, ch.Title, ch.Thumbnail, ch.ChannelId));
    }
}
