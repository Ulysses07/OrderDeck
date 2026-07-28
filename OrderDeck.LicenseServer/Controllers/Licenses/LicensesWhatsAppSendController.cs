using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Services.Auth;
using OrderDeck.LicenseServer.Services.WhatsApp;

namespace OrderDeck.LicenseServer.Controllers.Licenses;

/// <summary>
/// Yayıncının kendi lisansından tek WhatsApp mesajı göndermesi — WPF'in ödeme
/// hatırlatma akışı bunu çağırır.
///
/// <para><b>Neden admin ucundan ayrı:</b> <see cref="AdminWhatsAppSendController"/>
/// <c>Bearer-Admin</c> ile herhangi bir lisansa gönderir; WPF <c>Bearer-Customer</c>
/// taşır ve yalnız kendi lisansına gönderebilmelidir. Sahiplik kontrolü burada,
/// tek yerde.</para>
///
/// <para><b>Yalnız serbest metin.</b> 24 saatlik pencere kapalıysa Graph'a
/// çıkılmaz, gövdede <c>window_closed</c> döner ve WPF eski <c>wa.me</c>
/// davranışına düşer. Onaylı şablon gönderimi (pencereden bağımsız) otomatik
/// hatırlatma merdiveniyle birlikte gelecek.</para>
/// </summary>
[ApiController]
[Route("api/v1/licenses/{licenseId:guid}/whatsapp/send")]
[Authorize(AuthenticationSchemes = "Bearer-Customer")]
public sealed class LicensesWhatsAppSendController : ControllerBase
{
    /// <summary>WhatsApp metin gövdesi 4096 karakter; daha uzun istek Graph'a
    /// çıkmadan burada kesilir.</summary>
    private const int MaxTextLength = 4096;

    private readonly LicenseDbContext _db;
    private readonly WhatsAppMessagingService _messaging;

    public LicensesWhatsAppSendController(LicenseDbContext db, WhatsAppMessagingService messaging)
    {
        _db = db;
        _messaging = messaging;
    }

    public sealed record SendRequest(string ToPhone, string Text, string? Origin);

    public sealed record SendResponse(
        bool Ok, string? ErrorCode, string? ErrorMessage, Guid? MessageId);

    [HttpPost]
    public async Task<IActionResult> Send(
        Guid licenseId, [FromBody] SendRequest req, CancellationToken ct)
    {
        var toPhone = req?.ToPhone ?? "";
        if (req is null || WaPhone.Canonical(toPhone).Length == 0)
            return Problem(title: "invalid-phone", statusCode: 400, detail: "Geçerli bir numara gerekli.");
        if (string.IsNullOrWhiteSpace(req.Text))
            return Problem(title: "empty-body", statusCode: 400, detail: "Mesaj boş olamaz.");
        if (req.Text.Length > MaxTextLength)
            return Problem(title: "text-too-long", statusCode: 400,
                detail: $"Mesaj en fazla {MaxTextLength} karakter olabilir.");

        var customerId = User.GetTenantCustomerId();
        var ownsLicense = await _db.Licenses
            .AnyAsync(l => l.Id == licenseId && l.CustomerId == customerId, ct);
        if (!ownsLicense) return NotFound();

        var origin = string.IsNullOrWhiteSpace(req.Origin) ? "wpf" : req.Origin.Trim();
        if (origin.Length > 32) origin = origin[..32];

        var outcome = await _messaging.SendTextAsync(licenseId, toPhone, req.Text, origin, ct);

        // Gönderilemedi ≠ istek hatalı: sebep (window_closed / no_account / Meta
        // hata kodu) gövdede taşınır. WPF wa.me'ye düşme kararını buna bakarak
        // verdiği için tek bir okuma yolu olması şart.
        return Ok(new SendResponse(
            outcome.Ok, outcome.ErrorCode, outcome.ErrorMessage, outcome.MessageId));
    }
}
