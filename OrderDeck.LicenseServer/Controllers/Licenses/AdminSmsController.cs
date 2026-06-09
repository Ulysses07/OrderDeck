using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Services.Sms;

namespace OrderDeck.LicenseServer.Controllers.Licenses;

/// <summary>
/// Admin SMS kredi yönetimi (reseller modeli — krediyi biz satıyoruz). Yayıncı
/// ödeyince admin elle bakiye yükler. Kredi birimi = SMS segmenti.
/// </summary>
[ApiController]
[Route("api/v1/admin/licenses/{licenseId:guid}/sms")]
[Authorize(AuthenticationSchemes = "Bearer-Admin")]
public sealed class AdminSmsController : ControllerBase
{
    private readonly LicenseDbContext _db;
    private readonly LicenseSmsBalanceService _balance;

    public AdminSmsController(LicenseDbContext db, LicenseSmsBalanceService balance)
    {
        _db = db;
        _balance = balance;
    }

    public sealed record TopupRequest(int Credits, string? Reason);
    public sealed record TopupResponse(int CreditsRemaining, DateTimeOffset UpdatedAt);

    [HttpPost("topup")]
    public async Task<IActionResult> Topup(
        Guid licenseId, [FromBody] TopupRequest req, CancellationToken ct)
    {
        if (req.Credits <= 0)
            return Problem(title: "invalid-credits", statusCode: 400, detail: "Credits must be > 0");

        var exists = await _db.Licenses.AnyAsync(l => l.Id == licenseId, ct);
        if (!exists) return NotFound();

        var remaining = await _balance.ApplyAsync(
            licenseId, req.Credits, "purchase", req.Reason, createdByCustomerId: null, ct);
        await _db.SaveChangesAsync(ct);

        return Ok(new TopupResponse(remaining, DateTimeOffset.UtcNow));
    }

    public sealed record TransactionItem(
        int Amount, string Kind, string? Reason, DateTimeOffset CreatedAt);
    public sealed record BalanceResponse(
        int CreditsRemaining, DateTimeOffset UpdatedAt, IReadOnlyList<TransactionItem> RecentTransactions);

    [HttpGet("balance")]
    public async Task<IActionResult> Balance(Guid licenseId, CancellationToken ct)
    {
        var exists = await _db.Licenses.AnyAsync(l => l.Id == licenseId, ct);
        if (!exists) return NotFound();

        var info = await _balance.GetAsync(licenseId, ct);
        var recent = await _db.LicenseSmsTransactions
            .Where(t => t.LicenseId == licenseId)
            .OrderByDescending(t => t.CreatedAt)
            .Take(20)
            .Select(t => new TransactionItem(t.Amount, t.Kind, t.Reason, t.CreatedAt))
            .ToListAsync(ct);

        return Ok(new BalanceResponse(info.CreditsRemaining, info.UpdatedAt, recent));
    }
}
