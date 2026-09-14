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

    public sealed record LicenseHwRequest(string LicenseKey, string HardwareFingerprint, string? LegacyHardwareFingerprint = null);
    public sealed record ActivateRequest(string LicenseKey, string HardwareFingerprint, string? MachineName, string? LegacyHardwareFingerprint = null);

    [HttpPost("validate")]
    public async Task<IActionResult> Validate([FromBody] LicenseHwRequest req, CancellationToken ct)
    {
        var customerId = GetCustomerId();
        var result = await _validator.ValidateAsync(req.LicenseKey, req.HardwareFingerprint, customerId, ct);
        if (result is null) return NotFound();

        // LastSeenAt buradan güncellenir, çünkü sahadaki istemci /heartbeat'i
        // hiç çağırmıyor: WPF'in HeartbeatHostedService'i RefreshAsync üzerinden
        // BU uca geliyor (LicenseApiClient.HeartbeatAsync üretim kodunda çağrısız).
        // R8 §23-8 ölçümü bunu görünür kıldı — LastSeenAt aylarca bayat kalmıştı.
        // Dönüş değeri bilerek yok sayılıyor: aktivasyonsuz validate meşru
        // (status=notactivated) ve dokunuşun başarısızlığı cevabı etkilememeli.
        await _activations.HeartbeatAsync(
            req.LicenseKey, customerId, req.HardwareFingerprint,
            req.LegacyHardwareFingerprint, ClientAppVersion(), ct);

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
    public async Task<IActionResult> Activate(
        [FromBody] ActivateRequest req,
        [FromServices] OrderDeck.LicenseServer.Services.Observability.OrderDeckMetrics metrics,
        CancellationToken ct)
    {
        var customerId = GetCustomerId();
        try
        {
            var act = await _activations.ActivateAsync(
                req.LicenseKey, customerId, req.HardwareFingerprint, req.MachineName,
                req.LegacyHardwareFingerprint, ct);
            metrics.LicensesActivated.Add(1);
            return StatusCode(201, new { activationId = act.Id, expiresAt = act.License?.ExpiresAt });
        }
        catch (ActivationManager.ActivationException ex)
        {
            metrics.LicenseActivationFailures.Add(1, new KeyValuePair<string, object?>("code", ex.Code));
            return Problem(title: ex.Code, detail: ex.Message, statusCode: 409);
        }
    }

    [HttpPost("deactivate")]
    public async Task<IActionResult> Deactivate([FromBody] LicenseHwRequest req, CancellationToken ct)
    {
        var customerId = GetCustomerId();
        var ok = await _activations.DeactivateAsync(
            req.LicenseKey, customerId, req.HardwareFingerprint, req.LegacyHardwareFingerprint, ct);
        if (!ok) return NotFound();
        return NoContent();
    }

    [HttpPost("heartbeat")]
    public async Task<IActionResult> Heartbeat([FromBody] LicenseHwRequest req, CancellationToken ct)
    {
        var customerId = GetCustomerId();
        var ok = await _activations.HeartbeatAsync(
            req.LicenseKey, customerId, req.HardwareFingerprint, req.LegacyHardwareFingerprint,
            ClientAppVersion(), ct);
        if (!ok) return Problem(title: "not-activated", statusCode: 404);

        // Return basic status for client offline grace handling (4b will need this).
        var result = await _validator.ValidateAsync(req.LicenseKey, req.HardwareFingerprint, customerId, ct);
        return Ok(new
        {
            status = result?.Status.ToString().ToLowerInvariant(),
            expiresAt = result?.ExpiresAt
        });
    }

    /// <summary>User-Agent'tan uygulama sürümünü çeker ("OrderDeck-WPF/0.9.5"
    /// → "0.9.5"). Desen tutmazsa null — eski istemciler UA göndermiyor ve
    /// alan null kalmalı, tarayıcı UA'sı gibi başka bir şey yazılmamalı.</summary>
    private string? ClientAppVersion()
    {
        var ua = Request.Headers.UserAgent.ToString();
        var m = AppVersionPattern.Match(ua);
        return m.Success ? m.Groups[1].Value : null;
    }

    private static readonly System.Text.RegularExpressions.Regex AppVersionPattern =
        new(@"\bOrderDeck-WPF/([0-9A-Za-z.\-]{1,32})",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    private Guid GetCustomerId()
    {
        var sub = User.FindFirst("sub")?.Value
            ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? throw new InvalidOperationException("sub claim missing");
        return Guid.Parse(sub);
    }
}
