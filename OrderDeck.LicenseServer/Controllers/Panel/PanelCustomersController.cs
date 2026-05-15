using System.Security.Claims;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Services.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace OrderDeck.LicenseServer.Controllers.Panel;

/// <summary>
/// Mobile Panel "müşteri detayı" endpoint'i. Bir müşterinin tüm geçmişi:
/// toplam sipariş + toplam ciro + aktif kargo dosyaları + son N sipariş.
///
/// Customer kimliği: WPF lokal Customer entity'sinin GUID hex string'i.
/// Order ve Shipment tablolarında bu string CustomerId alanında tutuluyor.
/// LicenseServer'ın kendi Customer (Customers tablosu) entity'siyle
/// karıştırmamak gerek — bu endpoint license-customer auth altında çalışır,
/// ama "müşteri" terimini WPF'in alıcı müşterisi olarak kullanır.
/// </summary>
[ApiController]
[Route("api/panel/customers")]
[Authorize(AuthenticationSchemes = "Bearer-Customer")]
public sealed class PanelCustomersController : ControllerBase
{
    private readonly LicenseDbContext _db;

    public PanelCustomersController(LicenseDbContext db)
    {
        _db = db;
    }

    public sealed record CustomerSummaryDto(
        string CustomerId,
        string? DisplayName,
        string Username,
        string Platform,
        int OrderCount,
        decimal TotalRevenue,
        int ActiveShipmentCount,
        DateTimeOffset FirstOrderAt,
        DateTimeOffset LastOrderAt,
        List<CustomerRecentOrderDto> RecentOrders,
        List<CustomerShipmentDto> ActiveShipments);

    public sealed record CustomerRecentOrderDto(
        Guid Id,
        Guid? SessionId,
        string? SessionTitle,
        string MessageText,
        string? Code,
        decimal Price,
        DateTimeOffset AddedAt,
        DateTimeOffset? PrintedAt,
        DateTimeOffset? CancelledAt,
        bool IsShippingFee);

    public sealed record CustomerShipmentDto(
        Guid Id,
        string Status,
        decimal CumulativeAmount,
        DateTimeOffset CreatedAt,
        DateTimeOffset? HeldAt);

    [HttpGet("{customerId}")]
    public async Task<IActionResult> Get(string customerId, CancellationToken ct)
    {
        var authCustomerId = User.GetTenantCustomerId();

        // Tenant filter: customer's licenses (1+).
        var licenseIds = await _db.Licenses
            .Where(l => l.CustomerId == authCustomerId)
            .Select(l => l.Id)
            .ToListAsync(ct);

        if (licenseIds.Count == 0) return NotFound();

        // Müşteri için tüm order'ları — display info için ilkini almaya yetecek
        // kadar projeksiyon.
        var ordersQuery = _db.Orders
            .Where(o => licenseIds.Contains(o.LicenseId) && o.CustomerId == customerId);

        var any = await ordersQuery.AnyAsync(ct);
        if (!any) return NotFound();

        // Display identity — order satırlarından en güncel (UpdatedAt en yeni)
        // DisplayName + Username + Platform al.
        var identity = await ordersQuery
            .OrderByDescending(o => o.UpdatedAt)
            .Select(o => new { o.DisplayName, o.Username, o.Platform })
            .FirstAsync(ct);

        // Aggregate counts/totals — basılmış, iptal değil, kargo ücreti değil,
        // tentative değil olanları gerçek satış say.
        var stats = await ordersQuery
            .Where(o => o.PrintedAt != null
                && o.CancelledAt == null
                && !o.IsShippingFee
                && !o.IsTentativeBackup)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Count = g.Count(),
                Total = g.Sum(o => o.Price)
            })
            .FirstOrDefaultAsync(ct);

        var firstLast = await ordersQuery
            .GroupBy(_ => 1)
            .Select(g => new
            {
                First = g.Min(o => o.AddedAt),
                Last = g.Max(o => o.AddedAt)
            })
            .FirstAsync(ct);

        // Son 20 sipariş (tüm satırlar — UI iptal/kargo badge'lerini gösterir).
        var recent = await ordersQuery
            .OrderByDescending(o => o.AddedAt)
            .Take(20)
            .Select(o => new CustomerRecentOrderDto(
                o.Id,
                o.SessionId,
                o.Session != null ? o.Session.Title : null,
                o.MessageText,
                o.Code,
                o.Price,
                o.AddedAt,
                o.PrintedAt,
                o.CancelledAt,
                o.IsShippingFee))
            .ToListAsync(ct);

        // Aktif kargo dosyaları (held + recipientpays).
        var activeShipments = await _db.Shipments
            .Where(s => licenseIds.Contains(s.LicenseId)
                && s.CustomerId == customerId
                && (s.Status == Domain.ShipmentStatus.Held
                    || s.Status == Domain.ShipmentStatus.RecipientPays))
            .OrderByDescending(s => s.CreatedAt)
            .Select(s => new CustomerShipmentDto(
                s.Id,
                s.Status.ToString().ToLowerInvariant(),
                s.CumulativeAmount,
                s.CreatedAt,
                s.HeldAt))
            .ToListAsync(ct);

        return Ok(new CustomerSummaryDto(
            customerId,
            identity.DisplayName,
            identity.Username,
            identity.Platform,
            stats?.Count ?? 0,
            stats?.Total ?? 0m,
            activeShipments.Count,
            firstLast.First,
            firstLast.Last,
            recent,
            activeShipments));
    }

}
