using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Services.Auth;

namespace OrderDeck.LicenseServer.Controllers.Panel;

/// <summary>
/// Yayıncı paneli — shopper destek talepleri (Faz 0b-1: forgot-password).
/// Shopper "Parolamı unuttum" derse server <see cref="Domain.ShopperSupportRequest"/>
/// satırı oluşturur (Bağlı her aktif yayıncı için bir tane). Yayıncı bu listeyi
/// görür, "Geçici parola gönder" der; server random parola üretir, shopper'ın
/// PasswordHash'ini günceller, plaintext parolayı sadece bu response'ta döner.
/// Yayıncı parolayı kendi WhatsApp'ından shopper'a mesaj atar.
/// </summary>
[ApiController]
[Route("api/panel/support-requests")]
[Authorize(AuthenticationSchemes = "Bearer-Customer")]
public sealed class PanelSupportRequestsController : ControllerBase
{
    private readonly LicenseDbContext _db;
    private readonly PasswordHasher _hasher;

    public PanelSupportRequestsController(LicenseDbContext db, PasswordHasher hasher)
    {
        _db = db;
        _hasher = hasher;
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

    // ── POST — geçici parola üret + shopper hash'ini güncelle ───────────────

    public sealed record IssueTempPasswordResponse(string TempPassword);

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

        var tempPassword = GenerateTempPassword();
        var now = DateTimeOffset.UtcNow;

        request.Shopper.PasswordHash = _hasher.Hash(tempPassword);
        request.Shopper.UpdatedAt = now;

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

        // Aktif refresh token'ları iptal et — eski cihazlar otomatik logout
        // olsun, shopper geçici parolayla giriş yapıp yenisini belirlesin.
        var refreshes = await _db.ShopperRefreshTokens
            .Where(t => t.ShopperId == request.ShopperId && t.RevokedAt == null)
            .ToListAsync(ct);
        foreach (var t in refreshes)
            t.RevokedAt = now;

        await _db.SaveChangesAsync(ct);

        return Ok(new IssueTempPasswordResponse(tempPassword));
    }

    /// <summary>
    /// 10 karakter, alfanumeric (lowercase + digit). Confuse-edici karakterler
    /// (0/O, 1/l/I) hariç. WhatsApp'ta kolayca yazılabilsin.
    /// </summary>
    private static string GenerateTempPassword()
    {
        const string alphabet = "abcdefghjkmnpqrstuvwxyz23456789";
        var rng = System.Security.Cryptography.RandomNumberGenerator.Create();
        Span<byte> buf = stackalloc byte[10];
        rng.GetBytes(buf);
        var sb = new System.Text.StringBuilder(10);
        for (var i = 0; i < buf.Length; i++)
            sb.Append(alphabet[buf[i] % alphabet.Length]);
        return sb.ToString();
    }
}
