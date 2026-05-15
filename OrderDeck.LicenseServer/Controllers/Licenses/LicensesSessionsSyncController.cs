using System.Security.Claims;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Auth;
using OrderDeck.LicenseServer.Services.Push;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace OrderDeck.LicenseServer.Controllers.Licenses;

/// <summary>
/// Siparis sync (2026-05-13): WPF StreamSession + Order replikası için
/// outbox push endpoint. Pattern: <see cref="LicensesPaymentsSyncController"/>.
/// </summary>
[ApiController]
[Route("api/v1/licenses/{licenseId:guid}")]
[Authorize(AuthenticationSchemes = "Bearer-Customer")]
public sealed class LicensesSessionsSyncController : ControllerBase
{
    private readonly LicenseDbContext _db;
    private readonly INotificationSender _push;
    private readonly ILogger<LicensesSessionsSyncController> _log;

    public LicensesSessionsSyncController(
        LicenseDbContext db,
        INotificationSender push,
        ILogger<LicensesSessionsSyncController> log)
    {
        _db = db;
        _push = push;
        _log = log;
    }

    // ─── Session sync ─────────────────────────────────────────────────

    public sealed record SyncSessionItem(
        Guid Id,
        string? Title,
        DateTimeOffset StartedAt,
        DateTimeOffset? EndedAt,
        string Platforms,
        string? Notes);

    public sealed record SyncSessionsRequest(List<SyncSessionItem> Sessions);

    public sealed record SyncedSessionDto(
        Guid Id, string? Title,
        DateTimeOffset StartedAt, DateTimeOffset? EndedAt,
        string Platforms, string? Notes,
        DateTimeOffset UpdatedAt);

    [HttpPost("sessions/sync")]
    public async Task<IActionResult> SyncSessions(
        Guid licenseId, [FromBody] SyncSessionsRequest req, CancellationToken ct)
    {
        var customerId = User.GetTenantCustomerId();
        var ownsLicense = await _db.Licenses
            .AnyAsync(l => l.Id == licenseId && l.CustomerId == customerId, ct);
        if (!ownsLicense) return NotFound();

        if (req?.Sessions is null || req.Sessions.Count == 0)
            return Ok(Array.Empty<SyncedSessionDto>());

        if (req.Sessions.Count > 200)
            return Problem(title: "batch-too-large", detail: "Max 200 session per batch.", statusCode: 400);

        var now = DateTimeOffset.UtcNow;
        var ids = req.Sessions.Select(s => s.Id).ToList();
        var existing = await _db.StreamSessions
            .Where(s => s.LicenseId == licenseId && ids.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, ct);

        var newLiveSessions = new List<(string? Title, string Platforms)>();

        foreach (var item in req.Sessions)
        {
            if (existing.TryGetValue(item.Id, out var current))
            {
                current.Title = item.Title;
                current.EndedAt = item.EndedAt;
                current.Platforms = item.Platforms;
                current.Notes = item.Notes;
                current.UpdatedAt = now;
            }
            else
            {
                _db.StreamSessions.Add(new StreamSession
                {
                    Id = item.Id,
                    LicenseId = licenseId,
                    Title = item.Title,
                    StartedAt = item.StartedAt,
                    EndedAt = item.EndedAt,
                    Platforms = item.Platforms,
                    Notes = item.Notes,
                    UpdatedAt = now
                });
                // Sadece henüz bitmemiş (canlı) yeni session için notify.
                if (item.EndedAt is null)
                    newLiveSessions.Add((item.Title, item.Platforms));
            }
        }

        await _db.SaveChangesAsync(ct);

        if (newLiveSessions.Count > 0)
        {
            try
            {
                foreach (var s in newLiveSessions)
                {
                    var title = "Yayın başladı";
                    var body = string.IsNullOrWhiteSpace(s.Title)
                        ? $"Platform: {s.Platforms}"
                        : s.Title!;
                    await _push.SendToCustomerAsync(
                        customerId, title, body,
                        new Dictionary<string, string>
                        {
                            ["type"] = "session-started",
                            ["licenseId"] = licenseId.ToString()
                        }, ct);
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex,
                    "Push send failed for new sessions (license={LicenseId}, count={Count})",
                    licenseId, newLiveSessions.Count);
            }
        }

        var echoed = await _db.StreamSessions
            .Where(s => s.LicenseId == licenseId && ids.Contains(s.Id))
            .Select(s => new SyncedSessionDto(
                s.Id, s.Title, s.StartedAt, s.EndedAt, s.Platforms, s.Notes, s.UpdatedAt))
            .ToListAsync(ct);

        return Ok(echoed);
    }

    // ─── Order (Label) sync ───────────────────────────────────────────

    public sealed record SyncOrderItem(
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

    public sealed record SyncOrdersRequest(List<SyncOrderItem> Orders);

    public sealed record SyncedOrderDto(
        Guid Id, Guid? SessionId, string CustomerId,
        string Platform, string Username, string? DisplayName,
        string MessageText, string? Code, decimal Price,
        DateTimeOffset AddedAt, DateTimeOffset? PrintedAt,
        DateTimeOffset? CancelledAt, string? CancelReason,
        bool IsShippingFee, bool IsBackupPromoted, bool IsTentativeBackup,
        DateTimeOffset UpdatedAt);

    [HttpPost("orders/sync")]
    public async Task<IActionResult> SyncOrders(
        Guid licenseId, [FromBody] SyncOrdersRequest req, CancellationToken ct)
    {
        var customerId = User.GetTenantCustomerId();
        var ownsLicense = await _db.Licenses
            .AnyAsync(l => l.Id == licenseId && l.CustomerId == customerId, ct);
        if (!ownsLicense) return NotFound();

        if (req?.Orders is null || req.Orders.Count == 0)
            return Ok(Array.Empty<SyncedOrderDto>());

        if (req.Orders.Count > 200)
            return Problem(title: "batch-too-large", detail: "Max 200 order per batch.", statusCode: 400);

        var now = DateTimeOffset.UtcNow;
        var ids = req.Orders.Select(o => o.Id).ToList();
        var existing = await _db.Orders
            .Where(o => o.LicenseId == licenseId && ids.Contains(o.Id))
            .ToDictionaryAsync(o => o.Id, ct);

        var newPrintedOrders = new List<decimal>();

        foreach (var item in req.Orders)
        {
            if (existing.TryGetValue(item.Id, out var current))
            {
                current.SessionId = item.SessionId;
                current.MessageText = item.MessageText;
                current.Code = item.Code;
                current.Price = item.Price;
                current.PrintedAt = item.PrintedAt;
                current.CancelledAt = item.CancelledAt;
                current.CancelReason = item.CancelReason;
                current.IsShippingFee = item.IsShippingFee;
                current.IsBackupPromoted = item.IsBackupPromoted;
                current.IsTentativeBackup = item.IsTentativeBackup;
                current.UpdatedAt = now;
            }
            else
            {
                _db.Orders.Add(new Order
                {
                    Id = item.Id,
                    LicenseId = licenseId,
                    SessionId = item.SessionId,
                    CustomerId = item.CustomerId,
                    Platform = item.Platform,
                    Username = item.Username,
                    DisplayName = item.DisplayName,
                    MessageText = item.MessageText,
                    Code = item.Code,
                    Price = item.Price,
                    AddedAt = item.AddedAt,
                    PrintedAt = item.PrintedAt,
                    CancelledAt = item.CancelledAt,
                    CancelReason = item.CancelReason,
                    IsShippingFee = item.IsShippingFee,
                    IsBackupPromoted = item.IsBackupPromoted,
                    IsTentativeBackup = item.IsTentativeBackup,
                    UpdatedAt = now
                });
                // Sadece basılmış (printed), iptal değil, kargo ücreti değil,
                // backup değil order'ları gerçek satış olarak say. Chat akışı
                // üst limit basma operasyonu — sayım yeterli, push'ı spam'lemez.
                if (item.PrintedAt is not null
                    && item.CancelledAt is null
                    && !item.IsShippingFee
                    && !item.IsTentativeBackup)
                {
                    newPrintedOrders.Add(item.Price);
                }
            }
        }

        await _db.SaveChangesAsync(ct);

        if (newPrintedOrders.Count > 0)
        {
            try
            {
                var (title, body) = BuildOrderNotification(newPrintedOrders);
                await _push.SendToCustomerAsync(
                    customerId, title, body,
                    new Dictionary<string, string>
                    {
                        ["type"] = "orders",
                        ["licenseId"] = licenseId.ToString(),
                        ["count"] = newPrintedOrders.Count.ToString()
                    }, ct);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex,
                    "Push send failed for new orders (license={LicenseId}, count={Count})",
                    licenseId, newPrintedOrders.Count);
            }
        }

        var echoed = await _db.Orders
            .Where(o => o.LicenseId == licenseId && ids.Contains(o.Id))
            .Select(o => new SyncedOrderDto(
                o.Id, o.SessionId, o.CustomerId,
                o.Platform, o.Username, o.DisplayName,
                o.MessageText, o.Code, o.Price,
                o.AddedAt, o.PrintedAt, o.CancelledAt, o.CancelReason,
                o.IsShippingFee, o.IsBackupPromoted, o.IsTentativeBackup,
                o.UpdatedAt))
            .ToListAsync(ct);

        return Ok(echoed);
    }

    /// <summary>
    /// Order batch notification metni. Spam'i azaltmak için her zaman
    /// "N yeni sipariş — X TL" özet formatı kullanılır (tek order olsa bile).
    /// </summary>
    public static (string Title, string Body) BuildOrderNotification(
        IReadOnlyList<decimal> prices)
    {
        var tr = System.Globalization.CultureInfo.GetCultureInfo("tr-TR");
        var total = prices.Sum();
        return ("Yeni sipariş",
            $"{prices.Count} yeni sipariş, toplam {total.ToString("N2", tr)} ₺");
    }

}
