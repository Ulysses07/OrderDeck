using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Services.Auth;
using OrderDeck.LicenseServer.Services.BroadcastPosts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace OrderDeck.LicenseServer.Controllers.Panel;

[ApiController]
[Route("api/panel/posts")]
[Authorize(AuthenticationSchemes = "Bearer-Customer")]
public sealed class PanelBroadcastPostsController : ControllerBase
{
    private const long MaxPhotoBytes = 10 * 1024 * 1024;
    private const long MaxVideoBytes = 60 * 1024 * 1024;

    private static readonly HashSet<string> AllowedPhotoMime = new(StringComparer.OrdinalIgnoreCase)
        { "image/jpeg", "image/heic", "image/heif", "image/png", "image/webp" };
    private static readonly HashSet<string> AllowedVideoMime = new(StringComparer.OrdinalIgnoreCase)
        { "video/mp4", "video/quicktime", "video/x-m4v" };

    private readonly LicenseDbContext _db;
    private readonly IBroadcastMediaStorage _storage;

    public PanelBroadcastPostsController(LicenseDbContext db, IBroadcastMediaStorage storage)
    {
        _db = db;
        _storage = storage;
    }

    public sealed record UploadUrlRequest(string Type, long SizeBytes, string ContentType);
    public sealed record UploadUrlResponse(string UploadUrl, string ObjectKey, DateTimeOffset ExpiresAt);

    [HttpPost("upload-url")]
    public async Task<IActionResult> CreateUploadUrl([FromBody] UploadUrlRequest req, CancellationToken ct)
    {
        if (req is null) return Problem(title: "missing-body", statusCode: 400);

        var (allowedMime, maxBytes) = req.Type?.ToLowerInvariant() switch
        {
            "photo" => (AllowedPhotoMime, MaxPhotoBytes),
            "video" => (AllowedVideoMime, MaxVideoBytes),
            _ => (null!, 0L)
        };
        if (allowedMime is null)
            return Problem(title: "invalid-type", detail: "Type must be 'photo' or 'video'.", statusCode: 400);
        if (!allowedMime.Contains(req.ContentType ?? ""))
            return Problem(title: "invalid-content-type", statusCode: 400);
        if (req.SizeBytes <= 0 || req.SizeBytes > maxBytes)
            return Problem(title: "size-out-of-range", detail: $"Max {maxBytes} bytes.", statusCode: 400);

        var customerId = User.GetTenantCustomerId();
        var licenseId = await ResolveActiveLicenseAsync(customerId, ct);
        if (licenseId is null) return Problem(title: "no-active-license", statusCode: 400);

        var postId = Guid.NewGuid();
        var objectKey = $"{licenseId.Value}/{postId}/media.bin";
        var url = await _storage.CreateUploadUrlAsync(objectKey, req.ContentType!, req.SizeBytes, ct);

        return Ok(new UploadUrlResponse(url, objectKey, DateTimeOffset.UtcNow.AddMinutes(10)));
    }

    private Task<Guid?> ResolveActiveLicenseAsync(Guid customerId, CancellationToken ct)
        => _db.Licenses
            .Where(l => l.CustomerId == customerId && l.RevokedAt == null
                && l.ExpiresAt > DateTimeOffset.UtcNow)
            .OrderBy(l => l.IssuedAt)
            .Select(l => (Guid?)l.Id)
            .FirstOrDefaultAsync(ct);
}
