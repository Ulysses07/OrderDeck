using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Services.Auth;

namespace OrderDeck.LicenseServer.Controllers.Licenses;

/// <summary>
/// WPF App Settings ekranındaki IBAN + AccountHolder bilgisinin
/// LicenseServer'a sync'i. Bu bilgi shopper-tarafından upload edilen PDF
/// dekontların IBAN match fraud kontrolünde kullanılır
/// (Payment.RecipientIban vs License.PaymentIban karşılaştırması).
///
/// Idempotent: PUT semantics; her sync mevcut değerleri override eder.
/// </summary>
[ApiController]
[Authorize(AuthenticationSchemes = "Bearer-Customer")]
[Route("api/v1/licenses/{licenseId:guid}/payment-account")]
public sealed class LicensesPaymentAccountController : ControllerBase
{
    private readonly LicenseDbContext _db;
    public LicensesPaymentAccountController(LicenseDbContext db) => _db = db;

    public sealed record SetPaymentAccountRequest(string? Iban, string? AccountHolder);

    [HttpPost]
    public async Task<IActionResult> Set(Guid licenseId, [FromBody] SetPaymentAccountRequest req, CancellationToken ct)
    {
        var customerId = User.GetTenantCustomerId();
        var license = await _db.Licenses
            .FirstOrDefaultAsync(l => l.Id == licenseId && l.CustomerId == customerId, ct);
        if (license is null) return NotFound();

        // Normalize IBAN: strip spaces/punctuation, uppercase. Empty → null.
        var normalizedIban = NormalizeIban(req.Iban);
        if (normalizedIban is { Length: > 34 })
            return Problem(title: "iban-too-long", statusCode: 400);

        var normalizedHolder = string.IsNullOrWhiteSpace(req.AccountHolder)
            ? null
            : req.AccountHolder.Trim();
        if (normalizedHolder is { Length: > 200 })
            return Problem(title: "account-holder-too-long", statusCode: 400);

        license.PaymentIban = normalizedIban;
        license.PaymentAccountHolder = normalizedHolder;
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    private static string? NormalizeIban(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var clean = new string(raw.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        return clean.Length == 0 ? null : clean;
    }
}
