using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Services.Auth;
using OrderDeck.LicenseServer.Services.Sync;

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

    /// <param name="PurgedAt">
    /// KVKK silme talebiyle sunucudaki kişisel alanlar temizlendiyse dolu.
    /// Bu satırlar yanıttan ELENMİYOR, işaretlenerek gönderiliyor: yayıncının
    /// kendi bilgisayarındaki kopyayı ancak bu işaret temizletebilir (WPF'in
    /// sunucudan silme haberi alacağı başka bir kanal yok). Elenselerdi silme
    /// yayıncının diskinde sonsuza kadar kalırdı.
    ///
    /// Alanın SONA eklenmesi kasıtlı — sahadaki eski kurulumlar (v0.8.0 ve
    /// öncesi) bilinmeyen JSON alanını yok sayar; bu ekleme onları kırmaz,
    /// yalnızca temizlik dalını çalıştıramazlar.
    /// </param>
    public sealed record WpfCustomerPullItem(
        Guid Id,
        string Platform,
        string Username,
        string? FullName,
        string? Phone,
        string? Address,
        DateTimeOffset UpdatedAt,
        DateTimeOffset? PurgedAt);

    /// <summary>
    /// Bileşik imleç (<c>since</c> + <c>sinceId</c>), kararlılık ufkunun
    /// altında; gerekçe <see cref="ReverseSyncCursor"/>'da. WPF bir sonraki
    /// imleci sayfanın son satırından okur — yanıt sırası <c>(UpdatedAt, Id)</c>.
    /// take default 100, max 500.
    /// </summary>
    [HttpGet("since")]
    public async Task<IActionResult> Since(
        Guid licenseId,
        [FromQuery] DateTimeOffset since,
        [FromQuery] Guid sinceId = default,
        [FromQuery] int take = 100,
        CancellationToken ct = default)
    {
        var customerId = User.GetTenantCustomerId();
        var ownsLicense = await _db.Licenses
            .AnyAsync(l => l.Id == licenseId && l.CustomerId == customerId, ct);
        if (!ownsLicense) return NotFound();

        take = Math.Clamp(take, 1, 500);
        var horizon = ReverseSyncCursor.Horizon();

        var rows = await _db.WpfCustomerProjections
            .Where(p => p.LicenseId == licenseId
                        && p.UpdatedAt <= horizon
                        && (p.UpdatedAt > since
                            || (p.UpdatedAt == since && p.Id.CompareTo(sinceId) > 0)))
            .OrderBy(p => p.UpdatedAt).ThenBy(p => p.Id)
            .Take(take)
            .Select(p => new WpfCustomerPullItem(
                p.Id, p.Platform, p.Username,
                p.FullName, p.Phone, p.Address,
                p.UpdatedAt, p.PurgedAt))
            .ToListAsync(ct);

        return Ok(rows);
    }
}
