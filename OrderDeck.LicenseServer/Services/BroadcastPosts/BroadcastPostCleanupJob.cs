using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OrderDeck.LicenseServer.Data;

namespace OrderDeck.LicenseServer.Services.BroadcastPosts;

/// <summary>
/// Hangfire daily job: 30 gün geçmiş ve sabitlenmemiş post'ları soft-delete eder
/// + R2 medyalarını temizler. Pin'lenmiş post'lar Expires=9999 olduğu için
/// query'de hiç bulunmaz.
/// </summary>
public sealed class BroadcastPostCleanupJob
{
    private const int BatchSize = 500;

    private readonly LicenseDbContext _db;
    private readonly IBroadcastMediaStorage _storage;
    private readonly ILogger<BroadcastPostCleanupJob> _log;

    public BroadcastPostCleanupJob(
        LicenseDbContext db, IBroadcastMediaStorage storage,
        ILogger<BroadcastPostCleanupJob> log)
    {
        _db = db;
        _storage = storage;
        _log = log;
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var expired = await _db.BroadcastPosts
            .Where(p => p.ExpiresAt < now && !p.IsPinned && p.DeletedAt == null)
            .OrderBy(p => p.ExpiresAt)
            .Take(BatchSize)
            .ToListAsync(ct);

        if (expired.Count == 0)
        {
            _log.LogDebug("BroadcastPost cleanup: no expired rows");
            return;
        }

        foreach (var p in expired)
        {
            if (!string.IsNullOrWhiteSpace(p.MediaObjectKey))
            {
                try { await _storage.DeleteAsync(p.MediaObjectKey, ct); }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Cleanup: R2 delete failed for {Key}", p.MediaObjectKey);
                }
            }
            p.DeletedAt = now;
        }

        await _db.SaveChangesAsync(ct);
        _log.LogInformation("BroadcastPost cleanup: soft-deleted {Count} expired rows", expired.Count);
    }
}
