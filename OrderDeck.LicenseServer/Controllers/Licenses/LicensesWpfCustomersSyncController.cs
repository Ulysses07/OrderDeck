using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Auth;

namespace OrderDeck.LicenseServer.Controllers.Licenses;

/// <summary>
/// WPF App'in lokal Customer kayıtlarını LicenseServer'a periyodik bulk sync.
/// Server-side WpfCustomerProjection tablosuna upsert. Sipariş eşleşmesi:
/// shopper-app kullanıcısı bir yayıncıya bağlanırken (LicenseId, Platform,
/// Username) ile match yapılır; match retroactive olarak burada da çalıştırılır
/// (sync sırasında yeni eşleşen link.WpfCustomerId güncellenir).
/// </summary>
[ApiController]
[Authorize(AuthenticationSchemes = "Bearer-Customer")]
[Route("api/v1/licenses/{licenseId:guid}/wpf-customers")]
public sealed class LicensesWpfCustomersSyncController : ControllerBase
{
    private readonly LicenseDbContext _db;
    public LicensesWpfCustomersSyncController(LicenseDbContext db) => _db = db;

    public sealed record SyncItem(
        Guid Id,
        string Platform,
        string Username,
        string? FullName,
        string? Phone,
        string? Address,
        DateTimeOffset UpdatedAt);

    public sealed record SyncRequest(List<SyncItem> Customers);

    public sealed record SyncResponse(int Synced, int RetroactiveMatches);

    [HttpPost("sync")]
    public async Task<IActionResult> Sync(Guid licenseId, [FromBody] SyncRequest req, CancellationToken ct)
    {
        var customerId = User.GetTenantCustomerId();
        var ownsLicense = await _db.Licenses
            .AnyAsync(l => l.Id == licenseId && l.CustomerId == customerId, ct);
        if (!ownsLicense) return NotFound();

        if (req?.Customers is null || req.Customers.Count == 0)
            return Ok(new SyncResponse(0, 0));

        if (req.Customers.Count > 500)
            return Problem(title: "batch-too-large", statusCode: 400, detail: "Max 500 customers per batch");

        // Validate input items minimally
        foreach (var c in req.Customers)
        {
            if (string.IsNullOrWhiteSpace(c.Platform) || c.Platform.Length > 32)
                return Problem(title: "invalid-platform", statusCode: 400);
            if (string.IsNullOrWhiteSpace(c.Username) || c.Username.Length > 128)
                return Problem(title: "invalid-username", statusCode: 400);
        }

        var ids = req.Customers.Select(c => c.Id).ToList();
        var existing = await _db.WpfCustomerProjections
            .Where(p => p.LicenseId == licenseId && ids.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, ct);

        var synced = 0;
        foreach (var item in req.Customers)
        {
            if (existing.TryGetValue(item.Id, out var current))
            {
                current.Platform = item.Platform.ToLowerInvariant();
                current.Username = item.Username;
                current.FullName = item.FullName;
                current.Phone = item.Phone;
                current.Address = item.Address;
                current.UpdatedAt = item.UpdatedAt;
            }
            else
            {
                _db.WpfCustomerProjections.Add(new WpfCustomerProjection
                {
                    Id = item.Id,
                    LicenseId = licenseId,
                    Platform = item.Platform.ToLowerInvariant(),
                    Username = item.Username,
                    FullName = item.FullName,
                    Phone = item.Phone,
                    Address = item.Address,
                    UpdatedAt = item.UpdatedAt,
                });
            }
            synced++;
        }

        await _db.SaveChangesAsync(ct);

        // Retroactive match: for newly-synced (or updated) projections, find any
        // ShopperBroadcasterLink with matching (LicenseId, Platform, Username) where
        // WpfCustomerId is null, and set it. Drive-by — avoids needing a cron job.
        var retroactiveMatches = 0;
        foreach (var item in req.Customers)
        {
            var platformLower = item.Platform.ToLowerInvariant();
            var unmatchedLinks = await _db.ShopperBroadcasterLinks
                .Where(l => l.LicenseId == licenseId
                    && l.WpfCustomerId == null
                    && l.LeftAt == null
                    && l.Platform == platformLower
                    && l.Username == item.Username)
                .ToListAsync(ct);
            foreach (var link in unmatchedLinks)
            {
                link.WpfCustomerId = item.Id;
                retroactiveMatches++;
            }
        }
        if (retroactiveMatches > 0)
            await _db.SaveChangesAsync(ct);

        return Ok(new SyncResponse(synced, retroactiveMatches));
    }
}
