using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Auth;

namespace OrderDeck.LicenseServer.Controllers.Shopper;

[ApiController]
[Authorize(AuthenticationSchemes = "Bearer-Shopper")]
[Route("api/v1/shopper/devices")]
public sealed class ShopperDevicesController : ControllerBase
{
    private readonly LicenseDbContext _db;
    public ShopperDevicesController(LicenseDbContext db) => _db = db;

    public sealed record RegisterRequest(string DeviceId, string Platform, string PushToken);

    [HttpPost]
    public async Task<IActionResult> Register([FromBody] RegisterRequest req, CancellationToken ct)
    {
        var shopperId = User.GetShopperId();
        if (shopperId is null) return Unauthorized();

        // Validate shopper exists + not deleted
        var shopperExists = await _db.Shoppers
            .AnyAsync(s => s.Id == shopperId && s.DeletedAt == null, ct);
        if (!shopperExists) return Unauthorized();

        if (string.IsNullOrWhiteSpace(req.DeviceId) || req.DeviceId.Length > 64)
            return Problem(title: "invalid-device-id", statusCode: 400);
        if (req.Platform != "ios" && req.Platform != "android")
            return Problem(title: "invalid-platform", statusCode: 400);
        if (string.IsNullOrWhiteSpace(req.PushToken) || req.PushToken.Length > 512)
            return Problem(title: "invalid-push-token", statusCode: 400);

        var now = DateTimeOffset.UtcNow;
        var existing = await _db.ShopperPushDevices
            .FirstOrDefaultAsync(d => d.ShopperId == shopperId && d.DeviceId == req.DeviceId, ct);
        if (existing is null)
        {
            _db.ShopperPushDevices.Add(new ShopperPushDevice
            {
                Id = Guid.NewGuid(),
                ShopperId = shopperId.Value,
                DeviceId = req.DeviceId,
                Platform = req.Platform,
                PushToken = req.PushToken,
                UpdatedAt = now,
            });
        }
        else
        {
            existing.Platform = req.Platform;
            existing.PushToken = req.PushToken;
            existing.UpdatedAt = now;
        }

        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpDelete("{deviceId}")]
    public async Task<IActionResult> Unregister(string deviceId, CancellationToken ct)
    {
        var shopperId = User.GetShopperId();
        if (shopperId is null) return Unauthorized();

        var existing = await _db.ShopperPushDevices
            .FirstOrDefaultAsync(d => d.ShopperId == shopperId && d.DeviceId == deviceId, ct);
        if (existing is null) return NotFound();

        _db.ShopperPushDevices.Remove(existing);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }
}
