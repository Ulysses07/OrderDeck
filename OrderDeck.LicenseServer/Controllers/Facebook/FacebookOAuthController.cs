using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Services.Facebook;
using OrderDeck.LicenseServer.Services.Instagram;

namespace OrderDeck.LicenseServer.Controllers.Facebook;

/// <summary>
/// Masaüstü uygulamasının Facebook OAuth'unun sunucu ayağı.
///
/// <para><b>Neden var:</b> App Secret eskiden binary'ye derleniyordu. Bir
/// yandan her kurulumdan çıkarılabiliyordu, bir yandan da yayın hattına
/// enjeksiyon adımı hiç eklenmediği için sahadaki kurulumlarda BOŞ gidiyordu
/// ve Facebook'a bağlanma "App ID / Secret missing" ile patlıyordu. Takas
/// buraya alınınca sır sunucuda kaldı ve istemcinin taşıyacağı bir sır
/// kalmadı.</para>
///
/// <para><b>Yetki:</b> lisans kimliğine bağlı değil — takas hiçbir tenant
/// verisine dokunmuyor. Gereken tek şey isteğin lisanslı bir müşteriden
/// gelmesi; aksi hâlde uç, herkese açık ücretsiz bir OAuth vekiline
/// dönüşürdü.</para>
///
/// <para><b>Token saklama:</b> varsayılan olarak takas token'ı SAKLAMAZ;
/// masaüstü DPAPI ile kendi diskinde tutar. Tek istisna: müşterinin
/// <c>IntakeFormConfig.InstagramDmBotEnabled</c> bayrağı açıksa
/// <see cref="InstagramAccountService.TryConnectAsync"/> opt-in olarak
/// Page token'ını şifreli saklar ve webhook aboneliği kurar. Bayrak kapalı
/// müşterilerde davranış değişmez.</para>
/// </summary>
[ApiController]
[Route("api/v1/facebook/oauth")]
[Authorize(AuthenticationSchemes = "Bearer-Customer")]
public sealed class FacebookOAuthController : ControllerBase
{
    private readonly FacebookOptions _opt;
    private readonly IFacebookOAuthExchanger _exchanger;
    private readonly InstagramAccountService _igAccounts;
    private readonly ILogger<FacebookOAuthController> _log;

    public FacebookOAuthController(
        IOptions<FacebookOptions> opt,
        IFacebookOAuthExchanger exchanger,
        InstagramAccountService igAccounts,
        ILogger<FacebookOAuthController> log)
    {
        _opt = opt.Value;
        _exchanger = exchanger;
        _igAccounts = igAccounts;
        _log = log;
    }

    /// <summary>İstemcinin yetkilendirme URL'ini kurabilmesi için gereken
    /// herkese açık değerler. App Secret BURADAN DÖNMEZ.</summary>
    public sealed record ConfigDto(
        string AppId, string LoginConfigId, string RedirectUri, string GraphApiVersion);

    [HttpGet("config")]
    public IActionResult GetConfig()
    {
        if (!_opt.IsConfigured) return NotConfigured();

        return Ok(new ConfigDto(
            _opt.AppId, _opt.LoginConfigId, _opt.RedirectUri, _opt.GraphApiVersion));
    }

    public sealed record ExchangeRequest(string Code);

    /// <summary>Uzun ömürlü kullanıcı token'ı. Sunucu bunu varsayılan olarak
    /// SAKLAMAZ; masaüstü DPAPI ile kendi diskinde tutar. Yalnız opt-in IG DM
    /// botu etkin müşterilerde Page token şifreli olarak saklanır
    /// (bkz. <see cref="InstagramAccountService"/>).</summary>
    public sealed record ExchangeResponse(string AccessToken, long ExpiresInSeconds);

    [HttpPost("exchange")]
    [EnableRateLimiting("facebook-oauth")]
    public async Task<IActionResult> Exchange(
        [FromBody] ExchangeRequest req, CancellationToken ct)
    {
        if (req is null || string.IsNullOrWhiteSpace(req.Code))
            return Problem(title: "missing-code", statusCode: 400);

        if (!_opt.IsConfigured) return NotConfigured();

        var result = await _exchanger.ExchangeCodeForLongLivedAsync(req.Code, ct);
        if (!result.Ok)
            return Problem(
                title: "facebook-exchange-failed",
                detail: $"{result.ErrorCode}: {result.ErrorMessage}",
                statusCode: 502);

        // IG "!kayıt → DM" botu (opt-in): bayrağı açık müşteride Page token'ı
        // sunucuya kalıcılaşır. Bayrak kapalıysa TryConnectAsync hiçbir şey
        // yapmaz — bu ucun "token saklamaz" sözü varsayılan olarak sürer.
        // Hata exchange'i DÜŞÜRMEZ: masaüstü FB bağlantısı bottan bağımsız.
        // Hangfire'a kuyruklanMAZ — token job argümanı olarak Hangfire
        // deposuna düz metin yazılırdı.
        try
        {
            await _igAccounts.TryConnectAsync(GetCustomerId(), result.Value!.AccessToken, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "IG DM botu bağlama denemesi düştü (exchange etkilenmedi).");
        }

        return Ok(new ExchangeResponse(
            result.Value!.AccessToken, result.Value.ExpiresInSeconds));
    }

    /// <summary>Sunucuda Facebook yapılandırması yoksa istemci bunu operatöre
    /// "sunucu hazır değil" diye söyleyebilmeli — sessiz başarısızlık, teşhisi
    /// saatlerce zorlaştıran türden bir hata.</summary>
    private ObjectResult NotConfigured() => Problem(
        title: "facebook-not-configured",
        detail: "Sunucuda Facebook uygulaması yapılandırılmamış.",
        statusCode: 503);

    private Guid GetCustomerId()
    {
        var sub = User.FindFirst("sub")?.Value
            ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? throw new InvalidOperationException("sub claim missing");
        return Guid.Parse(sub);
    }
}
