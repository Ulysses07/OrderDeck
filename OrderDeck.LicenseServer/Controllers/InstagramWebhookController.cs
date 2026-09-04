using System.IO;
using System.Text;
using Hangfire;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Services.Facebook;
using OrderDeck.LicenseServer.Services.Instagram;
using OrderDeck.LicenseServer.Services.WhatsApp;

namespace OrderDeck.LicenseServer.Controllers;

/// <summary>
/// Meta Instagram live_comments webhook uç noktası.
///
/// <para><b>Karanlık yayın:</b> <c>InstagramDm__Enabled</c> false iken (veya
/// eksikken) her iki metod 404 döner — IntakeLogin deseni. Özellik App Review
/// onaylanana kadar kapalı kalır.</para>
///
/// <para><b>İmza:</b> WhatsApp'tan ayrı app yok; webhook masaüstü Facebook
/// app'ine bağlı, dolayısıyla imza doğrulaması <c>FacebookOptions.AppSecret</c>
/// ile yapılır. <see cref="WhatsAppSignatureValidator"/> genel amaçlı — adı
/// yanıltmasın.</para>
///
/// <para><b>Hızlı 200:</b> Meta ~5 sn içinde 200 bekler. İş Hangfire'a
/// kuyruklanır, controller hemen döner.</para>
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("api/v1/instagram/webhook")]
public sealed class InstagramWebhookController : ControllerBase
{
    /// <summary>Meta tek seferde büyük gövde göndermez; kötü niyetli isteğe karşı üst sınır.</summary>
    private const int MaxBodyBytes = 1024 * 1024;

    private readonly InstagramDmOptions _opt;
    private readonly FacebookOptions _fb;
    private readonly IBackgroundJobClient _jobs;
    private readonly ILogger<InstagramWebhookController> _log;

    public InstagramWebhookController(
        IOptions<InstagramDmOptions> opt,
        IOptions<FacebookOptions> fb,
        IBackgroundJobClient jobs,
        ILogger<InstagramWebhookController> log)
    {
        _opt = opt.Value;
        _fb = fb.Value;
        _jobs = jobs;
        _log = log;
    }

    /// <summary>
    /// Meta'nın abonelik doğrulaması: <c>hub.verify_token</c> bizimkiyle eşleşirse
    /// <c>hub.challenge</c> düz metin olarak aynen geri döner.
    /// </summary>
    [HttpGet]
    public IActionResult Verify(
        [FromQuery(Name = "hub.mode")] string? mode,
        [FromQuery(Name = "hub.verify_token")] string? verifyToken,
        [FromQuery(Name = "hub.challenge")] string? challenge)
    {
        if (!_opt.Ready) return NotFound();

        if (mode != "subscribe" || !FixedTimeEquals(verifyToken, _opt.VerifyToken))
        {
            _log.LogWarning("Instagram webhook doğrulaması reddedildi (mode={Mode}).", mode);
            return StatusCode(StatusCodes.Status403Forbidden);
        }

        return Content(challenge ?? string.Empty, "text/plain");
    }

    /// <summary>Olay bildirimi. İmza doğrulanır, iş kuyruklanır, hemen 200 dönülür.</summary>
    [HttpPost]
    public async Task<IActionResult> Receive(CancellationToken ct)
    {
        if (!_opt.Ready) return NotFound();

        // ContentLength chunked isteklerde null olur; null'ı sınır aşımı say
        // (Kestrel'in 30 MB tavanına güvenip 1 MB sınırını deldirmeyelim).
        if ((Request.ContentLength ?? long.MaxValue) > MaxBodyBytes)
            return StatusCode(StatusCodes.Status413PayloadTooLarge);

        string rawBody;
        using (var reader = new StreamReader(Request.Body, Encoding.UTF8))
        {
            rawBody = await reader.ReadToEndAsync(ct);
        }

        var signature = Request.Headers["X-Hub-Signature-256"].ToString();
        if (!WhatsAppSignatureValidator.IsValid(signature, rawBody, _fb.AppSecret))
        {
            _log.LogWarning("Instagram webhook imzası geçersiz — istek reddedildi.");
            return StatusCode(StatusCodes.Status403Forbidden);
        }

        if (rawBody.Length > 0)
            _jobs.Enqueue<InstagramLiveCommentJob>(j => j.ProcessAsync(rawBody, CancellationToken.None));

        return Ok();
    }

    /// <summary>Verify token karşılaştırması sabit zamanlı — token tahminini zorlaştırır.</summary>
    private static bool FixedTimeEquals(string? a, string b)
    {
        if (a is null) return false;
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
    }
}
