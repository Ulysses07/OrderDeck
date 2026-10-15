using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Services.Auth;
using OrderDeck.LicenseServer.Services.Sync.PaymentRequests;

namespace OrderDeck.LicenseServer.Controllers.Licenses;

/// <summary>
/// Ödeme isteği uçları — spec 2026-10-13 §4.7, §6.4 (S3): bilgisayarlar arası PersonKey kirası. Desen satış komutlarıyla
/// aynı (<see cref="LicensesSalesCommandsController"/>): <c>Bearer-Customer</c> + rotadaki lisans çağırana ait olmalı,
/// değilse 404. İşleme hattı <see cref="PaymentRequestLeaseService"/>'te; uç yalnız sahipliği denetler ve sonucu çevirir:
/// başarı 200, ret ProblemDetails (<c>title</c> = kod, ek üyeler spec'teki gibi).
/// </summary>
[ApiController]
[Route("api/v1/licenses/{licenseId:guid}/payment-requests")]
[Authorize(AuthenticationSchemes = "Bearer-Customer")]
public sealed class LicensesPaymentRequestsController : ControllerBase
{
    private readonly LicenseDbContext _db;
    private readonly PaymentRequestLeaseService _leases;

    public LicensesPaymentRequestsController(LicenseDbContext db, PaymentRequestLeaseService leases)
    {
        _db = db;
        _leases = leases;
    }

    /// <summary><c>POST payment-requests/begin</c> (§6.4): kişi×kapsamın kirası. Mod Off'ta 409 <c>mode-off</c>.</summary>
    [HttpPost("begin")]
    public async Task<IActionResult> Begin(
        Guid licenseId, [FromBody] PaymentRequestBeginRequest request, CancellationToken ct)
    {
        if (!await OwnsLicenseAsync(licenseId, ct)) return NotFound();

        var reply = await _leases.BeginAsync(licenseId, request, Request.Headers.UserAgent.ToString(), ct);
        return ToResult(reply);
    }

    private IActionResult ToResult(PaymentRequestReply reply)
    {
        switch (reply)
        {
            case PaymentRequestSucceeded succeeded:
                return Ok(succeeded.Response);
            case PaymentRequestFailed failed:
                var problem = Problem(title: failed.Code, detail: failed.Detail, statusCode: failed.StatusCode);
                var details = (ProblemDetails)problem.Value!;
                foreach (var (key, value) in failed.Extensions)
                    details.Extensions[key] = value;
                return problem;
            default:
                throw new InvalidOperationException($"Bilinmeyen ödeme isteği sonucu: {reply.GetType().Name}");
        }
    }

    private async Task<bool> OwnsLicenseAsync(Guid licenseId, CancellationToken ct)
    {
        var customerId = User.GetTenantCustomerId();
        return await _db.Licenses.AnyAsync(l => l.Id == licenseId && l.CustomerId == customerId, ct);
    }
}
