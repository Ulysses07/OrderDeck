using System.Security.Claims;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace OrderDeck.LicenseServer.Controllers.Panel;

/// <summary>
/// OrderDeck Panel mobile app için ödeme (dekont) sorgulama ve onay/red
/// işlemleri. Yayıncı sadece kendi lisansının ödemelerini görür/yönetir.
///
/// Bu PR'da Payment satırlarının nasıl OLUŞTURULDUĞU dahil değil — sonraki
/// PR'larda WPF App outbox (PDF parse sonucu) veya customer mobile app
/// (dekont upload) tarafından create edilecek.
/// </summary>
[ApiController]
[Route("api/panel/payments")]
[Authorize(AuthenticationSchemes = "Bearer-Customer")]
public sealed class PanelPaymentsController : ControllerBase
{
    private readonly LicenseDbContext _db;

    public PanelPaymentsController(LicenseDbContext db)
    {
        _db = db;
    }

    public sealed record PaymentDto(
        Guid Id,
        Guid LicenseId,
        string PayerName,
        decimal Amount,
        DateTimeOffset PaidAt,
        string ReferansNo,
        string Status,
        DateTimeOffset CreatedAt,
        DateTimeOffset? ApprovedAt,
        DateTimeOffset? RejectedAt,
        string? RejectReason);

    /// <summary>GET /api/panel/payments?status=pending|approved|rejected&amp;take=50</summary>
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] string? status,
        [FromQuery] int take = 50,
        CancellationToken ct = default)
    {
        var customerId = GetCustomerId();
        take = Math.Clamp(take, 1, 200);

        var query = _db.Payments
            .Where(p => p.License.CustomerId == customerId);

        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!TryParseStatus(status, out var parsed))
                return Problem(title: "invalid-status", statusCode: 400);
            query = query.Where(p => p.Status == parsed);
        }

        var rows = await query
            .OrderByDescending(p => p.CreatedAt)
            .Take(take)
            .Select(p => new PaymentDto(
                p.Id, p.LicenseId, p.PayerName, p.Amount, p.PaidAt, p.ReferansNo,
                p.Status.ToString().ToLowerInvariant(),
                p.CreatedAt, p.ApprovedAt, p.RejectedAt, p.RejectReason))
            .ToListAsync(ct);

        return Ok(rows);
    }

    [HttpPost("{id:guid}/approve")]
    public async Task<IActionResult> Approve(Guid id, CancellationToken ct)
    {
        var customerId = GetCustomerId();
        var payment = await _db.Payments
            .Include(p => p.License)
            .FirstOrDefaultAsync(p => p.Id == id && p.License.CustomerId == customerId, ct);
        if (payment is null) return NotFound();

        if (payment.Status != PaymentStatus.Pending)
            return Problem(title: "not-pending", detail: "Bu ödeme zaten karara bağlanmış.", statusCode: 409);

        var now = DateTimeOffset.UtcNow;
        payment.Status = PaymentStatus.Approved;
        payment.ApprovedAt = now;
        payment.ApprovedByCustomerId = customerId;
        payment.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    public sealed record RejectRequest(string? Reason);

    [HttpPost("{id:guid}/reject")]
    public async Task<IActionResult> Reject(Guid id, [FromBody] RejectRequest req, CancellationToken ct)
    {
        var customerId = GetCustomerId();
        var payment = await _db.Payments
            .Include(p => p.License)
            .FirstOrDefaultAsync(p => p.Id == id && p.License.CustomerId == customerId, ct);
        if (payment is null) return NotFound();

        if (payment.Status != PaymentStatus.Pending)
            return Problem(title: "not-pending", detail: "Bu ödeme zaten karara bağlanmış.", statusCode: 409);

        var reason = (req?.Reason ?? "").Trim();
        if (reason.Length > 500) reason = reason[..500];

        var now = DateTimeOffset.UtcNow;
        payment.Status = PaymentStatus.Rejected;
        payment.RejectedAt = now;
        payment.RejectedByCustomerId = customerId;
        payment.RejectReason = reason.Length > 0 ? reason : null;
        payment.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    private static bool TryParseStatus(string raw, out PaymentStatus result)
    {
        switch (raw.Trim().ToLowerInvariant())
        {
            case "pending": result = PaymentStatus.Pending; return true;
            case "approved": result = PaymentStatus.Approved; return true;
            case "rejected": result = PaymentStatus.Rejected; return true;
            default: result = PaymentStatus.Pending; return false;
        }
    }

    private Guid GetCustomerId()
    {
        var sub = User.FindFirst("sub")?.Value
            ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? throw new InvalidOperationException("sub claim missing");
        return Guid.Parse(sub);
    }
}
