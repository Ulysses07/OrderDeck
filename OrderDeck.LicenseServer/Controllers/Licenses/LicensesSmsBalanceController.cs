using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Services.Auth;
using OrderDeck.LicenseServer.Services.Sms;

namespace OrderDeck.LicenseServer.Controllers.Licenses;

/// <summary>
/// Yayıncının (WPF) kendi SMS kredi bakiyesini okuması. Yükleme admin tarafında
/// (<see cref="AdminSmsController"/>); burası salt-okunur.
/// </summary>
[ApiController]
[Route("api/v1/licenses/{licenseId:guid}/sms")]
[Authorize(AuthenticationSchemes = "Bearer-Customer")]
public sealed class LicensesSmsBalanceController : ControllerBase
{
    private readonly LicenseDbContext _db;
    private readonly LicenseSmsBalanceService _balance;

    public LicensesSmsBalanceController(LicenseDbContext db, LicenseSmsBalanceService balance)
    {
        _db = db;
        _balance = balance;
    }

    public sealed record BalanceResponse(int CreditsRemaining, DateTimeOffset UpdatedAt);

    [HttpGet("balance")]
    public async Task<IActionResult> Balance(Guid licenseId, CancellationToken ct)
    {
        var customerId = User.GetTenantCustomerId();
        var ownsLicense = await _db.Licenses
            .AnyAsync(l => l.Id == licenseId && l.CustomerId == customerId, ct);
        if (!ownsLicense) return NotFound();

        var info = await _balance.GetAsync(licenseId, ct);
        return Ok(new BalanceResponse(info.CreditsRemaining, info.UpdatedAt));
    }
}
