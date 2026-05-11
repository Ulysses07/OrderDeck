using System.Security.Claims;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace OrderDeck.LicenseServer.Controllers.Licenses;

/// <summary>
/// WPF App ile LicenseServer arasında Payment senkronizasyonu için
/// endpoint'ler. WPF tarafında <c>PaymentSyncService</c> (PR B'de
/// eklenecek) bu endpoint'leri tüketir:
///
/// <list type="bullet">
///   <item>POST .../payments/sync — outbox push (WPF → server upsert)</item>
///   <item>GET  .../payments/since?since=... — reverse sync (server → WPF pull)</item>
/// </list>
///
/// Auth: Bearer-Customer + route'taki licenseId yetkili customer'a ait olmalı.
/// </summary>
[ApiController]
[Route("api/v1/licenses/{licenseId:guid}/payments")]
[Authorize(AuthenticationSchemes = "Bearer-Customer")]
public sealed class LicensesPaymentsSyncController : ControllerBase
{
    private readonly LicenseDbContext _db;

    public LicensesPaymentsSyncController(LicenseDbContext db)
    {
        _db = db;
    }

    public sealed record SyncPaymentItem(
        Guid Id,
        string PayerName,
        decimal Amount,
        DateTimeOffset PaidAt,
        string ReferansNo,
        string? PdfHash,
        string? ShipmentDirective = null);   // Kargo PR E. null veya "normal" = default.

    public sealed record SyncRequest(List<SyncPaymentItem> Payments);

    public sealed record SyncedPaymentDto(
        Guid Id,
        string Status,
        DateTimeOffset? ApprovedAt,
        DateTimeOffset? RejectedAt,
        string? RejectReason,
        DateTimeOffset UpdatedAt,
        string ShipmentDirective);   // Kargo PR E. server-authoritative echo.

    /// <summary>
    /// Batch upsert from WPF outbox. Idempotent by Payment.Id (UPSERT). Aynı
    /// LicenseId+ReferansNo birden fazla farklı Id ile gelirse ikinci insert
    /// unique constraint violation alır (caller dedup'lemiş olmalı).
    /// </summary>
    [HttpPost("sync")]
    public async Task<IActionResult> Sync(
        Guid licenseId,
        [FromBody] SyncRequest req,
        CancellationToken ct)
    {
        var customerId = GetCustomerId();
        var ownsLicense = await _db.Licenses
            .AnyAsync(l => l.Id == licenseId && l.CustomerId == customerId, ct);
        if (!ownsLicense) return NotFound();

        if (req?.Payments is null || req.Payments.Count == 0)
            return Ok(Array.Empty<SyncedPaymentDto>());

        if (req.Payments.Count > 200)
            return Problem(title: "batch-too-large", detail: "Max 200 payment per batch.", statusCode: 400);

        var now = DateTimeOffset.UtcNow;
        var ids = req.Payments.Select(p => p.Id).ToList();
        var existing = await _db.Payments
            .Where(p => p.LicenseId == licenseId && ids.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, ct);

        foreach (var item in req.Payments)
        {
            var directive = ParseDirective(item.ShipmentDirective);

            if (existing.TryGetValue(item.Id, out var current))
            {
                // Server-authoritative on status fields. Mutable from client:
                // PayerName, Amount, PaidAt, PdfHash (PDF re-parse could refine),
                // ShipmentDirective (vendor changed decision in WPF).
                // ReferansNo not updated — would break the LicenseId+ReferansNo
                // unique index if client tries to change it.
                current.PayerName = item.PayerName;
                current.Amount = item.Amount;
                current.PaidAt = item.PaidAt;
                current.PdfHash = item.PdfHash;
                current.ShipmentDirective = directive;
                current.UpdatedAt = now;
            }
            else
            {
                _db.Payments.Add(new Payment
                {
                    Id = item.Id,
                    LicenseId = licenseId,
                    PayerName = item.PayerName,
                    Amount = item.Amount,
                    PaidAt = item.PaidAt,
                    ReferansNo = item.ReferansNo,
                    PdfHash = item.PdfHash,
                    Status = PaymentStatus.Pending,
                    ShipmentDirective = directive,
                    CreatedAt = now,
                    UpdatedAt = now
                });
            }
        }

        await _db.SaveChangesAsync(ct);

        // Echo server-authoritative status back so the WPF can update its
        // local copy (e.g. picked up an approval that landed between push
        // requests).
        var echoed = await _db.Payments
            .Where(p => p.LicenseId == licenseId && ids.Contains(p.Id))
            .Select(p => new SyncedPaymentDto(
                p.Id,
                p.Status.ToString().ToLowerInvariant(),
                p.ApprovedAt, p.RejectedAt, p.RejectReason, p.UpdatedAt,
                p.ShipmentDirective.ToString().ToLowerInvariant()))
            .ToListAsync(ct);

        return Ok(echoed);
    }

    /// <summary>
    /// Reverse sync: WPF App periyodik olarak çağırır → mobile tarafından
    /// onay/red edilen payment'ları local'e indirir. `since` cursor (UTC).
    /// </summary>
    [HttpGet("since")]
    public async Task<IActionResult> Since(
        Guid licenseId,
        [FromQuery] DateTimeOffset since,
        [FromQuery] int take = 100,
        CancellationToken ct = default)
    {
        var customerId = GetCustomerId();
        var ownsLicense = await _db.Licenses
            .AnyAsync(l => l.Id == licenseId && l.CustomerId == customerId, ct);
        if (!ownsLicense) return NotFound();

        take = Math.Clamp(take, 1, 500);

        var rows = await _db.Payments
            .Where(p => p.LicenseId == licenseId && p.UpdatedAt > since)
            .OrderBy(p => p.UpdatedAt)
            .Take(take)
            .Select(p => new SyncedPaymentDto(
                p.Id,
                p.Status.ToString().ToLowerInvariant(),
                p.ApprovedAt, p.RejectedAt, p.RejectReason, p.UpdatedAt,
                p.ShipmentDirective.ToString().ToLowerInvariant()))
            .ToListAsync(ct);

        return Ok(rows);
    }

    /// <summary>Kargo PR E: client'tan gelen directive string'i enum'a parse eder.
    /// null/empty/invalid → Normal (default). Bilinçli: eski WPF client'ları
    /// alanı göndermez ama Payment yine de Normal directive ile create edilir.</summary>
    private static ShipmentDirective ParseDirective(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return ShipmentDirective.Normal;
        return raw.Trim().ToLowerInvariant() switch
        {
            "normal" => ShipmentDirective.Normal,
            "hold" => ShipmentDirective.Hold,
            "recipientpays" => ShipmentDirective.RecipientPays,
            _ => ShipmentDirective.Normal
        };
    }

    private Guid GetCustomerId()
    {
        var sub = User.FindFirst("sub")?.Value
            ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? throw new InvalidOperationException("sub claim missing");
        return Guid.Parse(sub);
    }
}
