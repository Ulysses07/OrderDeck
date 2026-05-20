using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.ShopperPayments;

namespace OrderDeck.LicenseServer.Controllers.Shopper;

/// <summary>
/// Shopper-facing broadcaster (yayıncı) endpoint'leri. CodeLookup anonim;
/// diğerleri (join/leave) Bearer-Shopper gerektirir.
/// </summary>
[ApiController]
[Authorize(AuthenticationSchemes = "Bearer-Shopper")]
[Route("api/v1/shopper/broadcasters")]
public sealed class ShopperBroadcastersController : ControllerBase
{
    private readonly LicenseDbContext _db;
    private readonly ShopperPaymentSubmissionService _paymentService;

    public ShopperBroadcastersController(LicenseDbContext db, ShopperPaymentSubmissionService paymentService)
    {
        _db = db;
        _paymentService = paymentService;
    }

    public sealed record CodeLookupResponse(Guid LicenseId, string DisplayName);

    public sealed record JoinRequest(string BroadcasterCode, string Platform, string Username);
    public sealed record JoinResponse(BroadcasterSummary[] Broadcasters);

    private Guid? GetShopperId()
    {
        var sub = User.FindFirst("sub")?.Value ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(sub, out var id) ? id : null;
    }

    [AllowAnonymous]
    [HttpGet("code-lookup")]
    public async Task<IActionResult> CodeLookup([FromQuery] string? code, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(code))
            return Problem(title: "empty-code", statusCode: 400);

        var normalized = code.Trim().ToLowerInvariant();
        var license = await _db.Licenses
            .Where(l => l.ShopperCode == normalized)
            .Select(l => new { l.Id, CustomerName = l.Customer.Name })
            .FirstOrDefaultAsync(ct);
        if (license is null) return NotFound();

        return Ok(new CodeLookupResponse(license.Id, license.CustomerName));
    }

    // ── POST /api/v1/shopper/broadcasters/join ────────────────────────────────

    [HttpPost("join")]
    public async Task<IActionResult> Join([FromBody] JoinRequest req, CancellationToken ct)
    {
        // 1. Parse shopperId from claims
        var shopperId = GetShopperId();
        if (shopperId is null) return Unauthorized();

        // 2. Load shopper
        var shopper = await _db.Shoppers
            .FirstOrDefaultAsync(s => s.Id == shopperId && s.DeletedAt == null, ct);
        if (shopper is null) return Unauthorized();

        // 3. Validate fields
        if (string.IsNullOrWhiteSpace(req.BroadcasterCode) ||
            string.IsNullOrWhiteSpace(req.Platform) ||
            string.IsNullOrWhiteSpace(req.Username))
            return Problem(title: "invalid-input", statusCode: 400);

        // 4. Lookup license by ShopperCode
        var normalizedCode = req.BroadcasterCode.Trim().ToLowerInvariant();
        var license = await _db.Licenses
            .Include(l => l.Customer)
            .Where(l => l.ShopperCode == normalizedCode)
            .FirstOrDefaultAsync(ct);
        if (license is null)
            return Problem(title: "invalid-code", statusCode: 404);

        // 5. Check active link for (ShopperId, LicenseId)
        var existingActiveLink = await _db.ShopperBroadcasterLinks
            .FirstOrDefaultAsync(l => l.ShopperId == shopperId && l.LicenseId == license.Id && l.LeftAt == null, ct);
        if (existingActiveLink is not null)
            return Problem(title: "already-linked", statusCode: 409);

        // 6. Match WpfCustomerProjection
        var platformNorm = req.Platform.Trim().ToLowerInvariant();
        var usernameNorm = req.Username.Trim();
        var wpfMatch = await _db.WpfCustomerProjections
            .Where(p => p.LicenseId == license.Id &&
                        p.Platform == platformNorm &&
                        p.Username == usernameNorm)
            .FirstOrDefaultAsync(ct);

        // 7. Insert new link
        var link = new ShopperBroadcasterLink
        {
            Id = Guid.NewGuid(),
            ShopperId = shopperId.Value,
            LicenseId = license.Id,
            Platform = platformNorm,
            Username = usernameNorm,
            WpfCustomerId = wpfMatch?.Id,
            JoinedAt = DateTimeOffset.UtcNow,
        };
        _db.ShopperBroadcasterLinks.Add(link);

        // 8. SaveChanges
        await _db.SaveChangesAsync(ct);

        // 9. Load all active links → BroadcasterSummary array
        var broadcasters = await _db.ShopperBroadcasterLinks
            .Where(l => l.ShopperId == shopperId && l.LeftAt == null)
            .Select(l => new BroadcasterSummary(l.LicenseId, l.License.Customer.Name, l.Platform, l.Username))
            .ToArrayAsync(ct);

        // 10. Return 200
        return Ok(new JoinResponse(broadcasters));
    }

    // ── GET /api/v1/shopper/broadcasters/{licenseId}/orders ──────────────────

    public sealed record OrderItem(
        Guid Id,
        Guid? SessionId,
        string? SessionTitle,
        string Platform,
        string MessageText,
        string? Code,
        decimal Price,
        DateTimeOffset AddedAt,
        DateTimeOffset? PrintedAt,
        DateTimeOffset? CancelledAt,
        bool IsShippingFee);

    public sealed record OrdersResponse(OrderItem[] Items, string? NextCursor);

    [HttpGet("{licenseId:guid}/orders")]
    public async Task<IActionResult> GetOrders(
        Guid licenseId,
        [FromQuery] string? cursor,
        [FromQuery] int limit = 50,
        [FromQuery] string? status = null,
        CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 100);

        // 1. Parse shopperId from claims
        var shopperId = GetShopperId();
        if (shopperId is null) return Unauthorized();

        var shopper = await _db.Shoppers
            .FirstOrDefaultAsync(s => s.Id == shopperId && s.DeletedAt == null, ct);
        if (shopper is null) return Unauthorized();

        // 2. Verify active link
        var link = await _db.ShopperBroadcasterLinks
            .FirstOrDefaultAsync(l => l.ShopperId == shopperId && l.LicenseId == licenseId && l.LeftAt == null, ct);
        if (link is null)
            return Problem(title: "not-linked", statusCode: 403);

        // 3. No WpfCustomerId → no orders mappable yet
        if (link.WpfCustomerId is null)
            return Ok(new OrdersResponse(Array.Empty<OrderItem>(), null));

        // 4. Convert WpfCustomerId to N-format string to match Order.CustomerId
        var wpfCustomerIdString = link.WpfCustomerId.Value.ToString("N");

        // 5. Build query
        var query = _db.Orders.Where(o => o.LicenseId == licenseId && o.CustomerId == wpfCustomerIdString);

        // 6. Status filter
        query = status?.ToLowerInvariant() switch
        {
            "active" => query.Where(o => o.CancelledAt == null),
            "cancelled" => query.Where(o => o.CancelledAt != null),
            _ => query
        };

        // 7. Parse cursor: composite {AddedAt ticks}|{Id}
        if (TryDecodeCursor(cursor, out var cursorTicks, out var cursorId))
        {
            var cursorTs = new DateTimeOffset(cursorTicks, TimeSpan.Zero);
            query = query.Where(o =>
                o.AddedAt < cursorTs ||
                (o.AddedAt == cursorTs && o.Id.CompareTo(cursorId) < 0));
        }

        var rows = await query
            .OrderByDescending(o => o.AddedAt)
            .ThenByDescending(o => o.Id)
            .Take(limit + 1)
            .Select(o => new
            {
                o.Id,
                o.SessionId,
                SessionTitle = o.Session != null ? o.Session.Title : null,
                o.Platform,
                o.MessageText,
                o.Code,
                o.Price,
                o.AddedAt,
                o.PrintedAt,
                o.CancelledAt,
                o.IsShippingFee,
            })
            .ToListAsync(ct);

        // 8. Build nextCursor if more pages
        string? nextCursor = null;
        if (rows.Count > limit)
        {
            rows.RemoveAt(rows.Count - 1);
            var last = rows[^1];
            nextCursor = EncodeCursor(last.AddedAt, last.Id);
        }

        var items = rows.Select(r => new OrderItem(
            r.Id,
            r.SessionId,
            r.SessionTitle,
            r.Platform,
            r.MessageText,
            r.Code,
            r.Price,
            r.AddedAt,
            r.PrintedAt,
            r.CancelledAt,
            r.IsShippingFee)).ToArray();

        return Ok(new OrdersResponse(items, nextCursor));
    }

    // ── GET /api/v1/shopper/broadcasters/{licenseId}/payments ────────────────

    public sealed record PaymentItem(
        Guid Id,
        string PayerName,
        decimal Amount,
        DateTimeOffset PaidAt,
        string? ReferansNo,
        string Status,
        string? RejectReason,
        DateTimeOffset CreatedAt,
        DateTimeOffset? ApprovedAt,
        DateTimeOffset? RejectedAt);

    public sealed record PaymentsResponse(PaymentItem[] Items, string? NextCursor);

    [HttpGet("{licenseId:guid}/payments")]
    public async Task<IActionResult> GetPayments(
        Guid licenseId,
        [FromQuery] string? cursor,
        [FromQuery] int limit = 50,
        [FromQuery] string? status = null,
        CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 100);

        // 1. Parse shopperId from claims
        var shopperId = GetShopperId();
        if (shopperId is null) return Unauthorized();

        var shopper = await _db.Shoppers
            .FirstOrDefaultAsync(s => s.Id == shopperId && s.DeletedAt == null, ct);
        if (shopper is null) return Unauthorized();

        // 2. Verify active link
        var link = await _db.ShopperBroadcasterLinks
            .FirstOrDefaultAsync(l => l.ShopperId == shopperId && l.LicenseId == licenseId && l.LeftAt == null, ct);
        if (link is null)
            return Problem(title: "not-linked", statusCode: 403);

        // 3. Build query: only shopper-uploaded payments
        var query = _db.Payments.Where(p => p.LicenseId == licenseId && p.ShopperId == shopperId);

        // 4. Status filter
        query = status?.ToLowerInvariant() switch
        {
            "pending" => query.Where(p => p.Status == PaymentStatus.Pending),
            "approved" => query.Where(p => p.Status == PaymentStatus.Approved),
            "rejected" => query.Where(p => p.Status == PaymentStatus.Rejected),
            _ => query
        };

        // 5. Parse cursor: composite {CreatedAt ticks}|{Id}
        if (TryDecodeCursor(cursor, out var cursorTicks, out var cursorId))
        {
            var cursorTs = new DateTimeOffset(cursorTicks, TimeSpan.Zero);
            query = query.Where(p =>
                p.CreatedAt < cursorTs ||
                (p.CreatedAt == cursorTs && p.Id.CompareTo(cursorId) < 0));
        }

        var rows = await query
            .OrderByDescending(p => p.CreatedAt)
            .ThenByDescending(p => p.Id)
            .Take(limit + 1)
            .Select(p => new
            {
                p.Id,
                p.PayerName,
                p.Amount,
                p.PaidAt,
                p.ReferansNo,
                p.Status,
                p.RejectReason,
                p.CreatedAt,
                p.ApprovedAt,
                p.RejectedAt,
            })
            .ToListAsync(ct);

        // 6. Build nextCursor if more pages
        string? nextCursor = null;
        if (rows.Count > limit)
        {
            rows.RemoveAt(rows.Count - 1);
            var last = rows[^1];
            nextCursor = EncodeCursor(last.CreatedAt, last.Id);
        }

        var items = rows.Select(r => new PaymentItem(
            r.Id,
            r.PayerName,
            r.Amount,
            r.PaidAt,
            r.ReferansNo,
            r.Status.ToString().ToLowerInvariant(),
            r.RejectReason,
            r.CreatedAt,
            r.ApprovedAt,
            r.RejectedAt)).ToArray();

        return Ok(new PaymentsResponse(items, nextCursor));
    }

    // ── POST /api/v1/shopper/broadcasters/{licenseId}/payments ───────────────

    public sealed record SubmitResponse(
        Guid PaymentId,
        string[] FraudFlags,
        string ParserConfidence,
        SubmitParsedMetadata Parsed);

    public sealed record SubmitParsedMetadata(
        string? PayerName,
        decimal? Amount,
        DateTimeOffset? PaidAt,
        string? ReferansNo,
        string? RecipientIban,
        string? RecipientName);

    [HttpPost("{licenseId:guid}/payments")]
    [RequestSizeLimit(6 * 1024 * 1024)]  // 6 MB to allow 5MB PDF + multipart overhead
    public async Task<IActionResult> SubmitPayment(
        Guid licenseId,
        [FromForm] IFormFile? pdf,
        [FromForm] decimal? amount,
        [FromForm] string? payerName,
        [FromForm] DateTimeOffset? paidAt,
        [FromForm] string? referansNo,
        CancellationToken ct)
    {
        // 1. Parse shopperId from claims
        var shopperId = GetShopperId();
        if (shopperId is null) return Unauthorized();

        // 2. Load shopper; deleted → 401
        var shopper = await _db.Shoppers
            .FirstOrDefaultAsync(s => s.Id == shopperId && s.DeletedAt == null, ct);
        if (shopper is null) return Unauthorized();

        // 3. Find active link
        var link = await _db.ShopperBroadcasterLinks
            .FirstOrDefaultAsync(l => l.ShopperId == shopperId && l.LicenseId == licenseId && l.LeftAt == null, ct);
        if (link is null)
            return Problem(title: "not-linked", statusCode: 403);

        // 4. PDF presence check
        if (pdf is null || pdf.Length == 0)
            return Problem(title: "missing-pdf", statusCode: 400);

        // 5. Size check (5MB)
        if (pdf.Length > 5 * 1024 * 1024)
            return Problem(title: "payload-too-large", statusCode: 413);

        // 6. Read PDF bytes
        using var ms = new MemoryStream();
        await pdf.CopyToAsync(ms, ct);
        var bytes = ms.ToArray();

        // 7. IP + User-Agent
        var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "";
        var userAgent = Request.Headers.UserAgent.ToString();

        // 8. Delegate to submission service
        try
        {
            var result = await _paymentService.SubmitAsync(
                new SubmitInput(shopperId.Value, licenseId, bytes, amount, payerName, paidAt, referansNo, ipAddress, userAgent),
                ct);

            var response = new SubmitResponse(
                result.PaymentId,
                result.FraudFlags,
                result.ParserConfidence,
                new SubmitParsedMetadata(
                    result.ParserResult.PayerName,
                    result.ParserResult.Amount,
                    result.ParserResult.PaidAt is { } pd ? new DateTimeOffset(pd, TimeSpan.Zero) : null,
                    result.ParserResult.ReferansNo,
                    result.ParserResult.RecipientIban,
                    result.ParserResult.RecipientName));

            return StatusCode(201, response);
        }
        catch (SubmitFailureException ex)
        {
            return Problem(title: ex.ErrorCode, statusCode: ex.StatusCode, detail: ex.Message);
        }
    }

    // ── Cursor helpers ────────────────────────────────────────────────────────

    private static string EncodeCursor(DateTimeOffset sortValue, Guid id)
        => $"{sortValue.UtcTicks}|{id:N}";

    private static bool TryDecodeCursor(string? cursor, out long ticks, out Guid id)
    {
        ticks = 0;
        id = Guid.Empty;
        if (string.IsNullOrEmpty(cursor)) return false;
        var parts = cursor.Split('|', 2);
        return parts.Length == 2
            && long.TryParse(parts[0], out ticks)
            && Guid.TryParse(parts[1], out id);
    }

    // ── DELETE /api/v1/shopper/broadcasters/{licenseId} ───────────────────────

    [HttpDelete("{licenseId:guid}")]
    public async Task<IActionResult> Leave(Guid licenseId, CancellationToken ct)
    {
        // 1. Parse shopperId from claims
        var shopperId = GetShopperId();
        if (shopperId is null) return Unauthorized();

        // 2. Load shopper
        var shopper = await _db.Shoppers
            .FirstOrDefaultAsync(s => s.Id == shopperId && s.DeletedAt == null, ct);
        if (shopper is null) return Unauthorized();

        // 3. Find active link
        var link = await _db.ShopperBroadcasterLinks
            .FirstOrDefaultAsync(l => l.ShopperId == shopperId && l.LicenseId == licenseId && l.LeftAt == null, ct);
        if (link is null)
            return Problem(title: "not-linked", statusCode: 404);

        // 4. Soft leave
        link.LeftAt = DateTimeOffset.UtcNow;

        // 5. SaveChanges
        await _db.SaveChangesAsync(ct);

        // 6. Return 204
        return NoContent();
    }
}
