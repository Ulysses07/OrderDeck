using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Services.Auth;

namespace OrderDeck.LicenseServer.Controllers.Shopper;

/// <summary>
/// Shopper'ın yayıncı bazlı bakiyesini + transaction geçmişini sorgular.
/// Yayıncı tarafıyla (PanelCustomerBalanceController) aynı tabloları okur,
/// shopper-tenancy (ShopperBroadcasterLink.WpfCustomerId) üzerinden eşleşir.
/// </summary>
[ApiController]
[Authorize(AuthenticationSchemes = "Bearer-Shopper")]
[Route("api/v1/shopper/broadcasters/{licenseId:guid}/balance")]
public sealed class ShopperBalanceController : ControllerBase
{
    private readonly LicenseDbContext _db;
    public ShopperBalanceController(LicenseDbContext db) => _db = db;

    public sealed record BalanceDto(decimal Balance, DateTimeOffset UpdatedAt);

    public sealed record TransactionDto(
        Guid Id,
        decimal Amount,
        string Kind,
        decimal? OriginalAmount,
        decimal? ShippingDeducted,
        string? Reason,
        DateTimeOffset CreatedAt);

    public sealed record BalanceDetailsResponse(
        BalanceDto Balance,
        TransactionDto[] Transactions);

    [HttpGet]
    public async Task<IActionResult> Get(
        Guid licenseId,
        [FromQuery] int take = 50,
        CancellationToken ct = default)
    {
        if (take < 1 || take > 200) take = 50;

        var shopperId = User.GetShopperId();
        if (shopperId is null) return Unauthorized();

        // Shopper bu license'a aktif olarak bağlı olmalı + WpfCustomerId
        // doldurulmuş olmalı (eşleştirilmiş müşteri).
        var link = await _db.ShopperBroadcasterLinks
            .Where(l => l.ShopperId == shopperId.Value
                && l.LicenseId == licenseId
                && l.LeftAt == null
                && l.WpfCustomerId != null)
            .Select(l => new { WpfCustomerId = l.WpfCustomerId!.Value })
            .FirstOrDefaultAsync(ct);
        if (link is null) return NotFound();

        var balance = await _db.CustomerBalances
            .Where(b => b.LicenseId == licenseId && b.WpfCustomerId == link.WpfCustomerId)
            .Select(b => new BalanceDto(b.Balance, b.UpdatedAt))
            .FirstOrDefaultAsync(ct)
            ?? new BalanceDto(0m, DateTimeOffset.UtcNow);

        var transactions = await _db.CustomerBalanceTransactions
            .Where(t => t.LicenseId == licenseId && t.WpfCustomerId == link.WpfCustomerId)
            .OrderByDescending(t => t.CreatedAt)
            .Take(take)
            .Select(t => new TransactionDto(
                t.Id, t.Amount, t.Kind,
                t.OriginalAmount, t.ShippingDeducted,
                t.Reason, t.CreatedAt))
            .ToArrayAsync(ct);

        return Ok(new BalanceDetailsResponse(balance, transactions));
    }
}
