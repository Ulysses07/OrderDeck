using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Services.Audit;
using OrderDeck.LicenseServer.Services.Shoppers;

namespace OrderDeck.LicenseServer.Pages.Admin.Shoppers;

/// <summary>
/// Shopper KVKK silme taleplerinin elle işlendiği sayfa. Silme hiçbir zaman
/// otomatik koşmuyor; her satır bir insanın "Sil" demesini bekliyor.
/// </summary>
public class DeletionRequestsModel : PageModel
{
    private const int PageSize = 25;

    private readonly LicenseDbContext _db;
    private readonly ShopperPurgeService _purge;
    private readonly IAuditService _audit;

    public DeletionRequestsModel(
        LicenseDbContext db, ShopperPurgeService purge, IAuditService audit)
    {
        _db = db;
        _purge = purge;
        _audit = audit;
    }

    public sealed record Row(
        Guid Id,
        Guid ShopperId,
        string PhoneAtRequest,
        DateTimeOffset RequestedAt,
        DateTimeOffset? HandledAt,
        string? HandledBy,
        string? Notes,
        int PaymentCount);

    public List<Row> Items { get; private set; } = new();
    public bool ShowHandled { get; private set; }
    public int PendingCount { get; private set; }
    public int CurrentPage { get; private set; }
    public int TotalPages { get; private set; }

    public async Task OnGetAsync(bool handled, int page, CancellationToken ct)
    {
        await LoadAsync(handled, page, ct);
    }

    public async Task<IActionResult> OnPostPurgeAsync(
        Guid id, bool handled, int page, CancellationToken ct)
    {
        var request = await _db.ShopperDeletionRequests
            .FirstOrDefaultAsync(r => r.Id == id, ct);
        if (request is null) return NotFound();

        // İkinci kez basılırsa veriyi tekrar silmeye çalışmıyoruz; ilk silme
        // zaten geri alınamaz ve notu ezmek kanıtı bozardı.
        if (request.HandledAt is not null)
        {
            TempData["Success"] = "Bu talep zaten işlenmiş.";
            return RedirectToPage(new { handled, page });
        }

        var result = await _purge.PurgeAsync(request.ShopperId, ct);
        if (result is null)
        {
            TempData["Success"] = "Shopper kaydı bulunamadı; talep kapatıldı.";
        }

        request.HandledAt = DateTimeOffset.UtcNow;
        // AdminCookie kimliği "username" claim'inde; Identity.Name boş geliyor.
        request.HandledBy = User.FindFirst("username")?.Value
            ?? User.Identity?.Name ?? "admin";
        request.Notes = result?.ToNote() ?? "Shopper kaydı bulunamadı.";
        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(AuditEvents.ShopperPurged, AuditTargets.Shopper,
            request.ShopperId.ToString(),
            details: new
            {
                requestId = request.Id,
                paymentsScrubbed = result?.PaymentsScrubbed ?? 0,
                pdfsDeleted = result?.PdfsDeleted ?? 0,
                projectionsScrubbed = result?.ProjectionsScrubbed ?? 0,
                dependentRowsDeleted = result?.DependentRowsDeleted ?? 0,
            }, ct: ct);

        TempData["Success"] ??= $"Silindi. {request.Notes}";
        return RedirectToPage(new { handled, page });
    }

    private async Task LoadAsync(bool handled, int page, CancellationToken ct)
    {
        ShowHandled = handled;
        CurrentPage = page < 1 ? 1 : page;

        PendingCount = await _db.ShopperDeletionRequests
            .CountAsync(r => r.HandledAt == null, ct);

        var query = _db.ShopperDeletionRequests.AsNoTracking()
            .Where(r => handled ? r.HandledAt != null : r.HandledAt == null);

        var total = await query.CountAsync(ct);
        TotalPages = (int)Math.Ceiling(total / (double)PageSize);

        var rows = await query
            .OrderByDescending(r => r.RequestedAt)
            .Skip((CurrentPage - 1) * PageSize)
            .Take(PageSize)
            .ToListAsync(ct);

        // Ödeme sayısı bilerek gösteriliyor: silme geri alınamaz, admin
        // butona basmadan önce kaç mali kaydın etkileneceğini görmeli.
        var shopperIds = rows.Select(r => r.ShopperId).ToList();
        var paymentCounts = await _db.Payments
            .Where(p => p.ShopperId != null && shopperIds.Contains(p.ShopperId.Value))
            .GroupBy(p => p.ShopperId!.Value)
            .Select(g => new { ShopperId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.ShopperId, x => x.Count, ct);

        Items = rows
            .Select(r => new Row(
                r.Id, r.ShopperId, r.PhoneAtRequest, r.RequestedAt,
                r.HandledAt, r.HandledBy, r.Notes,
                paymentCounts.GetValueOrDefault(r.ShopperId)))
            .ToList();
    }
}
