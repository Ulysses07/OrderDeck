using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Services.Auth;
using OrderDeck.LicenseServer.Services.Sync.Commands;

namespace OrderDeck.LicenseServer.Controllers.Licenses;

/// <summary>
/// <c>POST sales/commands</c> — spec 2026-10-13 §4.6: devredilmiş yayının etiketlerini değiştiren, sunucuda doğrulanan
/// ve tek işlemde uygulanan komutlar (S2). Desen satış uçlarıyla aynı (<see cref="LicensesSalesController"/>):
/// <c>Bearer-Customer</c> + rotadaki lisans çağırana ait olmalı, değilse 404. İşleme hattı
/// <see cref="SalesCommandService"/>'te; bu uç yalnız sahipliği denetler ve sonucu çevirir: başarı 200
/// <see cref="SalesCommandResponse"/>, ret ProblemDetails (<c>title</c> = kod, ek üyeler spec'teki gibi).
/// </summary>
[ApiController]
[Route("api/v1/licenses/{licenseId:guid}/sales/commands")]
[Authorize(AuthenticationSchemes = "Bearer-Customer")]
public sealed class LicensesSalesCommandsController : ControllerBase
{
    private readonly LicenseDbContext _db;
    private readonly SalesCommandService _commands;

    public LicensesSalesCommandsController(LicenseDbContext db, SalesCommandService commands)
    {
        _db = db;
        _commands = commands;
    }

    [HttpPost]
    public async Task<IActionResult> Post(Guid licenseId, [FromBody] SalesCommandRequest request, CancellationToken ct)
    {
        var customerId = User.GetTenantCustomerId();
        if (!await _db.Licenses.AnyAsync(l => l.Id == licenseId && l.CustomerId == customerId, ct)) return NotFound();

        var reply = await _commands.ExecuteAsync(licenseId, request, Request.Headers.UserAgent.ToString(), ct);
        switch (reply)
        {
            case SalesCommandSucceeded succeeded:
                return Ok(succeeded.Response);
            case SalesCommandFailed failed:
                var problem = Problem(title: failed.Code, detail: failed.Detail, statusCode: failed.StatusCode);
                var details = (ProblemDetails)problem.Value!;
                foreach (var (key, value) in failed.Extensions)
                    details.Extensions[key] = value;
                return problem;
            default:
                throw new InvalidOperationException($"Bilinmeyen komut sonucu: {reply.GetType().Name}");
        }
    }
}
