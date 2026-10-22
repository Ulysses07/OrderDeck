using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Audit;
using OrderDeck.LicenseServer.Services.Auth;
using OrderDeck.LicenseServer.Services.Backup;
using OrderDeck.LicenseServer.Services.Privacy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace OrderDeck.LicenseServer.Controllers.Customers;

[ApiController]
[Route("api/v1/admin/customers")]
[Authorize(AuthenticationSchemes = "Bearer-Admin")]
public sealed class AdminCustomersController : ControllerBase
{
    private readonly LicenseDbContext _db;
    private readonly PasswordHasher _hasher;
    private readonly BackupStorageService _backups;
    private readonly IAuditService _audit;

    public AdminCustomersController(LicenseDbContext db, PasswordHasher hasher,
        BackupStorageService backups, IAuditService audit)
    {
        _db = db;
        _hasher = hasher;
        _backups = backups;
        _audit = audit;
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var rows = await _db.Customers
            .OrderByDescending(c => c.CreatedAt)
            .Select(c => new
            {
                id = c.Id,
                email = c.Email,
                name = c.Name,
                emailConfirmedAt = c.EmailConfirmedAt,
                createdAt = c.CreatedAt,
                licenseCount = c.Licenses.Count
            })
            .ToListAsync(ct);
        return Ok(rows);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var c = await _db.Customers
            .Include(x => x.Licenses)
            .FirstOrDefaultAsync(x => x.Id == id, ct);
        if (c is null) return NotFound();
        return Ok(new
        {
            id = c.Id,
            email = c.Email,
            name = c.Name,
            emailConfirmedAt = c.EmailConfirmedAt,
            createdAt = c.CreatedAt,
            notes = c.Notes,
            licenses = c.Licenses.Select(l => new
            {
                id = l.Id,
                licenseKey = l.LicenseKey,
                skuCode = l.SkuCode,
                expiresAt = l.ExpiresAt,
                revokedAt = l.RevokedAt
            })
        });
    }

    public sealed record CreateRequest(string Email, string Name, string? InitialPassword, bool? AutoConfirm);

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Email) || string.IsNullOrWhiteSpace(req.Name))
            return Problem(title: "missing-fields", statusCode: 400);

        var existing = await _db.Customers.FirstOrDefaultAsync(c => c.Email == req.Email, ct);
        if (existing is not null) return Conflict(new { error = "email-exists" });

        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            Email = req.Email,
            Name = req.Name,
            PasswordHash = _hasher.Hash(req.InitialPassword ?? Guid.NewGuid().ToString("N")),
            EmailConfirmedAt = (req.AutoConfirm ?? false) ? DateTimeOffset.UtcNow : null,
            CreatedAt = DateTimeOffset.UtcNow
        };
        _db.Customers.Add(customer);
        await _db.SaveChangesAsync(ct);

        return CreatedAtAction(nameof(Get), new { id = customer.Id }, new { id = customer.Id });
    }

    [HttpPost("{id:guid}/confirm-email")]
    public async Task<IActionResult> ConfirmEmail(Guid id, CancellationToken ct)
    {
        var c = await _db.Customers.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (c is null) return NotFound();
        if (c.EmailConfirmedAt is null)
        {
            c.EmailConfirmedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(ct);
        }
        return NoContent();
    }

    public sealed record PurgeRequest(string ConfirmEmail);

    /// <summary>
    /// KVKK / GDPR right-to-be-forgotten. Deletes the customer's operational
    /// data (licenses, activations, refresh tokens, intake form submissions,
    /// email logs, panel push devices, backup blobs + metadata, the data-controller profile and its versions — KVKK K-3)
    /// and anonymises the Customer row
    /// itself. The Customer row stays — its Id is referenced from AuditLog
    /// rows that we MUST retain for the configured retention window. Email
    /// + Name are blanked, PasswordHash is set to a sentinel that no hash
    /// can ever match, EmailConfirmedAt is cleared.
    ///
    /// Caller must echo the customer's current email in the request body to
    /// guard against fat-finger purges. Audit row is written with the
    /// pre-purge email so the action remains traceable.
    /// The audit row (with the e-mail) is pruned at 24 months like any other; the 3-year destruction record is the PII-free
    /// DataDestructionRecords rows written in the same SaveChanges (KVKK Ö-13).
    /// Obifin banka bağlantısı olan lisans varken 409 bank-connection-active: önce admin/obifin → "Kaldır ve sil" (KVKK K-6, Ö-9).
    /// Lisanslardaki açık banka veri talepleri kapanır, satırları belge olarak kalır (KVKK K-7).
    /// </summary>
    [HttpPost("{id:guid}/purge")]
    public async Task<IActionResult> Purge(Guid id, [FromBody] PurgeRequest req, CancellationToken ct)
    {
        var customer = await _db.Customers.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (customer is null) return NotFound();
        if (!string.Equals(customer.Email, req.ConfirmEmail, StringComparison.OrdinalIgnoreCase))
            return Problem(title: "email-mismatch",
                detail: "Body confirmEmail must match the customer's current email.",
                statusCode: 400);

        // KVKK K-6/Ö-9: banka tabloları lisansla kaskadla gider ama Obifin'e bir şey söylenmez — Obifin bankadan çekmeyi ve
        // saklamayı sürdürür. Obifin bağlantısı olan lisans varken silme reddedilir: Obifin tarafı (lisansa özgü kaldırma,
        // paylaşılan kullanıcı koruması, yazılı teyit kaydı) yalnız admin/obifin'deki "Kaldır ve sil"de kapanır. Purge içinde
        // ağ çağrısı yapılmaz.
        var customerLicenseIds = _db.Licenses.Where(l => l.CustomerId == id).Select(l => l.Id);
        var bankLicenses = await _db.ObifinConnections.CountAsync(c => customerLicenseIds.Contains(c.LicenseId), ct);
        if (bankLicenses > 0)
            return Problem(title: "bank-connection-active", statusCode: 409,
                detail: $"Bu yayıncının {bankLicenses} lisansında Obifin banka bağlantısı var. Önce admin/obifin'de her biri için " +
                    "'Kaldır ve sil' çalıştırın ve Obifin'den hareket kopyasının silindiğine dair yazılı teyit isteyin; sonra " +
                    "silmeyi yineleyin.");

        var preEmail = customer.Email;

        // 1. Collect the encrypted backup blob paths. The files are deleted
        //    AFTER SaveChanges, not before: if we wiped the files first and the
        //    save then failed, the customer would keep rows pointing at blobs
        //    that no longer exist — backups they can see but never restore.
        //    Holding the paths in a local list costs nothing.
        var backupRows = await _db.CustomerBackups
            .Where(b => b.CustomerId == id).ToListAsync(ct);
        var backupBlobPaths = backupRows.Select(b => b.BlobPath).ToList();

        // 2. Hard-delete dependent rows. FK cascade would handle Activations
        //    via Licenses, but doing it explicitly keeps the operation
        //    self-documenting and survives any future cascade changes.
        var licenseIds = await _db.Licenses.Where(l => l.CustomerId == id)
            .Select(l => l.Id).ToListAsync(ct);
        if (licenseIds.Count > 0)
        {
            _db.Activations.RemoveRange(_db.Activations.Where(a => licenseIds.Contains(a.LicenseId)));
            _db.Licenses.RemoveRange(_db.Licenses.Where(l => l.CustomerId == id));
        }
        _db.CustomerBackups.RemoveRange(backupRows);
        _db.EmailLogs.RemoveRange(_db.EmailLogs.Where(e => e.CustomerId == id));
        _db.RefreshTokens.RemoveRange(_db.RefreshTokens.Where(r => r.CustomerId == id));
        _db.PasswordResetTokens.RemoveRange(_db.PasswordResetTokens.Where(p => p.CustomerId == id));
        _db.EmailConfirmationTokens.RemoveRange(_db.EmailConfirmationTokens.Where(t => t.CustomerId == id));
        // KVKK K-3: veri sorumlusu bilgileri ve sürümleri (şahıs yayıncıda ad ve adres) yayıncının verisidir (karar 24). Müşteri
        // satırı anonimleştirilip kaldığı için FK cascade'i tetiklenmez, açıkça silinir.
        _db.DataControllerProfiles.RemoveRange(_db.DataControllerProfiles.Where(p => p.CustomerId == id));
        _db.DataControllerProfileVersions.RemoveRange(_db.DataControllerProfileVersions.Where(v => v.CustomerId == id));
        // Panel push devices (device id + FCM/APNS token). The FK cascades on Customer delete, but the Customer row is
        // anonymised and kept — the cascade never fires, so they go explicitly.
        _db.PushDevices.RemoveRange(_db.PushDevices.Where(d => d.CustomerId == id));
        // Intake form submissions reference IntakeFormConfig (not Customer directly).
        // Drop the customer's config + all submissions that fed into it.
        var configIds = await _db.IntakeFormConfigs
            .Where(c => c.CustomerId == id)
            .Select(c => c.Id)
            .ToListAsync(ct);
        if (configIds.Count > 0)
        {
            _db.IntakeFormSubmissions.RemoveRange(
                _db.IntakeFormSubmissions.Where(s => configIds.Contains(s.IntakeFormConfigId)));
            _db.IntakeFormConfigs.RemoveRange(_db.IntakeFormConfigs.Where(c => c.CustomerId == id));
        }

        // 3. Anonymise the Customer row. Email is moved to a deterministic
        //    placeholder so AuditLog rows still pivot off the same Id but
        //    PII is gone. PasswordHash is set to a value no Argon2 hash can
        //    match (length is invalid for the encoding) so the account can
        //    never be logged into again.
        customer.Email = $"purged-{customer.Id:N}@deleted.invalid";
        customer.Name = "[Deleted]";
        customer.PasswordHash = Customer.PurgedSentinel;
        // R11-S01: purge, yukarıdaki RemoveRange ile token satırlarını siler
        // ama silmenin SELECT'inden sonra düşen bir login insert'i tabloda
        // kalırdı. Nesil ilerlemesi o kaçağı da geçersizler.
        customer.AuthVersion++;
        customer.EmailConfirmedAt = null;
        customer.Unsubscribed = true;
        customer.Notes = null;

        // KVKK Ö-13: kişisel veri İÇERMEYEN kalıcı imha kaydı, silmeyle aynı SaveChanges'te. preEmail BURAYA girmez: e-posta
        // yalnız 24 ayda budanan customer.purged denetim satırında. SubjectId = yayıncı hesabının kimliği (satır anonimleştirilmiş
        // kalır): yedekten geri yüklemeden sonra silme bununla yeniden uygulanır. Sayımlar İZLEYİCİDEN: yukarıdaki RemoveRange'lerin
        // silinmek üzere işaretlediği satırlar, yani tam olarak bu SaveChanges'in sildiği. DB'den ayrıca saymak okuma ile kayıt
        // arasında eklenen satırı (girişin yazdığı yenileme jetonu, herkese açık formdan gelen gönderim) silinmediği hâlde sayardı.
        // Yapan: yöneticinin kimliği, kullanıcı adı değil (tablo budanmaz; adı aşağıdaki denetim satırında, 24 ay).
        var actor = DataDestruction.ActorOf(User);
        const string criterion = "yayıncı silme talebi";
        void Record(string target, string action, int count, string why = criterion) => DataDestruction.Record(_db, null, target,
            action, count, why, DataDestruction.Triggers.CustomerPurge, actor, subjectId: id);
        int Deleted<T>() where T : class => _db.ChangeTracker.Entries<T>().Count(e => e.State == EntityState.Deleted);
        // Lisansa bağlı satırlar (siparişler, katalog, banka verisi …) ayrıca sayılmaz: lisansla birlikte FK cascade'iyle gider.
        Record("Licenses", DataDestruction.Actions.Deleted, Deleted<License>(), criterion + ", bağlı satırlar FK cascade ile");
        Record("Activations", DataDestruction.Actions.Deleted, Deleted<Activation>());
        Record("CustomerBackups", DataDestruction.Actions.Deleted, Deleted<CustomerBackup>());
        Record("EmailLogs", DataDestruction.Actions.Deleted, Deleted<EmailLog>());
        Record("RefreshTokens", DataDestruction.Actions.Deleted, Deleted<RefreshToken>());
        Record("PasswordResetTokens", DataDestruction.Actions.Deleted, Deleted<PasswordResetToken>());
        Record("EmailConfirmationTokens", DataDestruction.Actions.Deleted, Deleted<EmailConfirmationToken>());
        Record("PushDevices", DataDestruction.Actions.Deleted, Deleted<PushDevice>());
        Record("IntakeFormConfigs", DataDestruction.Actions.Deleted, Deleted<IntakeFormConfig>());
        Record("IntakeFormSubmissions", DataDestruction.Actions.Deleted, Deleted<IntakeFormSubmission>());
        Record("Customers.Email+Name+PasswordHash+Notes", DataDestruction.Actions.Nulled, 1);
        Record("DataControllerProfiles", DataDestruction.Actions.Deleted, Deleted<DataControllerProfile>());
        Record("DataControllerProfileVersions", DataDestruction.Actions.Deleted, Deleted<DataControllerProfileVersion>());
        // KVKK K-7: yayıncının lisanslarındaki açık banka veri talepleri kapanır (lisansa FK yok: talep satırı belge olarak kalır,
        // IBAN özeti boşalır, imha kaydı talep başına bu tetikleyiciyle). Yönetici başvurana yine yanıt verir. Aynı kayıtta: talebi arada kapatan başka bir istek (ClosedAt
        // jetonu) bu kaydı düşürür, aşağıdaki 409 ile operatör yeniden çağırır.
        var closedBankSubjectRequests = await OrderDeck.LicenseServer.Services.Bank.BankSubjectRequestService
            .CloseOpenForLicensesAsync(_db, licenseIds, "Yayıncı hesabı silindi; talep kapatıldı — başvurana yanıt verin.",
                DataDestruction.Triggers.CustomerPurge, actor, DateTimeOffset.UtcNow, ct);

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // R12-S01: araya giren bir kimlik geçersizleştirmesi bu purge'ü
            // bayat bırakır. Silmenin yarısını yazıp "tamam" demek yerine
            // çatışmayı bildiriyoruz: operatör tekrar çağırır, purge
            // idempotent. Blob silme aşağıda — buraya düşen istek ona hiç
            // ulaşmaz, yani satırlar dururken dosyalar silinmiş olmaz.
            return Problem(title: "concurrent-credential-change", statusCode: 409);
        }

        // KVKK K-3: veri sorumlusu bilgisini bu silmenin okumasıyla kaydı arasında İLK KEZ yazan bir yazım (açık panel: erişim
        // jetonu silmeden sonra da 15 dk geçerli) yukarıdaki listede yoktu; müşteri satırı kaldığı için FK geçerli, satır
        // kalırdı. Kayıttan sonra yeniden aranır ve silinir. Kayıttan sonra kaydedilmek isteyen yazım ise müşterinin silindiğini
        // görür ya da kimlik nesli koşuluna takılır, yazmaz (DataControllerProfiles.UpsertAsync): bu arama hiçbirini kaçırmaz.
        var lateProfiles = await _db.DataControllerProfiles.Where(p => p.CustomerId == id).ToListAsync(ct);
        var lateVersions = await _db.DataControllerProfileVersions.Where(v => v.CustomerId == id).ToListAsync(ct);
        if (lateProfiles.Count + lateVersions.Count > 0)
        {
            _db.DataControllerProfiles.RemoveRange(lateProfiles);
            _db.DataControllerProfileVersions.RemoveRange(lateVersions);
            const string late = criterion + ", silmeyle eşzamanlı yazılmış";
            Record("DataControllerProfiles", DataDestruction.Actions.Deleted, lateProfiles.Count, late);
            Record("DataControllerProfileVersions", DataDestruction.Actions.Deleted, lateVersions.Count, late);
            await _db.SaveChangesAsync(ct);
        }

        // Rows are gone — now the files. A failure here leaves an orphan blob,
        // which BackupOrphanCleanupJob sweeps; DeleteBlob already logs and
        // swallows so a locked file can't fail an otherwise-completed purge.
        foreach (var path in backupBlobPaths) _backups.DeleteBlob(path);

        await _audit.LogAsync(AuditEvents.CustomerPurged, AuditTargets.Customer, id.ToString(),
            details: new
            {
                preEmail,
                deletedLicenses = licenseIds.Count,
                deletedBackups = backupRows.Count,
                // KVKK m. 13/2: kapanan banka veri talebi açık listeden düşer, başvurana yanıt yükümlülüğü sürer (sayı, kişisel
                // veri değil).
                closedBankSubjectRequests,
            }, ct: ct);

        return NoContent();
    }
}
