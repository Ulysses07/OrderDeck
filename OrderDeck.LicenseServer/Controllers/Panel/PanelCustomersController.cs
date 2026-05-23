using System.Security.Claims;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
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
        List<CustomerShipmentDto> ActiveShipments,
        // WpfCustomerProjection.Id (Platform+Username match). Müşteri panel
        // balance endpoint'ine bunu geçer. Eşleşme yoksa null — bakiye
        // özelliği bu müşteri için kullanılamaz (henüz shopper app ile bağ kurmamış).
        Guid? WpfCustomerProjectionId);

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

    public sealed record CustomerListItem(
        string Id,
        string? DisplayName,
        string Username,
        string Platform,
        decimal TotalSpent,
        int OrderCount,
        DateTimeOffset LastOrderAt,
        bool IsActive);

    public sealed record CustomerListResponse(
        List<CustomerListItem> Customers,
        string? NextCursor);

    // Internal client-side aggregate row built from the in-memory GroupBy.
    // (The EF projection uses an anonymous type; we re-shape into this record
    // after the materialize step so the sort/cursor helpers have a stable type.)
    private sealed record CustomerAggRow(
        string CustomerId,
        decimal TotalSpent,
        int OrderCount,
        DateTimeOffset LastOrderAt,
        string? DisplayName,
        string Username,
        string Platform);

    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] string? cursor,
        [FromQuery] string? sort,
        [FromQuery] int? activeWithinDays,
        [FromQuery] string? platforms,
        [FromQuery] decimal? minSpent,
        [FromQuery] decimal? maxSpent,
        [FromQuery] int? minOrders,
        [FromQuery] int? maxOrders,
        [FromQuery] int limit = 50,
        CancellationToken ct = default)
    {
        if (limit < 1 || limit > 100) limit = 50;

        var sortMode = sort?.ToLowerInvariant() switch
        {
            null or "" or "lastorder" => "lastOrder",
            "totalspent" => "totalSpent",
            "ordercount" => "orderCount",
            "name" => "name",
            _ => null
        };
        if (sortMode is null)
            return Problem(title: "invalid-sort", statusCode: 400);

        var authCustomerId = User.GetTenantCustomerId();
        var licenseIds = await _db.Licenses
            .Where(l => l.CustomerId == authCustomerId)
            .Select(l => l.Id)
            .ToListAsync(ct);

        if (licenseIds.Count == 0)
            return Ok(new CustomerListResponse(new List<CustomerListItem>(), null));

        var platformList = platforms?
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => p.ToLowerInvariant())
            .ToList();

        // ─── Pass 1: server-side aggregate over real sales ────────────────
        // Real-sale: printed + not cancelled + not shipping fee + not tentative.
        // Aggregate'i tek round-trip'te DB'de GroupBy ile çıkar. Ghost customer'lar
        // (sadece iptal/kargo/tentative satırı olanlar) zaten Where filtresinin
        // dışında kaldığı için elenir — eski "OrderCount > 0" guard'a gerek yok.
        var realSalesQuery = _db.Orders.Where(o =>
            licenseIds.Contains(o.LicenseId)
            && o.PrintedAt != null
            && o.CancelledAt == null
            && !o.IsShippingFee
            && !o.IsTentativeBackup);

        if (platformList is { Count: > 0 })
            realSalesQuery = realSalesQuery.Where(o => platformList.Contains(o.Platform.ToLower()));

        var aggregates = await realSalesQuery
            .GroupBy(o => o.CustomerId)
            .Select(g => new
            {
                CustomerId = g.Key,
                OrderCount = g.Count(),
                TotalSpent = g.Sum(o => o.Price),
                LastOrderAt = g.Max(o => o.AddedAt)
            })
            .ToListAsync(ct);

        // Identity (DisplayName / Username / Platform) Pass 2'de doldurulur.
        // Numeric sortlar için: filter + sort + cursor + limit → shortlist → identity.
        // Name sort için: identity'i tüm filtered aggregate'lar için yükle (sıralama
        // adından önce isim gerekli) → sort + cursor + limit.
        var aggregateRows = aggregates
            .Select(a => new CustomerAggRow(
                a.CustomerId,
                a.TotalSpent,
                a.OrderCount,
                a.LastOrderAt,
                DisplayName: null,
                Username: string.Empty,
                Platform: string.Empty))
            .ToList();

        // Filters mirror the same predicates that would run server-side.
        IEnumerable<CustomerAggRow> filtered = aggregateRows;
        if (activeWithinDays.HasValue)
        {
            var cutoff = DateTimeOffset.UtcNow.AddDays(-activeWithinDays.Value);
            filtered = filtered.Where(c => c.LastOrderAt > cutoff);
        }
        if (minSpent.HasValue)
            filtered = filtered.Where(c => c.TotalSpent >= minSpent.Value);
        if (maxSpent.HasValue)
            filtered = filtered.Where(c => c.TotalSpent <= maxSpent.Value);
        if (minOrders.HasValue)
            filtered = filtered.Where(c => c.OrderCount >= minOrders.Value);
        if (maxOrders.HasValue)
            filtered = filtered.Where(c => c.OrderCount <= maxOrders.Value);

        var filteredList = filtered.ToList();

        var (cursorSortRaw, cursorCustomerId) = ParseCursor(cursor);

        List<CustomerAggRow> rows;
        if (sortMode == "name")
        {
            // ─── Pass 2 (name sort): identity for all filtered customers ──
            var allIds = filteredList.Select(r => r.CustomerId).ToList();
            var identities = await LoadLatestIdentitiesAsync(licenseIds, allIds, ct);
            var withIdentity = filteredList.Select(r =>
                identities.TryGetValue(r.CustomerId, out var id)
                    ? r with { DisplayName = id.DisplayName, Username = id.Username, Platform = id.Platform }
                    : r).ToList();
            rows = ApplySortAndCursorClient(withIdentity, sortMode, cursorSortRaw, cursorCustomerId, limit);
        }
        else
        {
            // ─── Numeric sort: shortlist first, identity for shortlist only ──
            var shortlisted = ApplySortAndCursorClient(filteredList, sortMode, cursorSortRaw, cursorCustomerId, limit);
            var shortlistIds = shortlisted.Select(r => r.CustomerId).ToList();
            var identities = await LoadLatestIdentitiesAsync(licenseIds, shortlistIds, ct);
            rows = shortlisted.Select(r =>
                identities.TryGetValue(r.CustomerId, out var id)
                    ? r with { DisplayName = id.DisplayName, Username = id.Username, Platform = id.Platform }
                    : r).ToList();
        }

        string? nextCursor = null;
        if (rows.Count > limit)
        {
            var last = rows[limit - 1];
            var lastSortValue = sortMode switch
            {
                "lastOrder" => last.LastOrderAt.ToString("O"),
                "totalSpent" => last.TotalSpent.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "orderCount" => last.OrderCount.ToString(),
                "name" => last.DisplayName ?? last.Username ?? "",
                _ => throw new InvalidOperationException()
            };
            nextCursor = $"{lastSortValue}|{last.CustomerId}";
            rows = rows.Take(limit).ToList();
        }

        var activeCutoff = DateTimeOffset.UtcNow.AddDays(-30);
        var items = rows.Select(r => new CustomerListItem(
            r.CustomerId,
            r.DisplayName,
            r.Username,
            r.Platform,
            r.TotalSpent,
            r.OrderCount,
            r.LastOrderAt,
            r.LastOrderAt > activeCutoff)).ToList();

        return Ok(new CustomerListResponse(items, nextCursor));
    }

    /// <summary>
    /// Pass 2: Verilen müşteri ID listesi için identity bilgisi
    /// (DisplayName / Username / Platform) yükler. Her müşteri için en güncel
    /// UpdatedAt'li sipariş satırını "kimlik kaynağı" olarak seçer. Tek SQL
    /// round-trip; in-memory grouplama ile her müşteri için tek kayıt çıkar.
    /// </summary>
    private async Task<Dictionary<string, (string? DisplayName, string Username, string Platform)>>
        LoadLatestIdentitiesAsync(List<Guid> licenseIds, List<string> customerIds, CancellationToken ct)
    {
        if (customerIds.Count == 0)
            return new Dictionary<string, (string?, string, string)>();

        var rows = await _db.Orders
            .Where(o => licenseIds.Contains(o.LicenseId) && customerIds.Contains(o.CustomerId))
            .Select(o => new { o.CustomerId, o.UpdatedAt, o.DisplayName, o.Username, o.Platform })
            .ToListAsync(ct);

        return rows
            .GroupBy(r => r.CustomerId)
            .ToDictionary(
                g => g.Key,
                g =>
                {
                    var latest = g.OrderByDescending(r => r.UpdatedAt).First();
                    return ((string?)latest.DisplayName, latest.Username, latest.Platform);
                });
    }

    private static (string? sortValue, string? customerId) ParseCursor(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor)) return (null, null);
        var parts = cursor.Split('|', 2);
        if (parts.Length != 2) return (null, null);
        return (parts[0], parts[1]);
    }

    private static List<CustomerAggRow> ApplySortAndCursorClient(
        List<CustomerAggRow> rows, string sortMode, string? cursorSortRaw,
        string? cursorCustomerId, int limit)
    {
        IEnumerable<CustomerAggRow> seq = rows;
        switch (sortMode)
        {
            case "lastOrder":
                if (cursorSortRaw != null && cursorCustomerId != null
                    && DateTimeOffset.TryParse(cursorSortRaw, out var lvCv))
                {
                    seq = seq.Where(c =>
                        c.LastOrderAt < lvCv ||
                        (c.LastOrderAt == lvCv && string.Compare(c.CustomerId, cursorCustomerId, StringComparison.Ordinal) < 0));
                }
                return seq
                    .OrderByDescending(c => c.LastOrderAt)
                    .ThenByDescending(c => c.CustomerId, StringComparer.Ordinal)
                    .Take(limit + 1)
                    .ToList();
            case "totalSpent":
                if (cursorSortRaw != null && cursorCustomerId != null
                    && decimal.TryParse(cursorSortRaw, System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out var tsCv))
                {
                    seq = seq.Where(c =>
                        c.TotalSpent < tsCv ||
                        (c.TotalSpent == tsCv && string.Compare(c.CustomerId, cursorCustomerId, StringComparison.Ordinal) < 0));
                }
                return seq
                    .OrderByDescending(c => c.TotalSpent)
                    .ThenByDescending(c => c.CustomerId, StringComparer.Ordinal)
                    .Take(limit + 1)
                    .ToList();
            case "orderCount":
                if (cursorSortRaw != null && cursorCustomerId != null
                    && int.TryParse(cursorSortRaw, out var ocCv))
                {
                    seq = seq.Where(c =>
                        c.OrderCount < ocCv ||
                        (c.OrderCount == ocCv && string.Compare(c.CustomerId, cursorCustomerId, StringComparison.Ordinal) < 0));
                }
                return seq
                    .OrderByDescending(c => c.OrderCount)
                    .ThenByDescending(c => c.CustomerId, StringComparer.Ordinal)
                    .Take(limit + 1)
                    .ToList();
            case "name":
                if (cursorSortRaw != null && cursorCustomerId != null)
                {
                    var cv = cursorSortRaw;
                    seq = seq.Where(c =>
                        string.Compare(c.DisplayName ?? c.Username ?? "", cv, StringComparison.Ordinal) > 0 ||
                        (string.Compare(c.DisplayName ?? c.Username ?? "", cv, StringComparison.Ordinal) == 0
                            && string.Compare(c.CustomerId, cursorCustomerId, StringComparison.Ordinal) > 0));
                }
                return seq
                    .OrderBy(c => c.DisplayName ?? c.Username ?? "", StringComparer.Ordinal)
                    .ThenBy(c => c.CustomerId, StringComparer.Ordinal)
                    .Take(limit + 1)
                    .ToList();
            default:
                throw new InvalidOperationException($"Unsupported sortMode: {sortMode}");
        }
    }

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

        // WpfCustomerProjection lookup — Platform+Username match. Bakiye
        // endpoint'i bunun ID'sine ihtiyaç duyuyor. Müşteri shopper app ile
        // bağ kurmamışsa eşleşme olmayabilir → null.
        var platformLower = identity.Platform.ToLowerInvariant();
        var wpfProjectionId = await _db.WpfCustomerProjections
            .Where(p => licenseIds.Contains(p.LicenseId)
                && p.Platform == platformLower
                && p.Username == identity.Username)
            .Select(p => (Guid?)p.Id)
            .FirstOrDefaultAsync(ct);

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
            activeShipments,
            wpfProjectionId));
    }

}
