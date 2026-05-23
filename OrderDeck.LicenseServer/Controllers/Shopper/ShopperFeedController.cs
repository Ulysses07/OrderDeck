using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Services.Pagination;

namespace OrderDeck.LicenseServer.Controllers.Shopper;

/// <summary>
/// Multi-broadcaster yayın akışı (feed). Shopper'ın tüm aktif bağlı
/// yayıncılarının BroadcastPost'larını birleşik, cursor-tabanlı paginated
/// liste olarak döner.
///
/// NOT: Sayfalama basitleştirilmiştir — sıralama IsPinned DESC, CreatedAt DESC,
/// Id DESC. Cursor yalnızca (CreatedAt, Id) üzerinden encode edilir;
/// pinned/unpinned sınırında cursor ile geçiş yoktur (ilk sayfada pinned
/// önce çıkar, sonraki sayfalar ham CreatedAt DESC iter).
/// </summary>
[ApiController]
[Authorize(AuthenticationSchemes = "Bearer-Shopper")]
[Route("api/v1/shopper/feed")]
public sealed class ShopperFeedController : ControllerBase
{
    private readonly LicenseDbContext _db;
    public ShopperFeedController(LicenseDbContext db) => _db = db;

    // ── Response DTOs ─────────────────────────────────────────────────────

    public sealed record FeedItem(
        Guid Id,
        Guid LicenseId,
        string BroadcasterName,
        string Type,
        string? TextBody,
        string? MediaObjectKey,
        string? MediaContentType,
        int? MediaWidth,
        int? MediaHeight,
        bool IsPinned,
        DateTimeOffset CreatedAt,
        DateTimeOffset ExpiresAt);

    public sealed record FeedResponse(FeedItem[] Items, string? NextCursor);

    // ── GET /api/v1/shopper/feed ──────────────────────────────────────────

    [HttpGet]
    public async Task<IActionResult> GetFeed(
        [FromQuery] string? cursor,
        [FromQuery] Guid? licenseId,
        [FromQuery] int limit = 50,
        CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 100);

        // 1. Parse shopperId from claims
        var sub = User.FindFirst("sub")?.Value ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!Guid.TryParse(sub, out var shopperId))
            return Unauthorized();

        var shopper = await _db.Shoppers
            .FirstOrDefaultAsync(s => s.Id == shopperId && s.DeletedAt == null, ct);
        if (shopper is null)
            return Unauthorized();

        // 2. Load active links for this shopper
        var allowedLicenseIds = await _db.ShopperBroadcasterLinks
            .Where(l => l.ShopperId == shopperId && l.LeftAt == null)
            .Select(l => l.LicenseId)
            .ToListAsync(ct);

        // 3. If licenseId param is provided, verify shopper is linked to it
        if (licenseId.HasValue)
        {
            if (!allowedLicenseIds.Contains(licenseId.Value))
                return Problem(title: "forbidden-broadcaster", statusCode: 403);
        }

        // Empty feed — no active links
        if (allowedLicenseIds.Count == 0)
            return Ok(new FeedResponse(Array.Empty<FeedItem>(), null));

        var now = DateTimeOffset.UtcNow;

        // 4. Build base query
        var query = _db.BroadcastPosts
            .Where(p => allowedLicenseIds.Contains(p.LicenseId)
                        && p.DeletedAt == null
                        && p.ExpiresAt > now);

        // 5. Optional licenseId filter
        if (licenseId.HasValue)
            query = query.Where(p => p.LicenseId == licenseId.Value);

        // 6. Parse cursor
        if (TickCursor.TryDecode(cursor, out var cursorTicks, out var cursorId))
        {
            var cursorTs = new DateTimeOffset(cursorTicks, TimeSpan.Zero);
            query = query.Where(p =>
                p.CreatedAt < cursorTs ||
                (p.CreatedAt == cursorTs && p.Id.CompareTo(cursorId) < 0));
        }

        // 7. Sort: IsPinned DESC first (only meaningful on first page without cursor),
        //    then CreatedAt DESC, Id DESC
        var ordered = cursor == null
            ? query.OrderByDescending(p => p.IsPinned)
                   .ThenByDescending(p => p.CreatedAt)
                   .ThenByDescending(p => p.Id)
            : query.OrderByDescending(p => p.CreatedAt)
                   .ThenByDescending(p => p.Id);

        var rows = await ordered
            .Take(limit + 1)
            .Select(p => new
            {
                p.Id,
                p.LicenseId,
                BroadcasterName = p.License.Customer.Name,
                p.Type,
                p.TextBody,
                p.MediaObjectKey,
                p.MediaContentType,
                p.MediaWidth,
                p.MediaHeight,
                p.IsPinned,
                p.CreatedAt,
                p.ExpiresAt,
            })
            .ToListAsync(ct);

        // 8. Cursor logic: if > limit rows, remove last and build nextCursor
        string? nextCursor = null;
        if (rows.Count > limit)
        {
            rows.RemoveAt(rows.Count - 1);
            var last = rows[^1];
            nextCursor = TickCursor.Encode(last.CreatedAt, last.Id);
        }

        var items = rows.Select(r => new FeedItem(
            r.Id,
            r.LicenseId,
            r.BroadcasterName,
            r.Type.ToString().ToLowerInvariant(),
            r.TextBody,
            r.MediaObjectKey,
            r.MediaContentType,
            r.MediaWidth,
            r.MediaHeight,
            r.IsPinned,
            r.CreatedAt,
            r.ExpiresAt)).ToArray();

        return Ok(new FeedResponse(items, nextCursor));
    }

}
