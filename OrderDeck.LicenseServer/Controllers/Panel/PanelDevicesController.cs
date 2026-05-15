using System.Security.Claims;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace OrderDeck.LicenseServer.Controllers.Panel;

/// <summary>
/// Push device registration for OrderDeck Panel mobile app. The mobile
/// client registers its FCM (Android) or APNS (iOS) token here after
/// permission grant, and unregisters on logout / app uninstall feedback.
///
/// Fan-out (`new dekont`, `new order`) is owned by a dedicated push
/// service (PR #7); this controller only manages registrations.
/// </summary>
[ApiController]
[Route("api/panel/devices")]
[Authorize(AuthenticationSchemes = "Bearer-Customer")]
public sealed class PanelDevicesController : ControllerBase
{
    private readonly LicenseDbContext _db;

    public PanelDevicesController(LicenseDbContext db)
    {
        _db = db;
    }

    public sealed record RegisterDeviceRequest(string DeviceId, string Platform, string PushToken);

    [HttpPost]
    public async Task<IActionResult> Register([FromBody] RegisterDeviceRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.DeviceId) ||
            string.IsNullOrWhiteSpace(req.Platform) ||
            string.IsNullOrWhiteSpace(req.PushToken))
            return Problem(title: "missing-fields", statusCode: 400);

        var platform = req.Platform.ToLowerInvariant();
        if (platform != "ios" && platform != "android")
            return Problem(title: "invalid-platform", detail: "Platform must be 'ios' or 'android'.", statusCode: 400);

        if (req.DeviceId.Length > 64) return Problem(title: "device-id-too-long", statusCode: 400);
        if (req.PushToken.Length > 512) return Problem(title: "push-token-too-long", statusCode: 400);

        var customerId = User.GetTenantCustomerId();
        var now = DateTimeOffset.UtcNow;

        // Upsert by (CustomerId, DeviceId) — unique index covers this.
        var existing = await _db.PushDevices
            .FirstOrDefaultAsync(d => d.CustomerId == customerId && d.DeviceId == req.DeviceId, ct);

        if (existing is null)
        {
            _db.PushDevices.Add(new PushDevice
            {
                Id = Guid.NewGuid(),
                CustomerId = customerId,
                DeviceId = req.DeviceId,
                Platform = platform,
                PushToken = req.PushToken,
                CreatedAt = now,
                LastSeenAt = now
            });
        }
        else
        {
            existing.Platform = platform;
            existing.PushToken = req.PushToken;
            existing.LastSeenAt = now;
        }

        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpDelete("{token}")]
    public async Task<IActionResult> Unregister(string token, CancellationToken ct)
    {
        var customerId = User.GetTenantCustomerId();
        var device = await _db.PushDevices
            .FirstOrDefaultAsync(d => d.CustomerId == customerId && d.PushToken == token, ct);
        if (device is null) return NoContent();   // idempotent

        _db.PushDevices.Remove(device);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var customerId = User.GetTenantCustomerId();
        var rows = await _db.PushDevices
            .Where(d => d.CustomerId == customerId)
            .OrderByDescending(d => d.LastSeenAt)
            .Select(d => new
            {
                id = d.Id,
                deviceId = d.DeviceId,
                platform = d.Platform,
                createdAt = d.CreatedAt,
                lastSeenAt = d.LastSeenAt
            })
            .ToListAsync(ct);
        return Ok(rows);
    }

}
