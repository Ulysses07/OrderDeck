using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Services.Auth;
using OrderDeck.LicenseServer.Services.WhatsApp;

namespace OrderDeck.LicenseServer.Controllers.Licenses;

/// <summary>
/// Yayıncının Meta'da ONAYLI şablonları — WPF'in ayar ekranı bunu çağırır.
///
/// <para><b>Neden panel ucundan ayrı:</b>
/// <see cref="Panel.PanelWhatsAppApprovedTemplatesController"/> lisansı
/// <c>PanelLicenseScope</c> ile ÖRTÜK seçiyor; panelde bu doğru, çünkü orada
/// oturum tek bir yayıncıyı temsil ediyor. WPF ise operatörün seçtiği lisans
/// anahtarından AÇIK bir lisans id çözüyor. Birden fazla lisansı olan
/// yayıncıda örtük seçim başka hesabın şablonlarını listelerdi; operatör
/// onlardan birini seçer ve gönderim anında Meta'dan "template does not exist"
/// alırdı — üstelik şablon seçimi ayarda saklandığı için hata her gönderimde
/// tekrarlanırdı. Gönderim ucu (<see cref="LicensesWhatsAppSendController"/>)
/// tam bu yüzden lisans kapsamlı; şablonu SEÇTİĞİMİZ uç da aynı kapsamda
/// olmak zorunda, yoksa seçimle gönderim farklı hesaplara bakar.</para>
///
/// <para><b>Saklamıyoruz, her istekte Meta'ya soruyoruz</b> — onay durumu
/// Meta'da değişiyor (onaylı şablon kaliteye göre duraklatılabiliyor) ve bayat
/// kopya yayıncıya gönderemeyeceği bir şablonu seçtirir.</para>
/// </summary>
[ApiController]
[Route("api/v1/licenses/{licenseId:guid}/whatsapp/approved-templates")]
[Authorize(AuthenticationSchemes = "Bearer-Customer")]
public sealed class LicensesWhatsAppApprovedTemplatesController : ControllerBase
{
    private readonly LicenseDbContext _db;
    private readonly WhatsAppAccountService _accounts;
    private readonly IWhatsAppTemplateCatalog _catalog;

    public LicensesWhatsAppApprovedTemplatesController(
        LicenseDbContext db, WhatsAppAccountService accounts, IWhatsAppTemplateCatalog catalog)
    {
        _db = db;
        _accounts = accounts;
        _catalog = catalog;
    }

    /// <summary><c>UnsupportedReason</c> null ise şablon gönderilebilir. Dolu
    /// olan da listeye giriyor: yayıncı Meta'da onaylattığı şablonu listede hiç
    /// göremezse eksikliği kendi hesabına yorar (bkz. <see cref="WabaTemplate"/>).</summary>
    public sealed record TemplateDto(
        string Name,
        string Language,
        string Category,
        string? HeaderText,
        string BodyText,
        string? FooterText,
        IReadOnlyList<string> Buttons,
        int ParameterCount,
        IReadOnlyList<string> ParameterExamples,
        string? UnsupportedReason);

    [HttpGet]
    public async Task<IActionResult> List(Guid licenseId, CancellationToken ct)
    {
        var customerId = User.GetTenantCustomerId();
        var ownsLicense = await _db.Licenses
            .AnyAsync(l => l.Id == licenseId && l.CustomerId == customerId, ct);
        if (!ownsLicense) return NotFound();

        var waba = await _accounts.ResolveWabaContextAsync(licenseId, ct);
        if (waba is null)
        {
            return Problem(
                title: "no-whatsapp-account", statusCode: 503,
                detail: "Bu lisansa bağlı aktif WhatsApp hesabı yok.");
        }

        var result = await _catalog.ListApprovedAsync(waba.WabaId, waba.AccessToken, ct);
        if (!result.Ok)
        {
            return Problem(
                title: "whatsapp-templates-read-failed", statusCode: 502,
                detail: string.IsNullOrWhiteSpace(result.ErrorMessage)
                    ? result.ErrorCode ?? "bilinmeyen hata"
                    : $"{result.ErrorCode}: {result.ErrorMessage}");
        }

        return Ok(result.Value!.Select(t => new TemplateDto(
            t.Name, t.Language, t.Category, t.HeaderText, t.BodyText, t.FooterText,
            t.Buttons, t.ParameterCount, t.ParameterExamples, t.UnsupportedReason)));
    }
}
