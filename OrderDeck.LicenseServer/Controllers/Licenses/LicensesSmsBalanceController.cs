using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Services.Auth;

namespace OrderDeck.LicenseServer.Controllers.Licenses;

/// <summary>
/// UYUMLULUK STUB'I — kredi sistemi emekli (Plan 3, §1.4b kararı).
///
/// <para>Sahadaki eski WPF istemcileri (Velopack gecikmesi) geçmiş listesini
/// yüklemeden ÖNCE bu ucu await ediyor (BulkSmsViewModel.ReloadBalanceAndHistoryAsync);
/// uç 404 dönerse toplu SMS ekranı tamamen ölür. Bu yüzden uç bir sürüm boyunca
/// sabit değerle yaşar. KALDIRMA KOŞULU: saha WPF sürümleri bakiye çağrısı
/// yapmayan istemciye (bu planın Görev 8'i) geçtiğinde.</para>
/// </summary>
[ApiController]
[Route("api/v1/licenses/{licenseId:guid}/sms")]
[Authorize(AuthenticationSchemes = "Bearer-Customer")]
public sealed class LicensesSmsBalanceController : ControllerBase
{
    private readonly LicenseDbContext _db;
    public LicensesSmsBalanceController(LicenseDbContext db) => _db = db;

    public sealed record BalanceResponse(int CreditsRemaining, DateTimeOffset UpdatedAt);

    [HttpGet("balance")]
    public async Task<IActionResult> Balance(Guid licenseId, CancellationToken ct)
    {
        var customerId = User.GetTenantCustomerId();
        var ownsLicense = await _db.Licenses
            .AnyAsync(l => l.Id == licenseId && l.CustomerId == customerId, ct);
        if (!ownsLicense) return NotFound();

        // Sabit 0: eski istemcide yalnız kozmetik rozet ("Kredi: 0").
        // Gönderilebilirlik oradan değil Preview.Sufficient'tan geliyor.
        return Ok(new BalanceResponse(0, DateTimeOffset.UtcNow));
    }
}
