using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Services.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace OrderDeck.LicenseServer.Controllers.Panel;

/// <summary>
/// Mobile Panel "Hızlı istatistik" dashboard endpoint'i.
/// Range bazlı (today/week/month) aggregate metrikler:
/// ciro, sipariş sayısı, AOV, iptal oranı, aktif yayın, bekleyen dekont/kargo.
/// "Real sale" tanımı PanelCustomersController.Get(id) ile aynı:
/// PrintedAt != null && CancelledAt == null && !IsShippingFee && !IsTentativeBackup.
/// </summary>
[ApiController]
[Route("api/panel/stats")]
[Authorize(AuthenticationSchemes = "Bearer-Customer")]
public sealed class PanelStatsController : ControllerBase
{
    private static readonly TimeZoneInfo TurkeyTz =
        TimeZoneInfo.FindSystemTimeZoneById(OperatingSystem.IsWindows() ? "Turkey Standard Time" : "Europe/Istanbul");

    private readonly LicenseDbContext _db;

    public PanelStatsController(LicenseDbContext db)
    {
        _db = db;
    }

    public sealed record StatsResponse(
        string Range,
        DateTimeOffset RangeStart,
        DateTimeOffset RangeEnd,
        decimal Revenue,
        int OrderCount,
        decimal AverageOrderValue,
        decimal CancelRate,
        Guid? ActiveStreamId,
        string? ActiveStreamTitle,
        int PendingPaymentCount,
        int PendingShipmentCount,
        DateTimeOffset LastUpdatedAt);

    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] string? range, CancellationToken ct)
    {
        var rangeMode = range?.ToLowerInvariant() switch
        {
            null or "" or "today" => "today",
            "week" => "week",
            "month" => "month",
            _ => null
        };
        if (rangeMode is null)
            return Problem(title: "invalid-range", statusCode: 400);

        var utcNow = DateTimeOffset.UtcNow;
        var trNow = TimeZoneInfo.ConvertTime(utcNow, TurkeyTz);
        var (rangeStartTr, rangeEndTr) = ComputeRange(rangeMode, trNow);
        var rangeStartUtc = rangeStartTr.ToUniversalTime();
        var rangeEndUtc = rangeEndTr.ToUniversalTime();

        var authCustomerId = User.GetTenantCustomerId();
        var licenseIds = await _db.Licenses
            .Where(l => l.CustomerId == authCustomerId)
            .Select(l => l.Id)
            .ToListAsync(ct);

        if (licenseIds.Count == 0)
            return Ok(EmptyResponse(rangeMode, rangeStartTr, rangeEndTr, utcNow));

        var ordersInRange = _db.Orders
            .Where(o => licenseIds.Contains(o.LicenseId)
                     && o.AddedAt >= rangeStartUtc
                     && o.AddedAt < rangeEndUtc);

        var totalRowsInRange = await ordersInRange.CountAsync(ct);
        var cancelledInRange = await ordersInRange.CountAsync(o => o.CancelledAt != null, ct);

        var realSales = ordersInRange.Where(o => o.PrintedAt != null
                                              && o.CancelledAt == null
                                              && !o.IsShippingFee
                                              && !o.IsTentativeBackup);
        var revenue = await realSales.SumAsync(o => (decimal?)o.Price, ct) ?? 0m;
        var orderCount = await realSales.CountAsync(ct);

        var aov = orderCount > 0 ? Math.Round(revenue / orderCount, 2) : 0m;
        var cancelRate = totalRowsInRange > 0
            ? Math.Round((decimal)cancelledInRange / totalRowsInRange, 4)
            : 0m;

        var activeStream = await _db.StreamSessions
            .Where(s => licenseIds.Contains(s.LicenseId) && s.EndedAt == null)
            .OrderByDescending(s => s.StartedAt)
            .Select(s => new { s.Id, s.Title })
            .FirstOrDefaultAsync(ct);

        var pendingPaymentCount = await _db.Payments
            .Where(p => licenseIds.Contains(p.LicenseId)
                     && p.Status == Domain.PaymentStatus.Pending)
            .CountAsync(ct);

        var pendingShipmentCount = await _db.Shipments
            .Where(s => licenseIds.Contains(s.LicenseId)
                     && (s.Status == Domain.ShipmentStatus.Held
                         || s.Status == Domain.ShipmentStatus.RecipientPays))
            .CountAsync(ct);

        return Ok(new StatsResponse(
            Range: rangeMode,
            RangeStart: rangeStartTr,
            RangeEnd: rangeEndTr,
            Revenue: revenue,
            OrderCount: orderCount,
            AverageOrderValue: aov,
            CancelRate: cancelRate,
            ActiveStreamId: activeStream?.Id,
            ActiveStreamTitle: activeStream?.Title,
            PendingPaymentCount: pendingPaymentCount,
            PendingShipmentCount: pendingShipmentCount,
            LastUpdatedAt: utcNow));
    }

    private static StatsResponse EmptyResponse(string rangeMode, DateTimeOffset start, DateTimeOffset end, DateTimeOffset now)
        => new(rangeMode, start, end, 0m, 0, 0m, 0m, null, null, 0, 0, now);

    private static (DateTimeOffset start, DateTimeOffset end) ComputeRange(string mode, DateTimeOffset trNow)
    {
        var todayStart = new DateTimeOffset(trNow.Year, trNow.Month, trNow.Day, 0, 0, 0, trNow.Offset);
        return mode switch
        {
            "today" => (todayStart, trNow),
            "week"  => (todayStart.AddDays(-((int)(trNow.DayOfWeek == DayOfWeek.Sunday ? 6 : trNow.DayOfWeek - DayOfWeek.Monday))), trNow),
            "month" => (new DateTimeOffset(trNow.Year, trNow.Month, 1, 0, 0, 0, trNow.Offset), trNow),
            _ => throw new InvalidOperationException()
        };
    }
}
