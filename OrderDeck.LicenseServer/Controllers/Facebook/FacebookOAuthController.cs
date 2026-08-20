using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Services.Facebook;

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
/// </summary>
[ApiController]
[Route("api/v1/facebook/oauth")]
[Authorize(AuthenticationSchemes = "Bearer-Customer")]
public sealed class FacebookOAuthController : ControllerBase
{
    private readonly FacebookOptions _opt;
    private readonly IFacebookOAuthExchanger _exchanger;

    public FacebookOAuthController(
        IOptions<FacebookOptions> opt, IFacebookOAuthExchanger exchanger)
    {
        _opt = opt.Value;
        _exchanger = exchanger;
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

    /// <summary>Uzun ömürlü kullanıcı token'ı. Sunucu bunu SAKLAMAZ; masaüstü
    /// DPAPI ile kendi diskinde tutar.</summary>
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
}
