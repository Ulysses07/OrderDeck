using System.Security.Claims;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Auth;
using OrderDeck.LicenseServer.Services.Payments;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace OrderDeck.LicenseServer.Controllers.Panel;

/// <summary>
/// OrderDeck Panel mobile app için ödeme (dekont) sorgulama ve onay/red
/// işlemleri. Yayıncı sadece kendi lisansının ödemelerini görür/yönetir.
/// Kararı ve yan etkilerini <see cref="PaymentApprovalService"/> yürütür.
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
    private readonly PaymentApprovalService _approvals;

    public PanelPaymentsController(LicenseDbContext db, PaymentApprovalService approvals)
    {
        _db = db;
        _approvals = approvals;
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
        string? RejectReason,
        string ShipmentDirective);   // Kargo PR E: "normal" | "hold" | "recipientpays"

    /// <summary>
    /// GET /api/panel/payments?status=pending|approved|rejected&amp;directive=normal|hold|recipientpays&amp;take=50.
    /// Kargo PR E: directive filtre eklendi. Bekleyen Kargolar tab'ı status=approved&amp;directive=hold,
    /// Alıcı Ödemeli tab'ı status=approved&amp;directive=recipientpays kullanır.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] string? status,
        [FromQuery] string? directive,
        [FromQuery] int take = 50,
        CancellationToken ct = default)
    {
        var customerId = User.GetTenantCustomerId();
        take = Math.Clamp(take, 1, 200);

        var query = _db.Payments
            .Where(p => p.License.CustomerId == customerId);

        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!TryParseStatus(status, out var parsedStatus))
                return Problem(title: "invalid-status", statusCode: 400);
            query = query.Where(p => p.Status == parsedStatus);
        }

        if (!string.IsNullOrWhiteSpace(directive))
        {
            if (!TryParseDirective(directive, out var parsedDirective))
                return Problem(title: "invalid-directive", statusCode: 400);
            query = query.Where(p => p.ShipmentDirective == parsedDirective);
        }

        var rows = await query
            .OrderByDescending(p => p.CreatedAt)
            .Take(take)
            .Select(p => new PaymentDto(
                p.Id, p.LicenseId, p.PayerName, p.Amount, p.PaidAt, p.ReferansNo,
                p.Status.ToString().ToLowerInvariant(),
                p.CreatedAt, p.ApprovedAt, p.RejectedAt, p.RejectReason,
                p.ShipmentDirective.ToString().ToLowerInvariant()))
            .ToListAsync(ct);

        return Ok(rows);
    }

    [HttpPost("{id:guid}/approve")]
    public async Task<IActionResult> Approve(Guid id, CancellationToken ct)
        => DecisionResult(await _approvals.ApproveAsync(id, User.GetTenantCustomerId(), ct));

    public sealed record RejectRequest(string? Reason);

    [HttpPost("{id:guid}/reject")]
    public async Task<IActionResult> Reject(Guid id, [FromBody] RejectRequest req, CancellationToken ct)
        => DecisionResult(await _approvals.RejectAsync(id, User.GetTenantCustomerId(), req?.Reason, ct));

    private IActionResult DecisionResult(PaymentDecisionOutcome outcome) => outcome switch
    {
        PaymentDecisionOutcome.Decided => NoContent(),
        PaymentDecisionOutcome.NotFound => NotFound(),
        PaymentDecisionOutcome.NotPending =>
            Problem(title: "not-pending", detail: "Bu ödeme zaten karara bağlanmış.", statusCode: 409),
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null),
    };

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

    private static bool TryParseDirective(string raw, out ShipmentDirective result)
    {
        switch (raw.Trim().ToLowerInvariant())
        {
            case "normal": result = ShipmentDirective.Normal; return true;
            case "hold": result = ShipmentDirective.Hold; return true;
            case "recipientpays": result = ShipmentDirective.RecipientPays; return true;
            default: result = ShipmentDirective.Normal; return false;
        }
    }

}
