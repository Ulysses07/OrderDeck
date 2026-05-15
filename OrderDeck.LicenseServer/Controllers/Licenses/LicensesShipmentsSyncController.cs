using System.Security.Claims;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace OrderDeck.LicenseServer.Controllers.Licenses;

/// <summary>
/// WPF App ↔ LicenseServer Shipment senkronizasyonu (Kümülatif kargo PR-D).
/// Pattern: <see cref="LicensesPaymentsSyncController"/> (Payment sync) ile
/// paralel.
///
/// <list type="bullet">
///   <item>POST .../shipments/sync — outbox push (WPF → server upsert)</item>
///   <item>GET  .../shipments/since?since=... — reverse sync (server → WPF pull)</item>
/// </list>
///
/// Auth: Bearer-Customer + route'taki licenseId yetkili customer'a ait olmalı.
/// </summary>
[ApiController]
[Route("api/v1/licenses/{licenseId:guid}/shipments")]
[Authorize(AuthenticationSchemes = "Bearer-Customer")]
public sealed class LicensesShipmentsSyncController : ControllerBase
{
    private readonly LicenseDbContext _db;

    public LicensesShipmentsSyncController(LicenseDbContext db)
    {
        _db = db;
    }

    public sealed record SyncShipmentItem(
        Guid Id,
        string CustomerId,
        string Status,
        decimal CumulativeAmount,
        DateTimeOffset CreatedAt,
        DateTimeOffset? HeldAt,
        DateTimeOffset? ShippedAt);

    public sealed record SyncRequest(List<SyncShipmentItem> Shipments);

    public sealed record SyncedShipmentDto(
        Guid Id,
        string CustomerId,
        string Status,
        decimal CumulativeAmount,
        DateTimeOffset CreatedAt,
        DateTimeOffset? HeldAt,
        DateTimeOffset? ShippedAt,
        DateTimeOffset UpdatedAt);

    /// <summary>
    /// Batch upsert from WPF outbox. WPF authoritative — server tüm field'ları
    /// kabul eder. Mobile Panel sadece okur (Payment.Approve gibi mutation
    /// endpoint'i yok, çünkü Shipment kararını WPF veriyor).
    /// </summary>
    [HttpPost("sync")]
    public async Task<IActionResult> Sync(
        Guid licenseId,
        [FromBody] SyncRequest req,
        CancellationToken ct)
    {
        var customerId = User.GetTenantCustomerId();
        var ownsLicense = await _db.Licenses
            .AnyAsync(l => l.Id == licenseId && l.CustomerId == customerId, ct);
        if (!ownsLicense) return NotFound();

        if (req?.Shipments is null || req.Shipments.Count == 0)
            return Ok(Array.Empty<SyncedShipmentDto>());

        if (req.Shipments.Count > 200)
            return Problem(title: "batch-too-large", detail: "Max 200 shipment per batch.", statusCode: 400);

        var now = DateTimeOffset.UtcNow;
        var ids = req.Shipments.Select(s => s.Id).ToList();
        var existing = await _db.Shipments
            .Where(s => s.LicenseId == licenseId && ids.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, ct);

        foreach (var item in req.Shipments)
        {
            var status = ParseStatus(item.Status);

            if (existing.TryGetValue(item.Id, out var current))
            {
                // WPF authoritative — tüm mutable alanları update
                current.CustomerId = item.CustomerId;
                current.Status = status;
                current.CumulativeAmount = item.CumulativeAmount;
                current.HeldAt = item.HeldAt;
                current.ShippedAt = item.ShippedAt;
                current.UpdatedAt = now;
                // CreatedAt değişmez (WPF tarafında oluşturulduğu an)
            }
            else
            {
                _db.Shipments.Add(new Shipment
                {
                    Id = item.Id,
                    LicenseId = licenseId,
                    CustomerId = item.CustomerId,
                    Status = status,
                    CumulativeAmount = item.CumulativeAmount,
                    CreatedAt = item.CreatedAt,
                    HeldAt = item.HeldAt,
                    ShippedAt = item.ShippedAt,
                    UpdatedAt = now
                });
            }
        }

        await _db.SaveChangesAsync(ct);

        var echoed = await _db.Shipments
            .Where(s => s.LicenseId == licenseId && ids.Contains(s.Id))
            .Select(s => new SyncedShipmentDto(
                s.Id, s.CustomerId,
                s.Status.ToString().ToLowerInvariant(),
                s.CumulativeAmount,
                s.CreatedAt, s.HeldAt, s.ShippedAt, s.UpdatedAt))
            .ToListAsync(ct);

        return Ok(echoed);
    }

    /// <summary>
    /// Reverse sync: WPF App nadiren bu endpoint'i çağırır (Shipment WPF
    /// authoritative; mobile mutation yapmıyor). Yine de eklendi — gelecekte
    /// multi-instance WPF veya CSR'ın server'da düzeltme yaptığı durumlar için.
    /// </summary>
    [HttpGet("since")]
    public async Task<IActionResult> Since(
        Guid licenseId,
        [FromQuery] DateTimeOffset since,
        [FromQuery] int take = 100,
        CancellationToken ct = default)
    {
        var customerId = User.GetTenantCustomerId();
        var ownsLicense = await _db.Licenses
            .AnyAsync(l => l.Id == licenseId && l.CustomerId == customerId, ct);
        if (!ownsLicense) return NotFound();

        take = Math.Clamp(take, 1, 500);

        var rows = await _db.Shipments
            .Where(s => s.LicenseId == licenseId && s.UpdatedAt > since)
            .OrderBy(s => s.UpdatedAt)
            .Take(take)
            .Select(s => new SyncedShipmentDto(
                s.Id, s.CustomerId,
                s.Status.ToString().ToLowerInvariant(),
                s.CumulativeAmount,
                s.CreatedAt, s.HeldAt, s.ShippedAt, s.UpdatedAt))
            .ToListAsync(ct);

        return Ok(rows);
    }

    private static ShipmentStatus ParseStatus(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return ShipmentStatus.Pending;
        return raw.Trim().ToLowerInvariant() switch
        {
            "pending" => ShipmentStatus.Pending,
            "held" => ShipmentStatus.Held,
            "recipientpays" => ShipmentStatus.RecipientPays,
            "shipped" => ShipmentStatus.Shipped,
            _ => ShipmentStatus.Pending
        };
    }

}
