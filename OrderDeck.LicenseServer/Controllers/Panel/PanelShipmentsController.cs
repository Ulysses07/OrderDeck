using System.Security.Claims;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace OrderDeck.LicenseServer.Controllers.Panel;

/// <summary>
/// Mobile Panel app için Shipment (kümülatif kargo dosyası) sorgulama.
/// Read-only — kararlar WPF tarafında verilir, Panel sadece görüntüler.
///
/// Filter desteği:
/// <list type="bullet">
///   <item>status=pending|held|recipientpays|shipped</item>
///   <item>customerId=&lt;guid hex&gt; — tek müşterinin kargo geçmişi</item>
/// </list>
///
/// Pattern: <see cref="PanelPaymentsController"/>. Auth: Bearer-Customer +
/// kullanıcının lisansına ait Shipment'lar.
/// </summary>
[ApiController]
[Route("api/panel/shipments")]
[Authorize(AuthenticationSchemes = "Bearer-Customer")]
public sealed class PanelShipmentsController : ControllerBase
{
    private readonly LicenseDbContext _db;

    public PanelShipmentsController(LicenseDbContext db)
    {
        _db = db;
    }

    public sealed record ShipmentDto(
        Guid Id,
        Guid LicenseId,
        string CustomerId,
        string Status,
        decimal CumulativeAmount,
        DateTimeOffset CreatedAt,
        DateTimeOffset? HeldAt,
        DateTimeOffset? ShippedAt,
        DateTimeOffset UpdatedAt);

    /// <summary>
    /// GET /api/panel/shipments?status=held&amp;customerId=...&amp;take=50.
    /// Default sıralama: en yeni (UpdatedAt DESC).
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] string? status,
        [FromQuery] string? customerId,
        [FromQuery] int take = 50,
        CancellationToken ct = default)
    {
        var ownerCustomerId = User.GetTenantCustomerId();
        take = Math.Clamp(take, 1, 200);

        var query = _db.Shipments
            .Where(s => s.License.CustomerId == ownerCustomerId);

        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!TryParseStatus(status, out var parsed))
                return Problem(title: "invalid-status", statusCode: 400);
            query = query.Where(s => s.Status == parsed);
        }

        if (!string.IsNullOrWhiteSpace(customerId))
        {
            var cid = customerId.Trim();
            query = query.Where(s => s.CustomerId == cid);
        }

        var rows = await query
            .OrderByDescending(s => s.UpdatedAt)
            .Take(take)
            .Select(s => new ShipmentDto(
                s.Id, s.LicenseId, s.CustomerId,
                s.Status.ToString().ToLowerInvariant(),
                s.CumulativeAmount,
                s.CreatedAt, s.HeldAt, s.ShippedAt, s.UpdatedAt))
            .ToListAsync(ct);

        return Ok(rows);
    }

    private static bool TryParseStatus(string raw, out ShipmentStatus result)
    {
        switch (raw.Trim().ToLowerInvariant())
        {
            case "pending": result = ShipmentStatus.Pending; return true;
            case "held": result = ShipmentStatus.Held; return true;
            case "recipientpays": result = ShipmentStatus.RecipientPays; return true;
            case "shipped": result = ShipmentStatus.Shipped; return true;
            default: result = ShipmentStatus.Pending; return false;
        }
    }

}
