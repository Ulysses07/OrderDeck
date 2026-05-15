using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Services.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace OrderDeck.LicenseServer.Controllers.Panel;

/// <summary>
/// Mobile Panel için WhatsApp şablonları endpoint'i. Tenant customer ID'den
/// License otomatik resolve edilir; mobile licenseId bilmek zorunda kalmaz.
///
/// Tek yayıncının birden çok lisansı varsa ilk aktif lisansın şablonu döner
/// (tipik kullanımda zaten tek lisans var).
///
/// Read-only — WPF tek yazar; mobile sadece okur. Yazma için
/// <see cref="Licenses.LicensesWhatsAppTemplatesController"/>.
/// </summary>
[ApiController]
[Route("api/panel/whatsapp-templates")]
[Authorize(AuthenticationSchemes = "Bearer-Customer")]
public sealed class PanelWhatsAppTemplatesController : ControllerBase
{
    private readonly LicenseDbContext _db;

    public PanelWhatsAppTemplatesController(LicenseDbContext db)
    {
        _db = db;
    }

    public sealed record TemplatesDto(
        string PaymentTemplate,
        string ShippingWonTemplate,
        DateTimeOffset UpdatedAt);

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var customerId = User.GetTenantCustomerId();
        var now = DateTimeOffset.UtcNow;

        // Tipik kullanım: tek lisans. Birden fazla varsa ilk aktif olanı seç.
        var licenseId = await _db.Licenses
            .Where(l => l.CustomerId == customerId
                && l.RevokedAt == null
                && l.ExpiresAt > now)
            .OrderBy(l => l.IssuedAt)
            .Select(l => (Guid?)l.Id)
            .FirstOrDefaultAsync(ct);

        if (licenseId is null) return NoContent();

        var row = await _db.WhatsAppTemplateSettings
            .Where(s => s.LicenseId == licenseId.Value)
            .Select(s => new TemplatesDto(s.PaymentTemplate, s.ShippingWonTemplate, s.UpdatedAt))
            .FirstOrDefaultAsync(ct);

        return row is null ? NoContent() : Ok(row);
    }
}
