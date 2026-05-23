using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Auth;

namespace OrderDeck.LicenseServer.Controllers.Licenses;

/// <summary>
/// WPF "Ödeme iste" akışı için bakiye uygulama endpoint'i. Yayıncı WhatsApp
/// mesajı atmadan önce burayı çağırır:
///   - "Bu müşterinin şu kadar bakiyesi var mı, ne kadarı uygulanabilir?" sorusu
///   - Onay → ledger'a purchase-deduction düşülür
///
/// İstemci (WPF) önce <c>preview</c> ile balance'ı çeker, mesajı oluştururken
/// gösterir, kullanıcı onaylayıp WhatsApp Desktop açıldıktan sonra <c>apply</c>
/// ile commit eder.
/// </summary>
[ApiController]
[Route("api/v1/licenses/{licenseId:guid}/customer-balance")]
[Authorize(AuthenticationSchemes = "Bearer-Customer")]
public sealed class LicensesCustomerBalanceApplyController : ControllerBase
{
    private readonly LicenseDbContext _db;
    public LicensesCustomerBalanceApplyController(LicenseDbContext db) => _db = db;

    public sealed record PreviewQuery(Guid WpfCustomerId);

    public sealed record PreviewResponse(
        Guid WpfCustomerId,
        decimal Balance,
        DateTimeOffset UpdatedAt);

    // ── GET preview ─────────────────────────────────────────────────────────

    [HttpGet("preview")]
    public async Task<IActionResult> Preview(
        Guid licenseId,
        [FromQuery] Guid wpfCustomerId,
        CancellationToken ct)
    {
        if (!await OwnsLicenseAsync(licenseId, ct)) return NotFound();

        var row = await _db.CustomerBalances
            .Where(b => b.LicenseId == licenseId && b.WpfCustomerId == wpfCustomerId)
            .Select(b => new PreviewResponse(b.WpfCustomerId, b.Balance, b.UpdatedAt))
            .FirstOrDefaultAsync(ct);

        return Ok(row ?? new PreviewResponse(wpfCustomerId, 0m, DateTimeOffset.UtcNow));
    }

    // ── POST apply ──────────────────────────────────────────────────────────

    public sealed record ApplyRequest(
        Guid WpfCustomerId,
        decimal Amount,
        decimal ProductTotal);

    public sealed record ApplyResponse(
        Guid TransactionId,
        decimal AppliedAmount,
        decimal RemainingBalance);

    [HttpPost("apply")]
    public async Task<IActionResult> Apply(
        Guid licenseId,
        [FromBody] ApplyRequest req,
        CancellationToken ct)
    {
        if (req.Amount <= 0) return Problem(title: "invalid-amount", statusCode: 400);
        if (!await OwnsLicenseAsync(licenseId, ct)) return NotFound();

        var balance = await _db.CustomerBalances
            .FirstOrDefaultAsync(b => b.LicenseId == licenseId
                && b.WpfCustomerId == req.WpfCustomerId, ct);
        if (balance is null || balance.Balance <= 0)
            return Problem(title: "no-balance", statusCode: 409);

        // İstenen tutar bakiyeden fazla olamaz; sipariş tutarından da fazla
        // olamaz (mantıksızlık).
        var appliedAmount = Math.Min(Math.Min(req.Amount, balance.Balance), req.ProductTotal);
        if (appliedAmount <= 0)
            return Problem(title: "nothing-to-apply", statusCode: 409);

        var customerId = User.GetTenantCustomerId();
        var now = DateTimeOffset.UtcNow;
        var txId = Guid.NewGuid();

        _db.CustomerBalanceTransactions.Add(new CustomerBalanceTransaction
        {
            Id = txId,
            LicenseId = licenseId,
            WpfCustomerId = req.WpfCustomerId,
            Amount = -appliedAmount,
            Kind = "purchase-deduction",
            OriginalAmount = req.ProductTotal,
            Reason = null,
            CreatedByCustomerId = customerId,
            CreatedAt = now,
        });

        balance.Balance -= appliedAmount;
        balance.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);

        return Ok(new ApplyResponse(txId, appliedAmount, balance.Balance));
    }

    private async Task<bool> OwnsLicenseAsync(Guid licenseId, CancellationToken ct)
    {
        var callerId = User.GetTenantCustomerId();
        return await _db.Licenses
            .AnyAsync(l => l.Id == licenseId && l.CustomerId == callerId, ct);
    }
}
