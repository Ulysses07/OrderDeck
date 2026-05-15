using System.Security.Claims;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Services.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace OrderDeck.LicenseServer.Controllers.Panel;

/// <summary>
/// Mobile Panel global arama. Tek query üç kategoriye bakar:
///   - Müşteriler (Order.Username + Order.DisplayName + Order.CustomerId distinct)
///   - Sipariş kodu (Order.Code)
///   - Dekont referans no (Payment.ReferansNo)
///
/// Her kategori en fazla 10 sonuç. Toplam &lt;=30 satır → mobile UI'da tek
/// liste, kategori başlığıyla gruplanır.
///
/// Sorgu min 2 char — daha kısa input early-out. Case-insensitive contains.
/// Tenant izolasyonu: auth customer'ın License.Id seti üzerinden.
/// </summary>
[ApiController]
[Route("api/panel/search")]
[Authorize(AuthenticationSchemes = "Bearer-Customer")]
public sealed class PanelSearchController : ControllerBase
{
    private readonly LicenseDbContext _db;

    public PanelSearchController(LicenseDbContext db)
    {
        _db = db;
    }

    public sealed record SearchResultDto(
        List<CustomerHitDto> Customers,
        List<OrderHitDto> Orders,
        List<PaymentHitDto> Payments);

    public sealed record CustomerHitDto(
        string CustomerId,
        string Username,
        string? DisplayName,
        string Platform);

    public sealed record OrderHitDto(
        Guid Id,
        Guid? SessionId,
        string? Code,
        string MessageText,
        string Username,
        string? DisplayName,
        decimal Price,
        DateTimeOffset AddedAt);

    public sealed record PaymentHitDto(
        Guid Id,
        string ReferansNo,
        string PayerName,
        decimal Amount,
        string Status,
        DateTimeOffset CreatedAt);

    [HttpGet]
    public async Task<IActionResult> Search([FromQuery] string? q, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(q) || q.Trim().Length < 2)
            return Ok(new SearchResultDto(new(), new(), new()));

        var term = q.Trim();
        var customerId = User.GetTenantCustomerId();
        var licenseIds = await _db.Licenses
            .Where(l => l.CustomerId == customerId)
            .Select(l => l.Id)
            .ToListAsync(ct);

        if (licenseIds.Count == 0)
            return Ok(new SearchResultDto(new(), new(), new()));

        // ─── Customers (Order'lardan distinct) ─────────────────────────
        // EF.Functions.Like case-insensitive provider'a göre değişir.
        // SQL Server collation default _CI_ → contains zaten case-insensitive.
        // In-memory provider tests için string.Contains kullanılır (server-
        // tarafı translation in-memory'de aynı semantik).
        var customers = await _db.Orders
            .Where(o => licenseIds.Contains(o.LicenseId)
                && (o.Username.Contains(term)
                    || (o.DisplayName != null && o.DisplayName.Contains(term))
                    || o.CustomerId.Contains(term)))
            .GroupBy(o => new { o.CustomerId, o.Platform })
            .Select(g => new CustomerHitDto(
                g.Key.CustomerId,
                g.OrderByDescending(o => o.UpdatedAt).First().Username,
                g.OrderByDescending(o => o.UpdatedAt).First().DisplayName,
                g.Key.Platform))
            .Take(10)
            .ToListAsync(ct);

        // ─── Orders by code or message ────────────────────────────────
        var orders = await _db.Orders
            .Where(o => licenseIds.Contains(o.LicenseId)
                && ((o.Code != null && o.Code.Contains(term))
                    || o.MessageText.Contains(term)))
            .OrderByDescending(o => o.AddedAt)
            .Take(10)
            .Select(o => new OrderHitDto(
                o.Id, o.SessionId, o.Code, o.MessageText,
                o.Username, o.DisplayName, o.Price, o.AddedAt))
            .ToListAsync(ct);

        // ─── Payments by ReferansNo or PayerName ──────────────────────
        var payments = await _db.Payments
            .Where(p => licenseIds.Contains(p.LicenseId)
                && (p.ReferansNo.Contains(term) || p.PayerName.Contains(term)))
            .OrderByDescending(p => p.CreatedAt)
            .Take(10)
            .Select(p => new PaymentHitDto(
                p.Id, p.ReferansNo, p.PayerName, p.Amount,
                p.Status.ToString().ToLowerInvariant(), p.CreatedAt))
            .ToListAsync(ct);

        return Ok(new SearchResultDto(customers, orders, payments));
    }

}
