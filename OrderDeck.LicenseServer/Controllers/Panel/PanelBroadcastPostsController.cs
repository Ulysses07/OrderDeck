using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
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

    private const int MaxTextLength = 2000;
    private const int MaxVideoDurationSec = 45;

    public sealed record CreatePostMediaDto(
        string ObjectKey, string ContentType, long SizeBytes,
        int? DurationSec, int Width, int Height);

    public sealed record CreatePostRequest(
        string Type, string? TextBody, CreatePostMediaDto? Media);

    public sealed record PostDto(
        Guid Id, string Type, string? TextBody,
        string? MediaContentType, int? MediaWidth, int? MediaHeight,
        int? MediaDurationSec, DateTimeOffset CreatedAt,
        DateTimeOffset ExpiresAt, bool IsPinned);

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreatePostRequest req, CancellationToken ct)
    {
        if (req is null) return Problem(title: "missing-body", statusCode: 400);

        var customerId = User.GetTenantCustomerId();
        var licenseId = await ResolveActiveLicenseAsync(customerId, ct);
        if (licenseId is null) return Problem(title: "no-active-license", statusCode: 400);

        var type = req.Type?.ToLowerInvariant() switch
        {
            "text" => BroadcastPostType.Text,
            "photo" => BroadcastPostType.Photo,
            "video" => BroadcastPostType.Video,
            _ => (BroadcastPostType?)null
        };
        if (type is null) return Problem(title: "invalid-type", statusCode: 400);

        var text = req.TextBody?.Trim();
        if (type == BroadcastPostType.Text && string.IsNullOrWhiteSpace(text))
            return Problem(title: "text-required", statusCode: 400);
        if (text is { Length: > MaxTextLength })
            return Problem(title: "text-too-long", statusCode: 400);
        if (string.IsNullOrEmpty(text)) text = null;

        BroadcastPost post;
        if (type == BroadcastPostType.Text)
        {
            post = NewPost(licenseId.Value, type.Value, text, null);
        }
        else
        {
            if (req.Media is null) return Problem(title: "media-required", statusCode: 400);

            if (!req.Media.ObjectKey.StartsWith($"{licenseId.Value}/"))
                return Problem(title: "invalid-object-key", statusCode: 400);

            if (type == BroadcastPostType.Video &&
                (req.Media.DurationSec is null or <= 0 or > MaxVideoDurationSec))
                return Problem(title: "video-duration-out-of-range",
                    detail: $"Max {MaxVideoDurationSec} seconds.", statusCode: 400);

            var head = await _storage.HeadAsync(req.Media.ObjectKey, ct);
            if (head is null) return Problem(title: "media-not-uploaded", statusCode: 400);

            var (allowedMime, maxBytes) = type == BroadcastPostType.Photo
                ? (AllowedPhotoMime, MaxPhotoBytes)
                : (AllowedVideoMime, MaxVideoBytes);
            if (!allowedMime.Contains(head.ContentType))
                return Problem(title: "invalid-content-type", statusCode: 400);
            if (head.SizeBytes <= 0 || head.SizeBytes > maxBytes)
                return Problem(title: "size-out-of-range", statusCode: 400);

            var verifiedMedia = req.Media with { ContentType = head.ContentType, SizeBytes = head.SizeBytes };
            post = NewPost(licenseId.Value, type.Value, text, verifiedMedia);
        }

        _db.BroadcastPosts.Add(post);
        await _db.SaveChangesAsync(ct);

        return Created($"/api/panel/posts/{post.Id}", ToDto(post));
    }

    private static BroadcastPost NewPost(Guid licenseId, BroadcastPostType type, string? text, CreatePostMediaDto? media)
    {
        var now = DateTimeOffset.UtcNow;
        return new BroadcastPost
        {
            Id = Guid.NewGuid(),
            LicenseId = licenseId,
            Type = type,
            TextBody = text,
            MediaObjectKey = media?.ObjectKey,
            MediaContentType = media?.ContentType,
            MediaSizeBytes = media?.SizeBytes,
            MediaDurationSec = media?.DurationSec,
            MediaWidth = media?.Width,
            MediaHeight = media?.Height,
            CreatedAt = now,
            ExpiresAt = now.AddDays(30),
            IsPinned = false
        };
    }

    private static PostDto ToDto(BroadcastPost p) =>
        new(p.Id, p.Type.ToString().ToLowerInvariant(), p.TextBody,
            p.MediaContentType, p.MediaWidth, p.MediaHeight, p.MediaDurationSec,
            p.CreatedAt, p.ExpiresAt, p.IsPinned);

    public sealed record ListResponse(List<PostDto> Posts, string? NextCursor);

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? cursor, [FromQuery] int limit = 20, CancellationToken ct = default)
    {
        if (limit < 1 || limit > 100) limit = 20;

        var customerId = User.GetTenantCustomerId();
        var licenseIds = await _db.Licenses
            .Where(l => l.CustomerId == customerId)
            .Select(l => l.Id)
            .ToListAsync(ct);

        if (licenseIds.Count == 0) return Ok(new ListResponse(new(), null));

        var query = _db.BroadcastPosts
            .Where(p => licenseIds.Contains(p.LicenseId) && p.DeletedAt == null);

        // Composite cursor: "{createdAt:O}|{id}". Pinned posts all fit on page 1
        // (Task 9 enforces max-5-pinned); subsequent pages paginate unpinned only.
        // Tie-breaker on Id keeps same-tick siblings deterministic across pages.
        if (!string.IsNullOrWhiteSpace(cursor) && TryParseCursor(cursor, out var cursorAt, out var cursorId))
        {
            query = query.Where(p => !p.IsPinned &&
                (p.CreatedAt < cursorAt || (p.CreatedAt == cursorAt && p.Id.CompareTo(cursorId) < 0)));
        }

        var rows = await query
            .OrderByDescending(p => p.IsPinned)
            .ThenByDescending(p => p.CreatedAt)
            .ThenByDescending(p => p.Id)
            .Take(limit + 1)
            .ToListAsync(ct);

        string? nextCursor = null;
        if (rows.Count > limit)
        {
            var last = rows[limit - 1];
            nextCursor = $"{last.CreatedAt.ToString("O")}|{last.Id}";
            rows = rows.Take(limit).ToList();
        }

        return Ok(new ListResponse(rows.Select(ToDto).ToList(), nextCursor));
    }

    private static bool TryParseCursor(string cursor, out DateTimeOffset createdAt, out Guid id)
    {
        createdAt = default; id = default;
        var parts = cursor.Split('|', 2);
        if (parts.Length != 2) return false;
        return DateTimeOffset.TryParse(parts[0], out createdAt) && Guid.TryParse(parts[1], out id);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var customerId = User.GetTenantCustomerId();
        var post = await _db.BroadcastPosts
            .Where(p => p.Id == id && p.DeletedAt == null && p.License.CustomerId == customerId)
            .FirstOrDefaultAsync(ct);
        if (post is null) return NotFound();
        return Ok(ToDto(post));
    }

    public sealed record UpdatePostRequest(string? TextBody);

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdatePostRequest req, CancellationToken ct)
    {
        if (req is null) return Problem(title: "missing-body", statusCode: 400);

        var customerId = User.GetTenantCustomerId();
        var post = await _db.BroadcastPosts
            .Where(p => p.Id == id && p.DeletedAt == null && p.License.CustomerId == customerId)
            .FirstOrDefaultAsync(ct);
        if (post is null) return NotFound();

        var text = req.TextBody?.Trim();
        if (post.Type == BroadcastPostType.Text && string.IsNullOrWhiteSpace(text))
            return Problem(title: "text-required", statusCode: 400);
        if (text is { Length: > MaxTextLength })
            return Problem(title: "text-too-long", statusCode: 400);
        if (string.IsNullOrEmpty(text)) text = null;

        post.TextBody = text;
        await _db.SaveChangesAsync(ct);

        return Ok(ToDto(post));
    }

    public sealed record MediaUrlResponse(string Url, DateTimeOffset ExpiresAt);

    [HttpGet("{id:guid}/media-url")]
    public async Task<IActionResult> GetMediaUrl(Guid id, CancellationToken ct)
    {
        var customerId = User.GetTenantCustomerId();
        var post = await _db.BroadcastPosts
            .Where(p => p.Id == id && p.DeletedAt == null && p.License.CustomerId == customerId)
            .FirstOrDefaultAsync(ct);
        if (post is null) return NotFound();
        if (string.IsNullOrWhiteSpace(post.MediaObjectKey))
            return Problem(title: "no-media", statusCode: 400);

        var url = await _storage.CreateDownloadUrlAsync(post.MediaObjectKey, ct);
        return Ok(new MediaUrlResponse(url, DateTimeOffset.UtcNow.AddMinutes(5)));
    }

    private Task<Guid?> ResolveActiveLicenseAsync(Guid customerId, CancellationToken ct)
        => _db.Licenses
            .Where(l => l.CustomerId == customerId && l.RevokedAt == null
                && l.ExpiresAt > DateTimeOffset.UtcNow)
            .OrderBy(l => l.IssuedAt)
            .Select(l => (Guid?)l.Id)
            .FirstOrDefaultAsync(ct);
}
