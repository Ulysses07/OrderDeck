using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Auth;

namespace OrderDeck.LicenseServer.Controllers.Panel;

/// <summary>
/// Yayıncı paneli — müşteri bakiye yönetimi (iade/refund kredisi).
///
/// Senaryolar:
///   - Hatalı ürün (refund-full): tam tutar bakiye olarak eklenir
///   - Müşteri iadesi (refund-net): tutar − kargo bakiye olarak eklenir
///   - Yanlış kayıt → reversal: önceki transaction'ı geri al
///   - Manuel ayar: yayıncı serbest miktar +/− ekleme
///
/// Bakiye DÜŞÜLMESİ bu controller'da değil — WPF "Ödeme iste" anında
/// LicensesCustomerBalanceApplyController'a (sonraki) çağrı yapılır.
/// </summary>
[ApiController]
[Route("api/panel/customers/{wpfCustomerId:guid}/balance")]
[Authorize(AuthenticationSchemes = "Bearer-Customer")]
public sealed class PanelCustomerBalanceController : ControllerBase
{
    private readonly LicenseDbContext _db;
    public PanelCustomerBalanceController(LicenseDbContext db) => _db = db;

    public sealed record BalanceDto(
        Guid WpfCustomerId,
        Guid LicenseId,
        decimal Balance,
        DateTimeOffset UpdatedAt);

    public sealed record TransactionDto(
        Guid Id,
        decimal Amount,
        string Kind,
        decimal? OriginalAmount,
        decimal? ShippingDeducted,
        string? Reason,
        Guid? ReversesTransactionId,
        DateTimeOffset CreatedAt);

    public sealed record BalanceDetailsResponse(
        BalanceDto Balance,
        TransactionDto[] Transactions);

    // ── GET — mevcut bakiye + son N transaction ─────────────────────────────

    [HttpGet]
    public async Task<IActionResult> Get(
        Guid wpfCustomerId,
        [FromQuery] int take = 50,
        CancellationToken ct = default)
    {
        if (take < 1 || take > 200) take = 50;

        var customerId = User.GetTenantCustomerId();
        var (licenseId, valid) = await ResolveLicenseAsync(wpfCustomerId, customerId, ct);
        if (!valid) return NotFound();

        var balance = await _db.CustomerBalances
            .Where(b => b.LicenseId == licenseId && b.WpfCustomerId == wpfCustomerId)
            .Select(b => new BalanceDto(b.WpfCustomerId, b.LicenseId, b.Balance, b.UpdatedAt))
            .FirstOrDefaultAsync(ct)
            ?? new BalanceDto(wpfCustomerId, licenseId, 0m, DateTimeOffset.UtcNow);

        var transactions = await _db.CustomerBalanceTransactions
            .Where(t => t.LicenseId == licenseId && t.WpfCustomerId == wpfCustomerId)
            .OrderByDescending(t => t.CreatedAt)
            .Take(take)
            .Select(t => new TransactionDto(
                t.Id, t.Amount, t.Kind,
                t.OriginalAmount, t.ShippingDeducted,
                t.Reason, t.ReversesTransactionId, t.CreatedAt))
            .ToArrayAsync(ct);

        return Ok(new BalanceDetailsResponse(balance, transactions));
    }

    // ── POST refund-full ────────────────────────────────────────────────────

    public sealed record RefundFullRequest(decimal Amount, string? Reason);

    [HttpPost("refund-full")]
    public async Task<IActionResult> RefundFull(
        Guid wpfCustomerId,
        [FromBody] RefundFullRequest req,
        CancellationToken ct)
    {
        if (req.Amount <= 0) return Problem(title: "invalid-amount", statusCode: 400);

        var customerId = User.GetTenantCustomerId();
        var (licenseId, valid) = await ResolveLicenseAsync(wpfCustomerId, customerId, ct);
        if (!valid) return NotFound();

        await ApplyTransactionAsync(licenseId, wpfCustomerId, customerId,
            amount: req.Amount,
            kind: "refund-full",
            originalAmount: req.Amount,
            shippingDeducted: null,
            reason: TrimReason(req.Reason),
            reverses: null,
            ct: ct);

        return Ok();
    }

    // ── POST refund-net (kargo düşülmüş iade) ───────────────────────────────

    public sealed record RefundNetRequest(
        decimal OriginalAmount,
        decimal ShippingDeducted,
        string? Reason);

    [HttpPost("refund-net")]
    public async Task<IActionResult> RefundNet(
        Guid wpfCustomerId,
        [FromBody] RefundNetRequest req,
        CancellationToken ct)
    {
        if (req.OriginalAmount <= 0)
            return Problem(title: "invalid-amount", statusCode: 400);
        if (req.ShippingDeducted < 0 || req.ShippingDeducted >= req.OriginalAmount)
            return Problem(title: "invalid-shipping", statusCode: 400);

        var netAmount = req.OriginalAmount - req.ShippingDeducted;
        var customerId = User.GetTenantCustomerId();
        var (licenseId, valid) = await ResolveLicenseAsync(wpfCustomerId, customerId, ct);
        if (!valid) return NotFound();

        await ApplyTransactionAsync(licenseId, wpfCustomerId, customerId,
            amount: netAmount,
            kind: "refund-net",
            originalAmount: req.OriginalAmount,
            shippingDeducted: req.ShippingDeducted,
            reason: TrimReason(req.Reason),
            reverses: null,
            ct: ct);

        return Ok();
    }

    // ── POST manual-adjustment ──────────────────────────────────────────────

    public sealed record ManualAdjustmentRequest(decimal Amount, string Reason);

    [HttpPost("manual-adjustment")]
    public async Task<IActionResult> ManualAdjustment(
        Guid wpfCustomerId,
        [FromBody] ManualAdjustmentRequest req,
        CancellationToken ct)
    {
        if (req.Amount == 0) return Problem(title: "invalid-amount", statusCode: 400);
        if (string.IsNullOrWhiteSpace(req.Reason))
            return Problem(title: "reason-required", statusCode: 400);

        var customerId = User.GetTenantCustomerId();
        var (licenseId, valid) = await ResolveLicenseAsync(wpfCustomerId, customerId, ct);
        if (!valid) return NotFound();

        // Negatif manuel ayar bakiyeyi sıfırın altına düşürmesin.
        if (req.Amount < 0)
        {
            var current = await GetCurrentBalanceAsync(licenseId, wpfCustomerId, ct);
            if (current + req.Amount < 0)
                return Problem(title: "insufficient-balance", statusCode: 409);
        }

        await ApplyTransactionAsync(licenseId, wpfCustomerId, customerId,
            amount: req.Amount,
            kind: "manual-adjustment",
            originalAmount: null,
            shippingDeducted: null,
            reason: TrimReason(req.Reason),
            reverses: null,
            ct: ct);

        return Ok();
    }

    // ── POST reverse (yanlış kayıt iptal) ───────────────────────────────────

    [HttpPost("transactions/{transactionId:guid}/reverse")]
    public async Task<IActionResult> Reverse(
        Guid wpfCustomerId,
        Guid transactionId,
        CancellationToken ct)
    {
        var customerId = User.GetTenantCustomerId();
        var (licenseId, valid) = await ResolveLicenseAsync(wpfCustomerId, customerId, ct);
        if (!valid) return NotFound();

        var original = await _db.CustomerBalanceTransactions
            .FirstOrDefaultAsync(t => t.Id == transactionId
                && t.LicenseId == licenseId
                && t.WpfCustomerId == wpfCustomerId, ct);
        if (original is null) return NotFound();

        // Daha önce reverse edilmiş mi (aynı transaction'ı reverses olarak gösteren satır var mı)?
        var alreadyReversed = await _db.CustomerBalanceTransactions
            .AnyAsync(t => t.ReversesTransactionId == transactionId, ct);
        if (alreadyReversed) return Problem(title: "already-reversed", statusCode: 409);

        // Reversal balance'ı sıfırın altına düşürmesin.
        var current = await GetCurrentBalanceAsync(licenseId, wpfCustomerId, ct);
        var reverseAmount = -original.Amount;
        if (current + reverseAmount < 0)
            return Problem(title: "insufficient-balance", statusCode: 409);

        await ApplyTransactionAsync(licenseId, wpfCustomerId, customerId,
            amount: reverseAmount,
            kind: "reversal",
            originalAmount: null,
            shippingDeducted: null,
            reason: $"Reverse of {transactionId:N}",
            reverses: transactionId,
            ct: ct);

        return Ok();
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private async Task<(Guid licenseId, bool valid)> ResolveLicenseAsync(
        Guid wpfCustomerId, Guid callerCustomerId, CancellationToken ct)
    {
        // WpfCustomerProjection → License → CustomerId (yayıncı) match.
        // Caller'ın bu projection'a sahip olduğunu doğrula (cross-tenant izolasyon).
        var row = await _db.WpfCustomerProjections
            .Where(p => p.Id == wpfCustomerId && p.License.CustomerId == callerCustomerId)
            .Select(p => (Guid?)p.LicenseId)
            .FirstOrDefaultAsync(ct);
        return row is null ? (Guid.Empty, false) : (row.Value, true);
    }

    private async Task<decimal> GetCurrentBalanceAsync(
        Guid licenseId, Guid wpfCustomerId, CancellationToken ct)
    {
        return await _db.CustomerBalances
            .Where(b => b.LicenseId == licenseId && b.WpfCustomerId == wpfCustomerId)
            .Select(b => (decimal?)b.Balance)
            .FirstOrDefaultAsync(ct) ?? 0m;
    }

    /// <summary>
    /// Ledger satırı yazar + CustomerBalance.Balance'ı günceller (tek transaction).
    /// CustomerBalance yoksa oluşturur. Caller validation'ları yapmış olmalı.
    /// </summary>
    private async Task ApplyTransactionAsync(
        Guid licenseId, Guid wpfCustomerId, Guid createdByCustomerId,
        decimal amount, string kind, decimal? originalAmount, decimal? shippingDeducted,
        string? reason, Guid? reverses, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;

        _db.CustomerBalanceTransactions.Add(new CustomerBalanceTransaction
        {
            Id = Guid.NewGuid(),
            LicenseId = licenseId,
            WpfCustomerId = wpfCustomerId,
            Amount = amount,
            Kind = kind,
            OriginalAmount = originalAmount,
            ShippingDeducted = shippingDeducted,
            Reason = reason,
            ReversesTransactionId = reverses,
            CreatedByCustomerId = createdByCustomerId,
            CreatedAt = now,
        });

        var existing = await _db.CustomerBalances
            .FirstOrDefaultAsync(b => b.LicenseId == licenseId && b.WpfCustomerId == wpfCustomerId, ct);
        if (existing is null)
        {
            _db.CustomerBalances.Add(new CustomerBalance
            {
                Id = Guid.NewGuid(),
                LicenseId = licenseId,
                WpfCustomerId = wpfCustomerId,
                Balance = amount,
                UpdatedAt = now,
            });
        }
        else
        {
            existing.Balance += amount;
            existing.UpdatedAt = now;
        }

        await _db.SaveChangesAsync(ct);
    }

    private static string? TrimReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) return null;
        var trimmed = reason.Trim();
        return trimmed.Length > 500 ? trimmed[..500] : trimmed;
    }
}
