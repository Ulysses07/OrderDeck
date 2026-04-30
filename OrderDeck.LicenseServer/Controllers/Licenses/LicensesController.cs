using System.Security.Claims;
using OrderDeck.LicenseServer.Services.Licensing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace OrderDeck.LicenseServer.Controllers.Licenses;

[ApiController]
[Route("api/v1/licenses")]
[Authorize(AuthenticationSchemes = "Bearer-Customer")]
public sealed class LicensesController : ControllerBase
{
    private readonly LicenseValidator _validator;
    private readonly ActivationManager _activations;

    public LicensesController(LicenseValidator validator, ActivationManager activations)
    {
        _validator = validator;
        _activations = activations;
    }

    public sealed record LicenseHwRequest(string LicenseKey, string HardwareFingerprint);
    public sealed record ActivateRequest(string LicenseKey, string HardwareFingerprint, string? MachineName);

    [HttpPost("validate")]
    public async Task<IActionResult> Validate([FromBody] LicenseHwRequest req, CancellationToken ct)
    {
        var customerId = GetCustomerId();
        var result = await _validator.ValidateAsync(req.LicenseKey, req.HardwareFingerprint, customerId, ct);
        if (result is null) return NotFound();
        return Ok(new
        {
            status = result.Status.ToString().ToLowerInvariant(),
            expiresAt = result.ExpiresAt,
            remainingDays = result.RemainingDays,
            sku = result.Sku,
            slotInfo = result.SlotInfo
        });
    }

    [HttpPost("activate")]
    public async Task<IActionResult> Activate([FromBody] ActivateRequest req, CancellationToken ct)
    {
        var customerId = GetCustomerId();
        try
        {
            var act = await _activations.ActivateAsync(
                req.LicenseKey, customerId, req.HardwareFingerprint, req.MachineName, ct);
            return StatusCode(201, new { activationId = act.Id, expiresAt = act.License?.ExpiresAt });
        }
        catch (ActivationManager.ActivationException ex)
        {
            return Problem(title: ex.Code, detail: ex.Message, statusCode: 409);
        }
    }

    [HttpPost("deactivate")]
    public async Task<IActionResult> Deactivate([FromBody] LicenseHwRequest req, CancellationToken ct)
    {
        var customerId = GetCustomerId();
        var ok = await _activations.DeactivateAsync(req.LicenseKey, customerId, req.HardwareFingerprint, ct);
        if (!ok) return NotFound();
        return NoContent();
    }

    [HttpPost("heartbeat")]
    public async Task<IActionResult> Heartbeat([FromBody] LicenseHwRequest req, CancellationToken ct)
    {
        var customerId = GetCustomerId();
        var ok = await _activations.HeartbeatAsync(req.LicenseKey, customerId, req.HardwareFingerprint, ct);
        if (!ok) return Problem(title: "not-activated", statusCode: 404);

        // Return basic status for client offline grace handling (4b will need this).
        var result = await _validator.ValidateAsync(req.LicenseKey, req.HardwareFingerprint, customerId, ct);
        return Ok(new
        {
            status = result?.Status.ToString().ToLowerInvariant(),
            expiresAt = result?.ExpiresAt
        });
    }

    private Guid GetCustomerId()
    {
        var sub = User.FindFirst("sub")?.Value
            ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? throw new InvalidOperationException("sub claim missing");
        return Guid.Parse(sub);
    }
}
