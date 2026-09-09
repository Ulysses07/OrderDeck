using System.Security.Claims;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Services.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace OrderDeck.LicenseServer.Controllers.Panel;

/// <summary>
/// Mobile Panel "Siparişler" ekranı için read-only endpoint'ler.
///
/// <list type="bullet">
///   <item>GET /api/panel/sessions — yayıncının yayın oturumlarının listesi
///         (en yeni önce + her birinin aggregate'i: sipariş sayısı + toplam)</item>
///   <item>GET /api/panel/sessions/{id}/orders — belirli bir yayının
///         sipariş listesi</item>
/// </list>
///
/// Cancelled + tentative backup'lar UI'a default'ta gelir; client filtre yapar.
/// Pattern: <see cref="PanelPaymentsController"/>.
/// </summary>
[ApiController]
[Route("api/panel")]
[Authorize(AuthenticationSchemes = "Bearer-Customer")]
public sealed class PanelOrdersController : ControllerBase
{
    private readonly LicenseDbContext _db;

    public PanelOrdersController(LicenseDbContext db)
    {
        _db = db;
    }

    public sealed record SessionSummaryDto(
        Guid Id,
        string? Title,
        DateTimeOffset StartedAt,
        DateTimeOffset? EndedAt,
        string Platforms,
        int OrderCount,
        decimal TotalAmount);

    [HttpGet("sessions")]
    public async Task<IActionResult> ListSessions(
        [FromQuery] int take = 30,
        CancellationToken ct = default)
    {
        var customerId = User.GetTenantCustomerId();
        take = Math.Clamp(take, 1, 100);

        // Her yayın için sipariş sayısı + toplam — server-side aggregate
        // (mobile başına ~30 satır geliyor, network maliyeti minimum).
        var rows = await _db.StreamSessions
            .Where(s => s.License.CustomerId == customerId)
            .OrderByDescending(s => s.StartedAt)
            .Take(take)
            .Select(s => new SessionSummaryDto(
                s.Id, s.Title, s.StartedAt, s.EndedAt, s.Platforms,
                _db.Orders.Count(o => o.LicenseId == s.LicenseId
                    && o.SessionId == s.Id
                    && o.CancelledAt == null
                    && !o.IsTentativeBackup
                    && !o.IsShippingFee),
                _db.Orders
                    .Where(o => o.LicenseId == s.LicenseId
                        && o.SessionId == s.Id
                        && o.CancelledAt == null
                        && !o.IsTentativeBackup
                        && !o.IsShippingFee)
                    .Sum(o => (decimal?)o.Price) ?? 0m))
            .ToListAsync(ct);

        return Ok(rows);
    }

    public sealed record OrderDto(
        Guid Id,
        Guid? SessionId,
        string CustomerId,
        string Platform,
        string Username,
        string? DisplayName,
        string MessageText,
        string? Code,
        decimal Price,
        DateTimeOffset AddedAt,
        DateTimeOffset? PrintedAt,
        DateTimeOffset? CancelledAt,
        string? CancelReason,
        bool IsShippingFee,
        bool IsBackupPromoted,
        bool IsTentativeBackup);

    [HttpGet("sessions/{sessionId:guid}/orders")]
    public async Task<IActionResult> ListOrdersBySession(
        Guid sessionId,
        [FromQuery] int take = 500,
        CancellationToken ct = default)
    {
        var customerId = User.GetTenantCustomerId();
        take = Math.Clamp(take, 1, 2000);

        // Session caller'a ait mi kontrol; lisans id'sini de al ki sipariş
        // sorgusu LicenseId ile daralsın — SessionId tek başına tenant
        // sınırı değil (bkz. 2026-09-09 denetimi F01).
        var session = await _db.StreamSessions
            .Where(s => s.Id == sessionId && s.License.CustomerId == customerId)
            .Select(s => new { s.LicenseId })
            .FirstOrDefaultAsync(ct);
        if (session is null) return NotFound();

        var rows = await _db.Orders
            .Where(o => o.LicenseId == session.LicenseId && o.SessionId == sessionId)
            .OrderBy(o => o.AddedAt)
            .Take(take)
            .Select(o => new OrderDto(
                o.Id, o.SessionId, o.CustomerId,
                o.Platform, o.Username, o.DisplayName,
                o.MessageText, o.Code, o.Price,
                o.AddedAt, o.PrintedAt, o.CancelledAt, o.CancelReason,
                o.IsShippingFee, o.IsBackupPromoted, o.IsTentativeBackup))
            .ToListAsync(ct);

        return Ok(rows);
    }

}
