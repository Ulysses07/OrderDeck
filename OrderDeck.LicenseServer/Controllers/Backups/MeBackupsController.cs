using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Audit;
using OrderDeck.LicenseServer.Services.Backup;

namespace OrderDeck.LicenseServer.Controllers.Backups;

[ApiController]
[Route("api/v1/me/backups")]
[Authorize(AuthenticationSchemes = "Bearer-Customer")]
public sealed class MeBackupsController : ControllerBase
{
    private readonly LicenseDbContext _db;
    private readonly BackupStorageService _storage;
    private readonly BackupRetentionService _retention;
    private readonly IAuditService _audit;
    private readonly IS3BackupSink _s3;
    private readonly Microsoft.Extensions.Options.IOptions<BackupOptions> _opt;
    private readonly BackupUploadThrottle _throttle;
    private readonly OrderDeck.LicenseServer.Services.Observability.OrderDeckMetrics _metrics;
    private readonly ILogger<MeBackupsController> _log;

    public MeBackupsController(
        LicenseDbContext db,
        BackupStorageService storage,
        BackupRetentionService retention,
        IAuditService audit,
        IS3BackupSink s3,
        Microsoft.Extensions.Options.IOptions<BackupOptions> opt,
        BackupUploadThrottle throttle,
        OrderDeck.LicenseServer.Services.Observability.OrderDeckMetrics metrics,
        ILogger<MeBackupsController> log)
    {
        _db = db;
        _storage = storage;
        _retention = retention;
        _audit = audit;
        _s3 = s3;
        _opt = opt;
        _throttle = throttle;
        _metrics = metrics;
        _log = log;
    }

    private Guid CustomerId =>
        Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)
                ?? User.FindFirstValue("sub")
                ?? throw new InvalidOperationException("Missing sub claim"));

    [HttpPost]
    [EnableRateLimiting("backup-upload")]
    public async Task<IActionResult> Upload(CancellationToken ct)
    {
        var sha = Request.Headers["X-Backup-Sha256"].ToString();
        if (string.IsNullOrWhiteSpace(sha) || sha.Length != 64)
            return BadRequest(new { error = "X-Backup-Sha256 header required (64 hex chars)" });

        var maxMb = _opt.Value.MaxBlobSizeMb;
        var maxBytes = maxMb * 1024L * 1024L;

        // Kestrel'in varsayılan gövde tavanı 30.000.000 bayt (~28,6 MB) ve
        // MaxBlobSizeMb'den TAMAMEN bağımsız. Bu satır eklenene kadar aşağıdaki
        // 413 dalına hiç gelinmiyordu: 28,6 MB'ı aşan istek daha sunucu
        // katmanında kesiliyor, müşteri bizim mesajımızı değil ham protokol
        // hatasını görüyordu — ve MaxBlobSizeMb'yi büyütmek hiçbir şeyi
        // değiştirmiyordu, çünkü bağlayıcı sınır o değildi.
        var sizeFeature = HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (sizeFeature is { IsReadOnly: false })
            sizeFeature.MaxRequestBodySize = maxBytes;

        // Content-Length varsa gövdeyi HİÇ okumadan reddet. Okuyup sonra
        // ölçmek, zaten reddedeceğimiz baytları önce belleğe almak demekti;
        // tavanı yükseltmenin bedelini de tam olarak orası ödüyordu.
        if (Request.ContentLength is { } declaredLength && declaredLength > maxBytes)
            return TooLarge(maxMb);

        // Sıra: gövdeyi okumadan ÖNCE. Kapının amacı eşzamanlı tamponlanan bayt
        // miktarını sınırlamak; okuduktan sonra beklemek hiçbir şey korumaz.
        if (!await _throttle.TryEnterAsync(ct))
        {
            Response.Headers.RetryAfter = "30";
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new { error = "Server is busy storing other backups. Retry shortly." });
        }

        byte[] bytes;
        try
        {
            try
            {
                bytes = await ReadBodyAsync(ct);
            }
            catch (BadHttpRequestException ex)
                when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
            {
                // Yukarıda kurduğumuz sınır tetiklendi (Content-Length yoksa ya
                // da yalan söylediyse). Ham hata yerine kendi gövdemizi dönüyoruz
                // ki istemci iki yolda da aynı yanıtı görsün.
                return TooLarge(maxMb);
            }
            catch (EndOfStreamException)
            {
                // Content-Length söylediğinden az bayt geldi. Yarım gövdeyi
                // yedek diye kaydetmektense reddetmek şart — SHA da tutmazdı
                // ama o kontrole gelmeden burada kesiliyor.
                return BadRequest(new { error = "Request body shorter than Content-Length" });
            }

            return await StoreAsync(bytes, sha, maxMb, ct);
        }
        finally
        {
            _throttle.Exit();
        }
    }

    private async Task<IActionResult> StoreAsync(byte[] bytes, string sha, int maxMb, CancellationToken ct)
    {
        // Savunma derinliği: Content-Length yoksa VE sunucu gövde sınırını
        // uygulamıyorsa (TestServer'da bu özellik yok) tek kalan ölçüm bu.
        if (bytes.LongLength > maxMb * 1024L * 1024L)
            return TooLarge(maxMb);

        var actualSha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (!string.Equals(actualSha, sha, StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { error = "SHA256 mismatch — body integrity check failed" });

        // Per-customer storage quota. Retention prunes oldest non-milestones to 5,
        // but a customer accumulating monthly milestones for years could still drift
        // past a sane budget. Reject up-front instead of after the encrypted blob
        // is on disk so we don't waste IO writing something we'd just delete.
        var quotaMb = _opt.Value.PerCustomerQuotaMb;
        if (quotaMb > 0)
        {
            var existingBytes = await _db.CustomerBackups
                .Where(b => b.CustomerId == CustomerId)
                .SumAsync(b => (long?)b.SizeBytes, ct) ?? 0L;
            var quotaBytes = quotaMb * 1024L * 1024L;
            // bytes is plaintext; encrypted is bytes.Length + 28 (nonce+tag). Use
            // bytes.Length as a close-enough estimate — over-counting is fine, we'd
            // rather reject borderline cases than blow the cap.
            if (existingBytes + bytes.LongLength > quotaBytes)
            {
                return StatusCode(StatusCodes.Status507InsufficientStorage,
                    new { error = $"Per-customer backup quota exceeded ({quotaMb} MB). Delete older backups via /api/v1/me/backups/{{id}}." });
            }
        }

        var (encrypted, keyVersion) = _storage.Encrypt(bytes);
        var blobPath = await _storage.WriteBlobAsync(CustomerId, encrypted, ct);

        var backup = new CustomerBackup
        {
            Id = Guid.NewGuid(),
            CustomerId = CustomerId,
            BlobPath = blobPath,
            SizeBytes = encrypted.Length,
            ChecksumSha256 = actualSha,
            CreatedAt = DateTimeOffset.UtcNow,
            IsMonthlyMilestone = false,
            UserAgent = Request.Headers["User-Agent"].ToString(),
            MachineName = Request.Headers["X-Machine-Name"].ToString(),
            KeyVersion = keyVersion
        };
        _db.CustomerBackups.Add(backup);
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch
        {
            // Blob diskte, satır yazılamadı. Sıra bilinçli olarak böyle: ters
            // sırada satır yazılıp blob yazılamasaydı hayalet yedek kalırdı.
            // Burada kalan yetim dosyayı hemen toplamak ucuz — ama YETERLİ
            // DEĞİL: süreç tam bu noktada ölürse bu catch hiç çalışmaz. Asıl
            // güvence BackupOrphanCleanupJob; bu yalnızca sık görülen hâli
            // beklemeden kapatır.
            _storage.DeleteBlob(blobPath);
            throw;
        }

        _metrics.BackupsUploaded.Add(1);
        _metrics.BackupUploadBytes.Record(encrypted.Length);

        await _retention.EnforceAfterInsertAsync(CustomerId, backup.Id, ct);

        // Re-load to capture milestone flag (retention may have set it)
        var saved = await _db.CustomerBackups.FindAsync(new object[] { backup.Id }, ct);

        // Off-host replication (Phase 5b). Fire-and-forget when BestEffort=true
        // so the customer's POST doesn't wait on cross-region S3 latency. Sink
        // is a no-op when Backup:S3:Enabled=false.
        if (_s3.IsEnabled)
        {
            var customerIdCopy = CustomerId;
            var blobPathCopy = blobPath;
            _ = Task.Run(async () =>
            {
                try { await _s3.UploadAsync(blobPathCopy, customerIdCopy); }
                catch (Exception ex) { _log.LogError(ex, "S3 replication failed for {Path}", blobPathCopy); }
            });
        }

        await _audit.LogAsync(BackupAuditEvents.BackupCreated,
            BackupAuditEvents.TargetType,
            backup.Id.ToString(),
            new { sizeBytes = encrypted.Length, isMonthlyMilestone = saved!.IsMonthlyMilestone },
            ct);

        return StatusCode(StatusCodes.Status201Created, new
        {
            id = saved.Id,
            sizeBytes = saved.SizeBytes,
            createdAt = saved.CreatedAt,
            isMonthlyMilestone = saved.IsMonthlyMilestone
        });
    }

    /// <summary>
    /// Gövdeyi tek bir dizide toplar.
    ///
    /// <para>Content-Length varsa dizi TAM boyutta ayrılıyor. Önceki hâl
    /// <c>MemoryStream</c> ile okuyup <c>ToArray()</c> çağırıyordu; bu, blob
    /// boyutunda iki fazladan kopya demekti — tampon büyürken bırakılan eski
    /// diziler, artı ToArray'in çıkardığı yeni dizi. 64 MB'lık bir yedekte
    /// gereksiz yere ~128 MB. Boyutun üst sınırı çağıran tarafta zaten
    /// doğrulanmış olduğu için burada ayrılan dizi de sınırlı.</para>
    ///
    /// <para>Content-Length yoksa (chunked) boyutu önden bilemiyoruz; tavanı
    /// Kestrel uyguluyor ve aşılırsa okuma <see cref="BadHttpRequestException"/>
    /// ile düşüyor.</para>
    /// </summary>
    private async Task<byte[]> ReadBodyAsync(CancellationToken ct)
    {
        if (Request.ContentLength is { } length)
        {
            var buffer = new byte[length];
            await Request.Body.ReadExactlyAsync(buffer, ct);
            return buffer;
        }

        using var ms = new MemoryStream();
        await Request.Body.CopyToAsync(ms, ct);
        return ms.ToArray();
    }

    private IActionResult TooLarge(int maxMb) =>
        StatusCode(StatusCodes.Status413PayloadTooLarge,
            new { error = $"Backup exceeds {maxMb} MB limit" });

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var list = await _db.CustomerBackups
            .Where(b => b.CustomerId == CustomerId)
            .OrderByDescending(b => b.CreatedAt)
            .Select(b => new
            {
                id = b.Id,
                sizeBytes = b.SizeBytes,
                createdAt = b.CreatedAt,
                isMonthlyMilestone = b.IsMonthlyMilestone,
                machineName = b.MachineName
            })
            .ToListAsync(ct);
        return Ok(list);
    }

    [HttpGet("{id:guid}/download")]
    public async Task<IActionResult> Download(Guid id, CancellationToken ct)
    {
        var b = await _db.CustomerBackups
            .FirstOrDefaultAsync(x => x.Id == id && x.CustomerId == CustomerId, ct);
        if (b is null) return NotFound();

        var encrypted = await _storage.ReadBlobAsync(b.BlobPath, ct);
        var plaintext = _storage.Decrypt(encrypted, b.KeyVersion);
        return File(plaintext, "application/octet-stream", $"orderdeck-backup-{b.CreatedAt:yyyyMMdd-HHmmss}.zip");
    }

    [HttpDelete("{id:guid}")]
    [EnableRateLimiting("backup-delete")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var b = await _db.CustomerBackups
            .FirstOrDefaultAsync(x => x.Id == id && x.CustomerId == CustomerId, ct);
        if (b is null) return NotFound();

        // Sıra kritik: ÖNCE satır, SONRA dosya. Ters sırada, dosya silindikten
        // sonra SaveChanges patlarsa müşteride "hayalet yedek" kalır — listede
        // görünür, geri yüklemeye kalktığı an yoktur. Bu sırada ise en kötü
        // ihtimalle yetim bir dosya kalır; onu BackupOrphanCleanupJob toplar.
        var blobPath = b.BlobPath;
        _db.CustomerBackups.Remove(b);
        await _db.SaveChangesAsync(ct);
        _storage.DeleteBlob(blobPath);

        await _audit.LogAsync(BackupAuditEvents.BackupDeleted,
            BackupAuditEvents.TargetType, id.ToString(),
            new { reason = "manual" }, ct);

        return NoContent();
    }
}
