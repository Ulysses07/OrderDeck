using System.IO;
using System.Text;
using Hangfire;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Services.WhatsApp;

namespace OrderDeck.LicenseServer.Controllers;

/// <summary>
/// Meta WhatsApp Cloud API webhook uç noktası. <b>Tüm tenant'lar için tek adres</b> —
/// hangi yayıncıya ait olduğu gövdedeki <c>metadata.phone_number_id</c> ile bulunur.
///
/// <para><b>Anonim ama korumalı:</b> Meta JWT gönderemez, o yüzden
/// <c>[AllowAnonymous]</c>. Kimlik doğrulama yerine POST'ta
/// <c>X-Hub-Signature-256</c> (HMAC-SHA256, App Secret) doğrulanır; imza tutmazsa
/// 403. GET'te Meta'nın verdiği <c>hub.verify_token</c> bizimkiyle karşılaştırılır.</para>
///
/// <para><b>Hızlı 200:</b> Meta ~5 sn içinde 200 bekler, aksi halde olayı tekrar
/// gönderir ve ısrarlı başarısızlıkta aboneliği devre dışı bırakabilir. Bu yüzden
/// ayrıştırma/DB işi Hangfire'a kuyruklanır, controller hemen 200 döner. İş
/// kuyrukta patlarsa Meta değil Hangfire retry eder.</para>
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("api/v1/whatsapp/webhook")]
public sealed class WhatsAppWebhookController : ControllerBase
{
    /// <summary>Meta tek seferde büyük gövde göndermez; kötü niyetli isteğe karşı üst sınır.</summary>
    private const int MaxBodyBytes = 1024 * 1024;

    private readonly WhatsAppOptions _opt;
    private readonly IBackgroundJobClient _jobs;
    private readonly ILogger<WhatsAppWebhookController> _log;

    public WhatsAppWebhookController(
        IOptions<WhatsAppOptions> opt, IBackgroundJobClient jobs, ILogger<WhatsAppWebhookController> log)
    {
        _opt = opt.Value;
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
        if (string.IsNullOrWhiteSpace(_opt.VerifyToken))
        {
            _log.LogWarning("WhatsApp webhook doğrulaması istendi ama VerifyToken yapılandırılmamış.");
            return Forbid();
        }

        if (mode != "subscribe" || !FixedTimeEquals(verifyToken, _opt.VerifyToken))
        {
            _log.LogWarning("WhatsApp webhook doğrulaması reddedildi (mode={Mode}).", mode);
            return Forbid();
        }

        return Content(challenge ?? string.Empty, "text/plain");
    }

    /// <summary>Olay bildirimi. İmza doğrulanır, iş kuyruklanır, hemen 200 dönülür.</summary>
    [HttpPost]
    public async Task<IActionResult> Receive(CancellationToken ct)
    {
        if (Request.ContentLength > MaxBodyBytes) return StatusCode(StatusCodes.Status413PayloadTooLarge);

        string rawBody;
        using (var reader = new StreamReader(Request.Body, Encoding.UTF8))
        {
            rawBody = await reader.ReadToEndAsync(ct);
        }

        var signature = Request.Headers["X-Hub-Signature-256"].ToString();
        if (!WhatsAppSignatureValidator.IsValid(signature, rawBody, _opt.AppSecret))
        {
            _log.LogWarning("WhatsApp webhook imzası geçersiz — istek reddedildi.");
            return Forbid();
        }

        if (rawBody.Length > 0)
            _jobs.Enqueue<WhatsAppInboundJob>(j => j.ProcessAsync(rawBody, CancellationToken.None));

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
