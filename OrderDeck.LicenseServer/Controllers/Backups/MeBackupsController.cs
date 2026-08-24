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
    private readonly Microsoft.Extensions.Options.IOptions<BackupOptions> _opt;
    private readonly BackupUploadThrottle _throttle;
    private readonly OrderDeck.LicenseServer.Services.Observability.OrderDeckMetrics _metrics;
    private readonly ILogger<MeBackupsController> _log;

    public MeBackupsController(
        LicenseDbContext db,
        BackupStorageService storage,
        BackupRetentionService retention,
        IAuditService audit,
        Microsoft.Extensions.Options.IOptions<BackupOptions> opt,
        BackupUploadThrottle throttle,
        OrderDeck.LicenseServer.Services.Observability.OrderDeckMetrics metrics,
        ILogger<MeBackupsController> log)
    {
        _db = db;
        _storage = storage;
        _retention = retention;
        _audit = audit;
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

        try
        {
            return await StoreAsync(sha, maxMb, maxBytes, ct);
        }
        finally
        {
            _throttle.Exit();
        }
    }

    /// <summary>
    /// Gövdeyi <b>akıtarak</b> diske şifreler: istek gövdesi → parçalı AES-GCM →
    /// blob dosyası. Bellekte hiçbir zaman iki parçadan (2 MiB) fazlası durmuyor.
    ///
    /// <para>Eskiden sıra tersineydi: önce tüm gövde bir diziye, sonra şifreli
    /// zarf ikinci bir diziye alınıyordu — istek başına <b>2 × blob</b>. 64 MB
    /// tavan ve 2 eşzamanlı yükleme ile bu, 1 GB'lık konteynerde 256 MB tepe
    /// demekti; üstelik 64 MB'lık diziler büyük nesne yığınına düşüyor ve orası
    /// varsayılan olarak SIKIŞTIRILMIYOR, yani aritmetik tepenin altında da
    /// parçalanmayla tükenilebiliyordu.</para>
    ///
    /// <para><b>Doğrulama neden yazımdan SONRA:</b> düz metnin özetini akıtmadan
    /// hesaplamanın yolu yok — baytları ikinci kez görebilmek için hepsini
    /// bellekte ya da diskte tutmak, yani düzeltmeye çalıştığımız şeyi geri
    /// getirmek gerekirdi. Doğrulama düşerse blob siliniyor; süreç tam o anda
    /// ölürse geriye satırı olmayan bir dosya kalır ve onu
    /// <see cref="OrderDeck.LicenseServer.Services.Backup.BackupOrphanCleanupJob"/>
    /// zaten topluyor. Ters tercih (önce satır) hayalet yedek üretirdi.</para>
    /// </summary>
    private async Task<IActionResult> StoreAsync(string sha, int maxMb, long maxBytes, CancellationToken ct)
    {
        var quotaMb = _opt.Value.PerCustomerQuotaMb;
        var quotaBytes = quotaMb * 1024L * 1024L;

        // Content-Length varsa kotayı tek bayt yazmadan reddedebiliyoruz;
        // yoksa (chunked) ancak yazdıktan sonra ölçebiliriz. Bu ilk bakış
        // BİLEREK korumasız: amacı yalnızca kesin reddedilecek bir gövdeyi
        // boşuna akıtmamak. Bağlayıcı karar aşağıda, hakemin arkasında.
        if (quotaMb > 0 && Request.ContentLength is { } declared)
        {
            var quickSum = await _db.CustomerBackups
                .Where(b => b.CustomerId == CustomerId)
                .SumAsync(b => (long?)b.SizeBytes, ct) ?? 0L;
            if (quickSum + declared > quotaBytes)
                return QuotaExceeded(quotaMb);
        }

        var blobPath = _storage.NewBlobPath(CustomerId);
        EncryptStreamResult result;
        try
        {
            await using var blob = new FileStream(
                blobPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 64 * 1024, useAsync: true);
            result = await _storage.EncryptToAsync(Request.Body, blob, maxBytes, ct);
        }
        catch (BackupTooLargeException)
        {
            // Savunma derinliği: Content-Length yoksa ya da yalan söylediyse VE
            // sunucu gövde sınırını uygulamıyorsa (TestServer'da bu özellik yok)
            // tek kalan ölçüm bu.
            _storage.DeleteBlob(blobPath);
            return TooLarge(maxMb);
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            // Kestrel'in gövde sınırı tetiklendi. Ham hata yerine kendi gövdemizi
            // dönüyoruz ki istemci iki yolda da aynı yanıtı görsün.
            _storage.DeleteBlob(blobPath);
            return TooLarge(maxMb);
        }
        catch (BadHttpRequestException)
        {
            // Content-Length söylediğinden az bayt geldi / bağlantı yarıda koptu.
            // Yarım gövdeyi yedek diye kaydetmektense reddetmek şart.
            _storage.DeleteBlob(blobPath);
            return BadRequest(new { error = "Request body ended before Content-Length" });
        }
        catch
        {
            _storage.DeleteBlob(blobPath);
            throw;
        }

        if (!string.Equals(result.PlaintextSha256Hex, sha, StringComparison.OrdinalIgnoreCase))
        {
            _storage.DeleteBlob(blobPath);
            return BadRequest(new { error = "SHA256 mismatch — body integrity check failed" });
        }

        // Per-customer storage quota. Retention prunes oldest non-milestones to 5,
        // but a customer accumulating monthly milestones for years could still drift
        // past a sane budget.
        //
        // Sıra kritik ve şöyle okunmalı: hakem satırı ÖNCE okunuyor (rowversion
        // yakalanıyor), toplam SONRA alınıyor. Aynı müşterinin eşzamanlı ikinci
        // yüklemesi ya (a) hakemi okumamızdan önce yazmıştır — o zaman toplamda
        // görünür — ya da (b) sonra yazar, o zaman aşağıdaki SaveChanges damga
        // uyuşmazlığıyla düşer. Üçüncü bir hâl yok; eski kodda ikisi de bayat
        // toplamı okuyup ikisi de geçebiliyordu ve aşan baytlar diskte kaldığı
        // için kota bir daha kendiliğinden toparlanmıyordu.
        if (quotaMb > 0)
        {
            var guard = await _db.BackupQuotaCounters
                .FirstOrDefaultAsync(g => g.CustomerId == CustomerId, ct);
            if (guard is null)
            {
                guard = new BackupQuotaCounter { CustomerId = CustomerId };
                _db.BackupQuotaCounters.Add(guard);
            }

            var usedBytes = await _db.CustomerBackups
                .Where(b => b.CustomerId == CustomerId)
                .SumAsync(b => (long?)b.SizeBytes, ct) ?? 0L;

            if (usedBytes + result.EnvelopeBytes > quotaBytes)
            {
                _storage.DeleteBlob(blobPath);
                return QuotaExceeded(quotaMb);
            }

            guard.Ticket++;
        }

        var backup = new CustomerBackup
        {
            Id = Guid.NewGuid(),
            CustomerId = CustomerId,
            BlobPath = blobPath,
            SizeBytes = result.EnvelopeBytes,
            ChecksumSha256 = result.PlaintextSha256Hex,
            CreatedAt = DateTimeOffset.UtcNow,
            IsMonthlyMilestone = false,
            UserAgent = Request.Headers["User-Agent"].ToString(),
            MachineName = Request.Headers["X-Machine-Name"].ToString(),
            KeyVersion = result.KeyVersion,
            EnvelopeFormat = BackupStorageService.FormatChunked
        };
        _db.CustomerBackups.Add(backup);
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Kota hakemini kaybettik: ya damga uyuşmadı (satır zaten vardı) ya
            // da ilk satırı ikimiz birden yazmaya çalıştık ve biri PK çakışması
            // aldı. İkinci hâl DbUpdateConcurrencyException DEĞİL, o yüzden
            // geniş tür yakalanıyor — dar yakalasaydık 409'un var olma sebebi
            // olan pencerede 500 dönerdi (emsal: PanelBarcodesController).
            // Yedek satırı aynı SaveChanges'te olduğu için o da geri alındı;
            // geriye yalnız blob kalıyor.
            _storage.DeleteBlob(blobPath);
            _log.LogInformation(
                "Yedek yükleme kota hakemini kaybetti (customer={Customer}) — istemci yeniden denemeli",
                CustomerId);
            return Conflict(new
            {
                error = "backup-quota-busy",
                detail = "Aynı anda başka bir yedek yükleniyordu; tekrar dene."
            });
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
        _metrics.BackupUploadBytes.Record(result.EnvelopeBytes);

        await _retention.EnforceAfterInsertAsync(CustomerId, backup.Id, ct);

        // Re-load to capture milestone flag (retention may have set it)
        var saved = await _db.CustomerBackups.FindAsync(new object[] { backup.Id }, ct);

        // Saha dışı kopyalama burada YAPILMIYOR. Eskiden `Task.Run` ile
        // ateşlenip unutuluyordu: kopyalandığına dair kayıt yoktu, yeniden
        // deneme yoktu ve süreç yeniden başlarsa (her deploy) uçuştaki iş
        // sessizce kayboluyordu. Artık gecelik cron `aws s3 sync` yapıyor —
        // artımlı ve tekrar güvenli, kaçan dosya ertesi gece gidiyor.
        // Bkz. deploy/scripts/backup-blobs-to-r2.sh, HA-PLAYBOOK G6.

        await _audit.LogAsync(BackupAuditEvents.BackupCreated,
            BackupAuditEvents.TargetType,
            backup.Id.ToString(),
            new { sizeBytes = result.EnvelopeBytes, isMonthlyMilestone = saved!.IsMonthlyMilestone },
            ct);

        return StatusCode(StatusCodes.Status201Created, new
        {
            id = saved.Id,
            sizeBytes = saved.SizeBytes,
            createdAt = saved.CreatedAt,
            isMonthlyMilestone = saved.IsMonthlyMilestone
        });
    }

    private IActionResult TooLarge(int maxMb) =>
        StatusCode(StatusCodes.Status413PayloadTooLarge,
            new { error = $"Backup exceeds {maxMb} MB limit" });

    private IActionResult QuotaExceeded(long quotaMb) =>
        StatusCode(StatusCodes.Status507InsufficientStorage,
            new { error = $"Per-customer backup quota exceeded ({quotaMb} MB). Delete older backups via /api/v1/me/backups/{{id}}." });

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

    /// <summary>
    /// Yedeği çözüp <b>akıtarak</b> döner.
    ///
    /// <para>Bu uç, yükleme yolunun aksine ne oran sınırına ne de eşzamanlılık
    /// kapısına takılıyor (yalnız IP başına 100/dk'lık genel sel kapağı var) —
    /// yani zarfı ve düz metni birlikte belleğe alan eski hâlde istek başına
    /// <b>2 × blob</b> tutan ve hiçbir tavanı olmayan taraf tam olarak burasıydı.
    /// Denetim raporu yalnız yükleme yolunu işaret ediyordu; ölçüsüz olan
    /// aslında bu.</para>
    /// </summary>
    [HttpGet("{id:guid}/download")]
    public async Task<IActionResult> Download(Guid id, CancellationToken ct)
    {
        var b = await _db.CustomerBackups
            .FirstOrDefaultAsync(x => x.Id == id && x.CustomerId == CustomerId, ct);
        if (b is null) return NotFound();

        await using var envelope = _storage.OpenBlobRead(b.BlobPath);

        // Başlıklar ilk bayttan ÖNCE yazılıyor: gövde akmaya başladıktan sonra
        // başlık eklenemez, ve hata olursa yanıt zaten yarım gider.
        Response.ContentType = "application/octet-stream";
        Response.Headers.ContentDisposition =
            $"attachment; filename=\"orderdeck-backup-{b.CreatedAt:yyyyMMdd-HHmmss}.zip\"";

        await _storage.DecryptToAsync(envelope, b.EnvelopeFormat, b.KeyVersion, Response.Body, ct);
        return new EmptyResult();
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
