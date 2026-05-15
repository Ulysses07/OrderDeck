using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace OrderDeck.LicenseServer.Controllers.Licenses;

/// <summary>
/// WhatsApp şablonları sync (2026-05-15). WPF PaymentSettings (lokal AppSettings)
/// değiştiğinde PUT ile push'lar. Mobile WhatsAppTemplatesScreen GET ile okur.
///
/// Tek satır per License — upsert pattern. Server authoritative değil, sadece
/// replika. WPF tek yazar; mobile read-only.
/// </summary>
[ApiController]
[Route("api/v1/licenses/{licenseId:guid}/whatsapp-templates")]
[Authorize(AuthenticationSchemes = "Bearer-Customer")]
public sealed class LicensesWhatsAppTemplatesController : ControllerBase
{
    private readonly LicenseDbContext _db;

    public LicensesWhatsAppTemplatesController(LicenseDbContext db)
    {
        _db = db;
    }

    public sealed record TemplatesDto(
        string PaymentTemplate,
        string ShippingWonTemplate,
        DateTimeOffset UpdatedAt);

    public sealed record PutRequest(string PaymentTemplate, string ShippingWonTemplate);

    [HttpGet]
    public async Task<IActionResult> Get(Guid licenseId, CancellationToken ct)
    {
        var customerId = User.GetTenantCustomerId();
        var ownsLicense = await _db.Licenses
            .AnyAsync(l => l.Id == licenseId && l.CustomerId == customerId, ct);
        if (!ownsLicense) return NotFound();

        var row = await _db.WhatsAppTemplateSettings
            .Where(s => s.LicenseId == licenseId)
            .Select(s => new TemplatesDto(s.PaymentTemplate, s.ShippingWonTemplate, s.UpdatedAt))
            .FirstOrDefaultAsync(ct);

        if (row is null) return NoContent();   // henüz WPF push'lamadı, mobile default göstersin
        return Ok(row);
    }

    [HttpPut]
    public async Task<IActionResult> Put(
        Guid licenseId,
        [FromBody] PutRequest req,
        CancellationToken ct)
    {
        if (req is null
            || string.IsNullOrWhiteSpace(req.PaymentTemplate)
            || string.IsNullOrWhiteSpace(req.ShippingWonTemplate))
            return Problem(title: "missing-fields", statusCode: 400);

        if (req.PaymentTemplate.Length > 2000 || req.ShippingWonTemplate.Length > 2000)
            return Problem(title: "template-too-long",
                detail: "Şablonlar en fazla 2000 karakter olabilir.", statusCode: 400);

        var customerId = User.GetTenantCustomerId();
        var ownsLicense = await _db.Licenses
            .AnyAsync(l => l.Id == licenseId && l.CustomerId == customerId, ct);
        if (!ownsLicense) return NotFound();

        var now = DateTimeOffset.UtcNow;
        var existing = await _db.WhatsAppTemplateSettings
            .FirstOrDefaultAsync(s => s.LicenseId == licenseId, ct);

        if (existing is null)
        {
            _db.WhatsAppTemplateSettings.Add(new WhatsAppTemplateSettings
            {
                Id = Guid.NewGuid(),
                LicenseId = licenseId,
                PaymentTemplate = req.PaymentTemplate,
                ShippingWonTemplate = req.ShippingWonTemplate,
                UpdatedAt = now
            });
        }
        else
        {
            existing.PaymentTemplate = req.PaymentTemplate;
            existing.ShippingWonTemplate = req.ShippingWonTemplate;
            existing.UpdatedAt = now;
        }

        await _db.SaveChangesAsync(ct);

        return Ok(new TemplatesDto(req.PaymentTemplate, req.ShippingWonTemplate, now));
    }
}
