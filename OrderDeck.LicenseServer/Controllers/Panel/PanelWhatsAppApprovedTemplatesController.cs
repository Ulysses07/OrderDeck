using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Services.Auth;
using OrderDeck.LicenseServer.Services.WhatsApp;

namespace OrderDeck.LicenseServer.Controllers.Panel;

/// <summary>
/// Yayıncının Meta'da ONAYLI WhatsApp şablonları.
///
/// <para><b>Neden <c>/api/panel/whatsapp-templates</c>'ten ayrı:</b> o uç WPF'in
/// wa.me bağlantısına yazdığı serbest metin kalıplarını döndürüyor ve Meta'yla
/// hiç ilgisi yok. Bunlar ise Meta'nın onayladığı, 24 saatlik pencere kapalıyken
/// gönderilebilen tek mesaj türü. İkisini aynı ada koymak, birini değiştirenin
/// öbürünü bozması demekti.</para>
///
/// <para><b>Saklamıyoruz, her istekte Meta'ya soruyoruz.</b> Onay durumu Meta'da
/// değişiyor (onaylı şablon kaliteye göre duraklatılabiliyor) ve bizim kopyamız
/// bayatladığında yayıncı gönderemeyeceği bir şablonu gönderilebilir sanırdı.</para>
///
/// <para><b>Hesap sahibine kilitli DEĞİL:</b> personel zaten serbest metin
/// gönderebiliyor; şablon listesi mesaj içeriği, hesap yapılandırması değil.</para>
/// </summary>
[ApiController]
[Route("api/panel/whatsapp-approved-templates")]
[Authorize(AuthenticationSchemes = "Bearer-Customer")]
public sealed class PanelWhatsAppApprovedTemplatesController : ControllerBase
{
    private readonly LicenseDbContext _db;
    private readonly WhatsAppAccountService _accounts;
    private readonly IWhatsAppTemplateCatalog _catalog;

    public PanelWhatsAppApprovedTemplatesController(
        LicenseDbContext db, WhatsAppAccountService accounts, IWhatsAppTemplateCatalog catalog)
    {
        _db = db;
        _accounts = accounts;
        _catalog = catalog;
    }

    /// <summary>
    /// <paramref name="UnsupportedReason"/> null ise şablon gönderilebilir.
    /// Dolu olan da listeye giriyor — bkz. <see cref="WabaTemplate"/>.
    /// </summary>
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
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var licenseId = await PanelLicenseScope.ResolveAsync(_db, User.GetTenantCustomerId(), ct);
        if (licenseId is null) return Problem(title: "no-active-license", statusCode: 400);

        var waba = await _accounts.ResolveWabaContextAsync(licenseId.Value, ct);
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
            t.Buttons.Select(b => b.Text).ToList(), t.ParameterCount, t.ParameterExamples, t.UnsupportedReason)));
    }
}
