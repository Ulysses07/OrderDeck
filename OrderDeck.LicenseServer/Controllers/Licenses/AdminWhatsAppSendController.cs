using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Services.WhatsApp;

namespace OrderDeck.LicenseServer.Controllers.Licenses;

/// <summary>
/// Admin tarafından tetiklenen tek mesajlık WhatsApp gönderimi — bağlanan
/// hesabın gerçekten çalıştığını doğrulamak için.
///
/// <para><b>Gerçek mesaj gider ve ücretlendirilebilir</b> (template = business-initiated).
/// Panel/otomasyon akışları bu ucu kullanmaz; onlar <see cref="WhatsAppMessagingService"/>'i
/// doğrudan çağırır.</para>
///
/// <para><b>Neden iki mod:</b> serbest metin yalnız 24 saatlik service penceresi
/// açıkken (müşteri son 24 saatte yazmışsa) gidebilir. Hiç yazışma yokken tek
/// yol onaylı template — Meta'nın her hesapta hazır verdiği <c>hello_world</c>
/// bunun için birebir.</para>
/// </summary>
[ApiController]
[Route("api/v1/admin/licenses/{licenseId:guid}/whatsapp/send-test")]
[Authorize(AuthenticationSchemes = "Bearer-Admin")]
public sealed class AdminWhatsAppSendController : ControllerBase
{
    private readonly LicenseDbContext _db;
    private readonly WhatsAppMessagingService _messaging;

    public AdminWhatsAppSendController(LicenseDbContext db, WhatsAppMessagingService messaging)
    {
        _db = db;
        _messaging = messaging;
    }

    /// <summary><see cref="TemplateName"/> doluysa template, değilse serbest metin gider.</summary>
    public sealed record SendTestRequest(
        string ToPhone,
        string? Text,
        string? TemplateName,
        string? LanguageCode);

    public sealed record SendTestResponse(
        bool Ok, string? ErrorCode, string? ErrorMessage, Guid? MessageId);

    [HttpPost]
    public async Task<IActionResult> Send(
        Guid licenseId, [FromBody] SendTestRequest req, CancellationToken ct)
    {
        if (WaPhone.Canonical(req.ToPhone).Length == 0)
            return Problem(title: "invalid-phone", statusCode: 400, detail: "Geçerli bir numara gir.");

        var useTemplate = !string.IsNullOrWhiteSpace(req.TemplateName);
        if (!useTemplate && string.IsNullOrWhiteSpace(req.Text))
            return Problem(title: "empty-body", statusCode: 400, detail: "Text veya TemplateName gerekli.");

        if (!await _db.Licenses.AnyAsync(l => l.Id == licenseId, ct)) return NotFound();

        var outcome = useTemplate
            ? await _messaging.SendTemplateAsync(
                licenseId, req.ToPhone,
                new WhatsAppTemplate(
                    req.TemplateName!.Trim(),
                    string.IsNullOrWhiteSpace(req.LanguageCode) ? "en_US" : req.LanguageCode.Trim(),
                    Array.Empty<string>()),
                origin: "admin-test", ct)
            : await _messaging.SendTextAsync(licenseId, req.ToPhone, req.Text!, origin: "admin-test", ct);

        var body = new SendTestResponse(
            outcome.Ok, outcome.ErrorCode, outcome.ErrorMessage, outcome.MessageId);

        // Gönderilemedi ≠ istek hatalı: sebep gövdede taşınıyor (window_closed,
        // no_account, Meta hata kodu...). 200 dönmek çağıranın hepsini aynı
        // şekilde okumasını sağlıyor.
        return Ok(body);
    }
}
