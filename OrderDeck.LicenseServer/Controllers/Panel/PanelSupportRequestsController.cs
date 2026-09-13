using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Services.Auth;
using OrderDeck.LicenseServer.Services.Sms;

namespace OrderDeck.LicenseServer.Controllers.Panel;

/// <summary>
/// Yayıncı paneli — shopper destek talepleri (Faz 0b-1: forgot-password).
/// Shopper "Parolamı unuttum" derse server <see cref="Domain.ShopperSupportRequest"/>
/// satırı oluşturur (Bağlı her aktif yayıncı için bir tane). Yayıncı bu listeyi
/// görür ve doğrulanmış telefon kanalına kurtarma kodu gönderilmesini başlatır.
/// Yayıncı global shopper parolasını veya normal erişim token'ını alamaz.
/// </summary>
[ApiController]
[Route("api/panel/support-requests")]
[Authorize(AuthenticationSchemes = "Bearer-Customer")]
public sealed class PanelSupportRequestsController : ControllerBase
{
    private readonly LicenseDbContext _db;
    private readonly PasswordResetCodeService _resetCodes;
    private readonly ShopperRefreshTokenService _refresh;
    private readonly ISmsSender _sms;
    private readonly ILogger<PanelSupportRequestsController> _log;

    public PanelSupportRequestsController(
        LicenseDbContext db,
        PasswordResetCodeService resetCodes,
        ShopperRefreshTokenService refresh,
        ISmsSender sms,
        ILogger<PanelSupportRequestsController> log)
    {
        _db = db;
        _resetCodes = resetCodes;
        _refresh = refresh;
        _sms = sms;
        _log = log;
    }

    public sealed record SupportRequestDto(
        Guid Id,
        Guid LicenseId,
        Guid ShopperId,
        string ShopperName,
        string ShopperPhone,
        string Kind,
        DateTimeOffset CreatedAt,
        DateTimeOffset? ResolvedAt);

    // ── GET — bekleyen + son N tamamlanan ──────────────────────────────────

    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] bool includeResolved = false,
        [FromQuery] int take = 50,
        CancellationToken ct = default)
    {
        if (take < 1 || take > 200) take = 50;

        var customerId = User.GetTenantCustomerId();
        var licenseIds = await _db.Licenses
            .Where(l => l.CustomerId == customerId)
            .Select(l => l.Id)
            .ToListAsync(ct);
        if (licenseIds.Count == 0)
            return Ok(Array.Empty<SupportRequestDto>());

        var query = _db.ShopperSupportRequests
            .Where(r => licenseIds.Contains(r.LicenseId));
        if (!includeResolved)
            query = query.Where(r => r.ResolvedAt == null);

        var rows = await query
            .OrderByDescending(r => r.CreatedAt)
            .Take(take)
            .Select(r => new SupportRequestDto(
                r.Id, r.LicenseId, r.ShopperId,
                r.Shopper.FullName, r.Shopper.Phone,
                r.Kind, r.CreatedAt, r.ResolvedAt))
            .ToListAsync(ct);
        return Ok(rows);
    }

    // ── POST — doğrulanmış telefon kanalına kurtarma kodu gönder ────────────

    public sealed record IssueTempPasswordResponse(string? TempPassword, string Status);

    [HttpPost("{id:guid}/issue-temp-password")]
    public async Task<IActionResult> IssueTempPassword(Guid id, CancellationToken ct)
    {
        var customerId = User.GetTenantCustomerId();
        var licenseIds = await _db.Licenses
            .Where(l => l.CustomerId == customerId)
            .Select(l => l.Id)
            .ToListAsync(ct);

        var request = await _db.ShopperSupportRequests
            .Include(r => r.Shopper)
            .FirstOrDefaultAsync(r => r.Id == id && licenseIds.Contains(r.LicenseId), ct);
        if (request is null) return NotFound();
        if (request.Kind != "forgot-password")
            return Problem(title: "unsupported-kind", statusCode: 400);
        if (request.ResolvedAt is not null)
            return Problem(title: "already-resolved", statusCode: 409);
        if (request.Shopper.DeletedAt is not null)
            return Problem(title: "shopper-deleted", statusCode: 409);

        var issued = await _resetCodes.IssueWithHandleAsync(
            request.Shopper,
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            ct);
        if (issued is null)
            return Problem(title: "recovery-unavailable", statusCode: 429);

        var message = $"OrderDeck dogrulama kodunuz: {issued.Code}. Kod 10 dakika gecerli.";
        try
        {
            await _sms.SendAsync(
                request.Shopper.Phone, message, SmsKind.Transactional, ct);
        }
        catch (Exception ex)
        {
            await _resetCodes.DiscardAsync(issued.Id, CancellationToken.None);
            if (ct.IsCancellationRequested)
                throw;
            _log.LogWarning(ex,
                "Support recovery SMS failed for shopper={ShopperId}",
                request.ShopperId);
            return Problem(title: "recovery-delivery-failed", statusCode: 503);
        }

        var now = DateTimeOffset.UtcNow;

        // Aynı shopper için aynı yayıncıda bekleyen tüm forgot-password
        // request'lerini birlikte resolved'la — yayıncı zaten parolayı yolladı.
        var siblings = await _db.ShopperSupportRequests
            .Where(r => r.ShopperId == request.ShopperId
                && licenseIds.Contains(r.LicenseId)
                && r.Kind == "forgot-password"
                && r.ResolvedAt == null)
            .ToListAsync(ct);
        foreach (var s in siblings)
            s.ResolvedAt = now;

        await _refresh.MarkAllRevokedAsync(request.ShopperId, now, ct);
        await _db.SaveChangesAsync(ct);

        // Eski istemciler aynı route ve tempPassword alanını deserialize
        // edebilsin; gerçek bir kimlik bilgisi hiçbir zaman dönmez.
        return Ok(new IssueTempPasswordResponse(null, "verification-sent"));
    }
}
