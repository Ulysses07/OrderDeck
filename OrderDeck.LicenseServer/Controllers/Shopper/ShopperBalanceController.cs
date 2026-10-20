using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Services.Auth;
using OrderDeck.LicenseServer.Services.CustomerSync;

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
    private readonly CustomerIdResolver _customerIds;
    private readonly PersonBalance _person;

    public ShopperBalanceController(LicenseDbContext db, CustomerIdResolver customerIds, PersonBalance person)
    {
        _db = db;
        _customerIds = customerIds;
        _person = person;
    }

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

        // Ortak bakiye (spec 2026-10-11 §4.10): kişi çözümü asıl kayıttan başlar. Birleştirme bağlantıyı taşıdığı
        // için bugün çoğunlukla aynı Id; eski bir bağlantı kopyaya işaret edebilir.
        var canonicalId = await _customerIds.CanonicalOfAsync(licenseId, link.WpfCustomerId, ct);

        var row = await _db.CustomerBalances.AsNoTracking()
            .Where(b => b.LicenseId == licenseId && b.WpfCustomerId == canonicalId)
            .Select(b => new { b.Balance, b.UpdatedAt })
            .FirstOrDefaultAsync(ct);
        // UpdatedAt son para hareketidir (M9): kilit dokunuşu "güncellendi" zamanını oynatmaz.
        var lastMovement = await _person.LastMovementAtAsync(licenseId, [canonicalId], ct);
        var balance = new BalanceDto(row?.Balance ?? 0m, lastMovement ?? row?.UpdatedAt ?? DateTimeOffset.UtcNow);

        // Satır kipi: hareketler bağlı satırınki, gruplara toplanmış (§4.10). K4 maskesi: açıklama yalnız grubun
        // çapası bu satırdaysa döner — başka üyeden gelen grupta tutar ve tür görünür, serbest metin gizli.
        var history = await _person.HistoryAsync(licenseId, [canonicalId], take, ct);
        var transactions = history
            .Select(h => new TransactionDto(h.Id, h.Amount, h.Kind, h.OriginalAmount, h.ShippingDeducted,
                h.AnchorWpfCustomerId == canonicalId ? h.Reason : null, h.CreatedAt))
            .ToArray();

        return Ok(new BalanceDetailsResponse(balance, transactions));
    }
}
