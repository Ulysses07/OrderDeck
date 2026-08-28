using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace OrderDeck.LicenseServer.Controllers;

/// <summary>
/// Panelin CSP ihlallerini toplayan uç (denetim maddesi O-15).
///
/// Neden var: panelin üretim CSP'si Caddy'de tanımlı ve elle test edilmesi
/// gereken iki akış kaldı (WhatsApp Embedded Signup, medya yükleme). Elle bir
/// kez tıklamak borcu kapatmaz — Meta SDK'sı ya da R2 kalıbı değiştiğinde aynı
/// borç geri gelir, üstelik kırılma SESSİZ olur: CSP ihlalleri tarayıcı
/// konsoluna değil ayrı bir kanala düşer, deploy'un smoke testi de yalnız
/// durum koduna bakar. Bu uç, kırılmayı sahadaki gerçek tarayıcıdan bildirir.
///
/// Anonim olmak ZORUNDA: ihlal giriş ekranında, henüz token yokken de olabilir.
/// Bu yüzden <c>Controllers.Panel</c> ad alanında DEĞİL — oradaki convention
/// testi her controller'a "Bearer-Customer" şeması dayatıyor (haklı olarak).
///
/// Kötüye kullanım yüzeyi bilerek dar: gövde 8 KB'de kesiliyor, alanlar model
/// düzeyinde kırpılıyor, IP başına saatlik limit var ve hiçbir şey veritabanına
/// yazılmıyor — tek etki bir günlük satırı.
/// </summary>
[ApiController]
[AllowAnonymous]
public sealed class CspReportController : ControllerBase
{
    private readonly ILogger<CspReportController> _log;

    public CspReportController(ILogger<CspReportController> log) => _log = log;

    /// <summary>
    /// Tarayıcının <c>securitypolicyviolation</c> olayından süzülmüş alanlar.
    /// İstemci sorgu dizesini zaten atıyor ve 300'de kırpıyor; buradaki
    /// <c>MaxLength</c>'ler ona güvenmemek için — uç anonim, gövdeyi isteyen
    /// herkes yollayabilir. Tavan bilerek istemcininkinden yüksek: eşit olsaydı
    /// kodlama farkından doğan tek karakter meşru bir bildirimi 400'e düşürürdü.
    ///
    /// <c>sample</c> ve <c>originalPolicy</c> BİLEREK alınmıyor: ilki engellenen
    /// script/stil içeriğinin bir parçasını taşıyabiliyor, ikincisi zaten
    /// bizde olan politikanın kopyası. İkisi de günlüğe yazılacak şey değil.
    /// </summary>
    public sealed record CspReportRequest(
        [MaxLength(512)] string? DocumentUri,
        [MaxLength(512)] string? BlockedUri,
        [MaxLength(64)] string? EffectiveDirective,
        [MaxLength(512)] string? SourceFile,
        int? LineNumber,
        [MaxLength(32)] string? Disposition);

    [HttpPost("api/public/csp-report")]
    [EnableRateLimiting("csp-report")]
    [RequestSizeLimit(8 * 1024)]
    public IActionResult Report([FromBody] CspReportRequest req)
    {
        // Yönerge yoksa elimizde işe yarar hiçbir şey yok; sessizce yut ki
        // bozuk/otomatik gövdeler günlüğü kirletmesin.
        if (string.IsNullOrWhiteSpace(req.EffectiveDirective))
            return NoContent();

        _log.LogWarning(
            "Panel CSP ihlali: yonerge={Directive} engellenen={BlockedUri} " +
            "sayfa={DocumentUri} kaynak={SourceFile}:{LineNumber} tur={Disposition}",
            req.EffectiveDirective,
            req.BlockedUri,
            req.DocumentUri,
            req.SourceFile,
            req.LineNumber,
            req.Disposition);

        return NoContent();
    }
}
