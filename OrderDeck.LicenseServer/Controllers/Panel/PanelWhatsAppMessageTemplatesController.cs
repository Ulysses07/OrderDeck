using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Services.Auth;
using OrderDeck.LicenseServer.Services.WhatsApp;

namespace OrderDeck.LicenseServer.Controllers.Panel;

/// <summary>
/// Yayıncının WhatsApp şablonlarını panelden yönetmesi.
///
/// <para><b>Onaylı listeden neden ayrı:</b> <c>whatsapp-approved-templates</c>
/// gönderim listesi — yalnız gönderilebilir olanı döndürüyor ve sözleşmesi
/// gönderim ekranına bağlı. Burası yönetim: onay bekleyeni ve reddedileni de
/// göstermek zorunda. İkisini birleştirmek, gönderim ekranını gönderilemez
/// şablonlarla doldurmak demekti.</para>
///
/// <para><b>Sahiplik neden elle doğrulanıyor:</b> Meta'nın düzenleme ucu
/// <c>POST /{TEMPLATE_ID}</c>, silme ucu da <c>hsm_id</c> alıyor — ikisi de
/// WABA kapsamlı DEĞİL. Kimliği doğrudan geçirseydik bir yayıncı, kimliğini
/// bildiği başka bir yayıncının şablonunu düzenleyebilir ya da silebilirdi.</para>
///
/// <para><b><c>[AllowStockStaff]</c> bilerek yok:</b> şablon oluşturmak marka
/// adına mesaj yazmak demek ve reddedilen şablon WABA'nın kalite notunu
/// düşürüyor. Stok elemanı bu bölümden dışarıda kalıyor.</para>
/// </summary>
[ApiController]
[Route("api/panel/whatsapp-message-templates")]
[Authorize(AuthenticationSchemes = "Bearer-Customer")]
public sealed class PanelWhatsAppMessageTemplatesController : ControllerBase
{
    private readonly LicenseDbContext _db;
    private readonly WhatsAppAccountService _accounts;
    private readonly IWhatsAppTemplateCatalog _catalog;

    public PanelWhatsAppMessageTemplatesController(
        LicenseDbContext db, WhatsAppAccountService accounts, IWhatsAppTemplateCatalog catalog)
    {
        _db = db;
        _accounts = accounts;
        _catalog = catalog;
    }

    public sealed record ButtonDto(string Type, string Text, string? Url, string? PhoneNumber);

    public sealed record TemplateDto(
        string Id,
        string Name,
        string Language,
        string Category,
        string Status,
        string? RejectedReason,
        string? HeaderText,
        string BodyText,
        string? FooterText,
        IReadOnlyList<ButtonDto> Buttons,
        IReadOnlyList<string> BodyExamples,
        string? UnsupportedReason);

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var (scope, error) = await ResolveScopeAsync(ct);
        if (error is not null) return error;

        var result = await _catalog.ListAllAsync(scope!.WabaId, scope.AccessToken, ct);
        if (!result.Ok) return GraphProblem("whatsapp-templates-read-failed", result);

        return Ok(result.Value!.Select(ToDto));
    }

    public sealed record ButtonRequest(string? Type, string? Text, string? Url, string? PhoneNumber);

    /// <summary>Şablonun bileşenleri — oluşturmada ve düzenlemede ortak.
    /// Ad/kategori/dil burada YOK: Meta'nın düzenleme ucu üçünü de değiştiremiyor,
    /// panel de üçünü her durumda kilitliyor.</summary>
    public sealed record DraftRequest(
        string? HeaderText,
        string? BodyText,
        string? FooterText,
        List<string>? BodyExamples,
        List<ButtonRequest>? Buttons);

    public sealed record CreateRequest(string? Name, string? Category, DraftRequest? Draft);

    public sealed record CreatedDto(string Id, string Status);

    /// <summary>Şablon dili sabit. Panelin tek dili var; yayıncıya seçtirmek,
    /// gönderim tarafında dili tutmayan şablonlar üretmek olurdu.</summary>
    private const string Language = "tr";

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateRequest req, CancellationToken ct)
    {
        var name = (req.Name ?? "").Trim();
        var category = (req.Category ?? "").Trim().ToUpperInvariant();

        var invalid = WhatsAppTemplateShape.ValidateName(name)
                      ?? WhatsAppTemplateShape.ValidateCategory(category);
        if (invalid is not null) return Invalid(invalid);

        var draft = ToDraft(req.Draft);
        var draftError = WhatsAppTemplateShape.Validate(draft);
        if (draftError is not null) return Invalid(draftError);

        var (scope, error) = await ResolveScopeAsync(ct);
        if (error is not null) return error;

        var result = await _catalog.CreateAsync(
            scope!.WabaId, scope.AccessToken, name, category, Language, draft, ct);
        if (!result.Ok) return GraphProblem("whatsapp-template-create-failed", result);

        return Ok(new CreatedDto(result.Value!.Id, result.Value!.Status));
    }

    /// <summary>Doğrulama hatası 400 + Türkçe metin. Panel bu metni olduğu gibi
    /// gösteriyor; Meta'nın kendi hata metni yayıncıya hiçbir şey anlatmıyor.</summary>
    private IActionResult Invalid(string message) =>
        Problem(title: "invalid-template", statusCode: 400, detail: message);

    private static WhatsAppTemplateDraft ToDraft(DraftRequest? r)
    {
        r ??= new DraftRequest(null, null, null, null, null);

        return new WhatsAppTemplateDraft(
            Clean(r.HeaderText),
            (r.BodyText ?? "").Trim(),
            Clean(r.FooterText),
            (r.BodyExamples ?? []).Select(e => (e ?? "").Trim()).ToList(),
            (r.Buttons ?? []).Select(b => new WhatsAppTemplateButton(
                (b.Type ?? "").Trim().ToUpperInvariant(),
                (b.Text ?? "").Trim(),
                Clean(b.Url),
                Clean(b.PhoneNumber))).ToList());
    }

    /// <summary>Boş dizgeyi null'a indiriyor: panel dokunulmamış alanı boş dizge
    /// olarak yolluyor ve boş bir HEADER bileşeni Meta'dan ret getirirdi.</summary>
    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static TemplateDto ToDto(WabaTemplate t) => new(
        t.Id, t.Name, t.Language, t.Category, t.Status, t.RejectedReason,
        t.HeaderText, t.BodyText, t.FooterText,
        t.Buttons.Select(b => new ButtonDto(b.Type, b.Text, b.Url, b.PhoneNumber)).ToList(),
        t.ParameterExamples, t.UnsupportedReason);

    private sealed record WabaScope(string WabaId, string AccessToken);

    private async Task<(WabaScope? Scope, IActionResult? Error)> ResolveScopeAsync(CancellationToken ct)
    {
        var licenseId = await PanelLicenseScope.ResolveAsync(_db, User.GetTenantCustomerId(), ct);
        if (licenseId is null) return (null, Problem(title: "no-active-license", statusCode: 400));

        var waba = await _accounts.ResolveWabaContextAsync(licenseId.Value, ct);
        if (waba is null)
        {
            return (null, Problem(
                title: "no-whatsapp-account", statusCode: 503,
                detail: "Bu lisansa bağlı aktif WhatsApp hesabı yok."));
        }

        return (new WabaScope(waba.WabaId, waba.AccessToken), null);
    }

    /// <summary>Meta hatası 502 ile geçiyor: sorun bizde değil, yukarı akışta.
    /// Kodu ve metni gövdeye yazıyoruz — "bir hata oluştu" diyen bir panel,
    /// yayıncıyı bize yazmaktan başka bir yere götürmüyor.</summary>
    private IActionResult GraphProblem<T>(string title, GraphResult<T> result) =>
        Problem(
            title: title, statusCode: 502,
            detail: string.IsNullOrWhiteSpace(result.ErrorMessage)
                ? result.ErrorCode ?? "bilinmeyen hata"
                : $"{result.ErrorCode}: {result.ErrorMessage}");
}
