using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Services.Auth;

namespace OrderDeck.LicenseServer.Controllers.Licenses;

/// <summary>
/// WPF App'in shopper-registered customers'ı (otomatik oluşturulan
/// WpfCustomerProjection rows) çekmesi için. Mevcut sync endpoint
/// WPF → server outbound; bu da inbound (server → WPF) pull.
/// </summary>
[ApiController]
[Route("api/v1/licenses/{licenseId:guid}/wpf-customers")]
[Authorize(AuthenticationSchemes = "Bearer-Customer")]
public sealed class LicensesWpfCustomersPullController : ControllerBase
{
    private readonly LicenseDbContext _db;
    public LicensesWpfCustomersPullController(LicenseDbContext db) => _db = db;

    public sealed record WpfCustomerPullItem(
        Guid Id,
        string Platform,
        string Username,
        string? FullName,
        string? Phone,
        string? Address,
        DateTimeOffset UpdatedAt);

    /// <summary>
    /// since cursor (UpdatedAt). WPF kendi watermark'ını ilerletir.
    /// take default 100, max 500.
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

        var rows = await _db.WpfCustomerProjections
            .Where(p => p.LicenseId == licenseId && p.UpdatedAt > since)
            .OrderBy(p => p.UpdatedAt)
            .Take(take)
            .Select(p => new WpfCustomerPullItem(
                p.Id, p.Platform, p.Username,
                p.FullName, p.Phone, p.Address,
                p.UpdatedAt))
            .ToListAsync(ct);

        return Ok(rows);
    }
}
