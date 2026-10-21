using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Services.Observability;
using OrderDeck.LicenseServer.Services.Privacy;
using OrderDeck.LicenseServer.Services.Shoppers;

namespace OrderDeck.LicenseServer.Services.Audit;

public sealed class SecurityDataRetentionOptions
{
    /// <summary>Kaç gün sonra IP / User-Agent alanları anonimleştirilsin.
    /// 0 = kapalı. Varsayılan 90, gizlilik politikasındaki "Güvenlik kayıtları:
    /// 90 gün" taahhüdüyle birebir aynı olmak zorunda — birini değiştiren
    /// diğerini de değiştirmeli.</summary>
    public int RetentionDays { get; set; } = 90;

    /// <summary>Tek turda güncellenecek satır sayısı. Transaction'ı küçük
    /// tutar; iş bitene kadar döngü devam eder.</summary>
    public int BatchSize { get; set; } = 1000;

    /// <summary>İşlenmiş silme talebindeki telefonun (<c>ShopperDeletionRequest.PhoneAtRequest</c>) ve kapanmış banka veri
    /// talebindeki kısaltılmış IBAN'ın (<c>BankSubjectRequest.IbanMasked</c>) boşaltılacağı gün, işlenme/kapanış tarihinden.
    /// 0 = kapalı. <see cref="RetentionDays"/>'ten bağımsız. KVKK ön inceleme D-5, Ö-11; gizlilik politikasına yazılmalı.</summary>
    public int DeletionRequestPhoneDays { get; set; } = 30;
}

/// <summary>
/// Hangfire recurring job — 90 günden eski güvenlik kayıtlarındaki IP ve
/// User-Agent alanlarını SATIRI SİLMEDEN boşaltır.
///
/// Neden silme değil de anonimleştirme: gizlilik politikası yalnızca IP ve
/// giriş zamanı için 90 gün taahhüt ediyor, oysa aynı satırlar audit izinin
/// (24 ay, <see cref="AuditRetentionJobs"/>) ve mali kayıtların taşıyıcısı.
/// Satırı silmek taahhüdün gerektirmediği bir kaybı doğururdu; sütunu
/// boşaltmak ikisini birden karşılıyor.
///
/// Ayrıca işlenmiş silme talebinin telefonu işlenmeden ve kapanmış banka veri talebinin kısaltılmış IBAN'ı kapanıştan
/// <see cref="SecurityDataRetentionOptions.DeletionRequestPhoneDays"/> gün sonra boşaltılır (KVKK D-5, Ö-11): talebin satırı
/// kanıt olarak kalır, bekleyen/açık talebe dokunulmaz. Bu süre <see cref="SecurityDataRetentionOptions.RetentionDays"/>
/// kapalıyken de işler.
///
/// Her boşaltma partisi kişisel veri içermeyen imha kaydını (<see cref="DataDestruction"/>; hedef çoğul DbSet adı + sütunlar)
/// boşaltmayla aynı işlemde yazar; başarılı koşu sonda koşu kaydı (<see cref="JobId"/>) bırakır; iş düşerse yöneticiye e-posta
/// gider (<see cref="AdminJobAlerts"/>) ve istisna Hangfire'a çıkar (KVKK Ö-13, iç imha prosedürü §4.2-3).
///
/// Kapsam dışı bıraktıklarım ve gerekçeleri:
///   • ShopperPasswordResetCode.RequestIp — satırın kendisi 7 günde
///     PasswordResetCodeCleanupJob tarafından siliniyor, zaten uyumlu.
///   • CustomerBackup.MachineName — yedek listesinde "hangi makine"
///     bilgisini gösteren işlevsel alan, güvenlik kaydı değil.
///
/// Job idempotent: WHERE koşulu zaten boşaltılmış satırları dışlıyor, bu
/// yüzden her gece aynı satırları tekrar yazmaz (ve imha kaydında yeniden saymaz).
/// </summary>
public sealed class SecurityDataRetentionJob
{
    /// <summary>Hangfire iş kimliği; koşu kaydının hedefi de bu (<see cref="DataDestruction.RecordRun"/>).</summary>
    public const string JobId = "security-data-retention";
    public const string FailureSubject = "[OrderDeck] Güvenlik kaydı saklama işi düştü";

    /// <summary>İmha kaydı hedefleri (çoğul DbSet adı + sütun): dış özet, geri yükleme prosedürü ve avukat paketi hedefe göre
    /// gruplar.</summary>
    public const string DeletionRequestPhoneTarget = "ShopperDeletionRequests.PhoneAtRequest";
    public const string BankSubjectRequestMaskTarget = "BankSubjectRequests.IbanMasked";

    private readonly LicenseDbContext _db;
    private readonly SecurityDataRetentionOptions _opt;
    private readonly AdminJobAlerts _alerts;
    private readonly ILogger<SecurityDataRetentionJob> _log;

    public SecurityDataRetentionJob(
        LicenseDbContext db,
        IOptions<SecurityDataRetentionOptions> opt,
        AdminJobAlerts alerts,
        ILogger<SecurityDataRetentionJob> log)
    {
        _db = db;
        _opt = opt.Value;
        _alerts = alerts;
        _log = log;
    }

    /// <summary>Düşerse yöneticiye e-posta (KVKK Ö-13) ve istisna Hangfire'a çıkar (koşu Failed).</summary>
    [AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
    public async Task AnonymiseAsync(CancellationToken ct)
    {
        if (_opt.DeletionRequestPhoneDays <= 0 && _opt.RetentionDays <= 0)
        {
            _log.LogInformation("[SecurityDataRetention] devre dışı (RetentionDays=0, DeletionRequestPhoneDays=0)");
            return;
        }

        try
        {
            await AnonymiseCoreAsync(ct);
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
        {
            _log.LogError(ex, "[SecurityDataRetention] düştü");
            await _alerts.SendAsync(FailureSubject,
                $"{JobId} düştü: {ex.GetType().Name}. Hangfire panosuna bakın; iş ertesi gün yeniden koşar, o güne kadar " +
                "güvenlik kayıtlarındaki IP/User-Agent ve silme taleplerindeki telefonun saklama süresi aşılmış olur.",
                CancellationToken.None);
            throw;
        }
    }

    private async Task AnonymiseCoreAsync(CancellationToken ct)
    {
        var relational = _db.Database.IsRelational();

        // KVKK D-5: işlenmiş silme talebinin telefonu (kendi süresi; RetentionDays kapalıyken de). Bekleyen talebe dokunulmaz.
        if (_opt.DeletionRequestPhoneDays > 0)
        {
            var days = _opt.DeletionRequestPhoneDays;
            var requestCutoff = DateTimeOffset.UtcNow.AddDays(-days);
            var phones = await RunAsync(
                DeletionRequestPhoneTarget, $"HandledAt+{days}g, işlenmiş talep",
                () => _db.ShopperDeletionRequests.Where(
                    r => r.HandledAt != null && r.HandledAt < requestCutoff && r.PhoneAtRequest != ""),
                q => q.ExecuteUpdateAsync(s => s.SetProperty(r => r.PhoneAtRequest, ""), ct),
                rows => { foreach (var r in rows) r.PhoneAtRequest = ""; },
                relational, ct);
            if (phones > 0)
                _log.LogInformation("[SecurityDataRetention] {Count} işlenmiş silme talebinin telefonu boşaltıldı", phones);

            // KVKK Ö-11: kapanmış banka veri talebinin kısaltılmış IBAN'ı (özet kapanışta boşaldı). Açık talebe dokunulmaz.
            // ExecuteUpdate kapanış anına (ClosedAt, eşzamanlılık jetonu) dokunmaz; InMemory yolunda da yalnız maske değişir.
            var masks = await RunAsync(
                BankSubjectRequestMaskTarget, $"ClosedAt+{days}g, kapanmış talep",
                () => _db.BankSubjectRequests.Where(
                    r => r.ClosedAt != null && r.ClosedAt < requestCutoff && r.IbanMasked != ""),
                q => q.ExecuteUpdateAsync(s => s.SetProperty(r => r.IbanMasked, ""), ct),
                rows => { foreach (var r in rows) r.IbanMasked = ""; },
                relational, ct);
            if (masks > 0)
                _log.LogInformation("[SecurityDataRetention] {Count} kapanmış banka veri talebinin kısaltılmış IBAN'ı boşaltıldı", masks);
        }

        if (_opt.RetentionDays > 0)
            await AnonymiseSecurityFieldsAsync(relational, ct);
        else
            _log.LogInformation("[SecurityDataRetention] IP/User-Agent boşaltması devre dışı (RetentionDays=0)");

        // Kesilen koşu (Hangfire kapanışı) partilerin döngüsünden sessizce çıkar: başarılı koşu kaydı yazılmasın
        // (audit-retention kalıbı). Ölçüt metninde ';' yok: dış kopyanın (CSV) ayracı.
        ct.ThrowIfCancellationRequested();
        DataDestruction.RecordRun(_db, JobId, $"IP/UA +{_opt.RetentionDays}g / silme talebi telefonu ve banka talebi maskesi " +
            $"+{_opt.DeletionRequestPhoneDays}g");
        await _db.SaveChangesAsync(ct);
        _db.ChangeTracker.Clear();
    }

    /// <summary>90 günden eski güvenlik kayıtlarındaki IP ve User-Agent alanları (politikadaki "Güvenlik kayıtları: 90 gün").
    /// Sorgular önceki sürümle aynı; her parti imha kaydı bırakır.</summary>
    private async Task AnonymiseSecurityFieldsAsync(bool relational, CancellationToken ct)
    {
        var days = _opt.RetentionDays;
        var cutoff = DateTimeOffset.UtcNow.AddDays(-days);

        var audit = await RunAsync(
            "AuditLogs.IpAddress", $"OccurredAt+{days}g",
            () => _db.AuditLogs.Where(a => a.OccurredAt < cutoff && a.IpAddress != null),
            q => q.ExecuteUpdateAsync(s => s.SetProperty(a => a.IpAddress, (string?)null), ct),
            rows => { foreach (var r in rows) r.IpAddress = null; },
            relational, ct);

        var refreshTokens = await RunAsync(
            "RefreshTokens.CreatedByIp", $"CreatedAt+{days}g",
            () => _db.RefreshTokens.Where(t => t.CreatedAt < cutoff && t.CreatedByIp != null),
            q => q.ExecuteUpdateAsync(s => s.SetProperty(t => t.CreatedByIp, (string?)null), ct),
            rows => { foreach (var r in rows) r.CreatedByIp = null; },
            relational, ct);

        var shopperTokens = await RunAsync(
            "ShopperRefreshTokens.CreatedByIp", $"CreatedAt+{days}g",
            () => _db.ShopperRefreshTokens.Where(t => t.CreatedAt < cutoff && t.CreatedByIp != null),
            q => q.ExecuteUpdateAsync(s => s.SetProperty(t => t.CreatedByIp, (string?)null), ct),
            rows => { foreach (var r in rows) r.CreatedByIp = null; },
            relational, ct);

        var intake = await RunAsync(
            "IntakeFormSubmissions.IpAddress+UserAgent", $"SubmittedAt+{days}g",
            () => _db.IntakeFormSubmissions.Where(
                s => s.SubmittedAt < cutoff && (s.IpAddress != null || s.UserAgent != null)),
            q => q.ExecuteUpdateAsync(s => s
                .SetProperty(x => x.IpAddress, (string?)null)
                .SetProperty(x => x.UserAgent, (string?)null), ct),
            rows => { foreach (var r in rows) { r.IpAddress = null; r.UserAgent = null; } },
            relational, ct);

        // Mali kayıt: satır ve tutar duruyor, yalnız istemci parmak izi gidiyor.
        // Sütunlar non-nullable olduğu için null yerine boş string yazılıyor. Hedef purge'ünkiyle aynı sabit: aynı sütunun
        // imhası tek hedef adıyla gruplanır.
        var paymentAudit = await RunAsync(
            ShopperPurgeService.SubmissionAuditFingerprintTarget, $"CreatedAt+{days}g",
            () => _db.PaymentSubmissionAudits.Where(
                a => a.CreatedAt < cutoff && (a.IpAddress != "" || a.UserAgent != "")),
            q => q.ExecuteUpdateAsync(s => s
                .SetProperty(x => x.IpAddress, "")
                .SetProperty(x => x.UserAgent, ""), ct),
            rows => { foreach (var r in rows) { r.IpAddress = ""; r.UserAgent = ""; } },
            relational, ct);

        var total = audit + refreshTokens + shopperTokens + intake + paymentAudit;
        if (total > 0)
            _log.LogInformation(
                "[SecurityDataRetention] {Cutoff} öncesi {Total} satır anonimleştirildi "
                + "(audit={Audit}, refresh={Refresh}, shopperRefresh={ShopperRefresh}, "
                + "intake={Intake}, paymentAudit={PaymentAudit})",
                cutoff, total, audit, refreshTokens, shopperTokens, intake, paymentAudit);
        else
            _log.LogDebug("[SecurityDataRetention] {Cutoff} öncesi anonimleştirilecek satır yok", cutoff);
    }

    /// <summary>
    /// Tek tablo için batch'li boşaltma. Her parti kişisel veri içermeyen imha kaydını (hedef = <paramref name="target"/>,
    /// <c>nulled</c>, ölçüt, tetikleyici <c>retention</c>, yapan <c>system</c>) boşaltmayla AYNI işlemde yazar (KVKK Ö-13; Görev 9
    /// kuralı). Relational sağlayıcıda ExecuteUpdate + kayıt açık bir işlemde (AuditRetentionJobs'taki kalıp), InMemory'de
    /// (testler) load+set ve kayıt tek SaveChanges'te. Sıfır satırlık parti kayıt yazmaz.
    /// </summary>
    private async Task<int> RunAsync<TEntity>(
        string target,
        string criterion,
        Func<IQueryable<TEntity>> query,
        Func<IQueryable<TEntity>, Task<int>> bulkUpdate,
        Action<List<TEntity>> inMemoryUpdate,
        bool relational,
        CancellationToken ct) where TEntity : class
    {
        void Record(int count) => DataDestruction.Record(_db, null, target, DataDestruction.Actions.Nulled, count, criterion,
            DataDestruction.Triggers.Retention, DataDestruction.SystemActor);

        var total = 0;
        while (!ct.IsCancellationRequested)
        {
            int affected;
            if (relational)
            {
                // ExecuteUpdate WHERE'i zaten boşaltılmışları dışlıyor, yani her tur kalan işi küçültüyor ve döngü kesin
                // sonlanıyor. Kayıt boşaltmayla aynı işlemde: biri olup öbürü olmasın.
                await using var tx = await _db.Database.BeginTransactionAsync(ct);
                affected = await bulkUpdate(query().Take(_opt.BatchSize));
                Record(affected);
                await _db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
            }
            else
            {
                var rows = await query().Take(_opt.BatchSize).ToListAsync(ct);
                inMemoryUpdate(rows);
                affected = rows.Count;
                Record(affected);
                await _db.SaveChangesAsync(ct);
            }
            _db.ChangeTracker.Clear();
            total += affected;
            if (affected < _opt.BatchSize) break;
        }

        if (total > 0)
            _log.LogDebug("[SecurityDataRetention] {Target}: {Count} satır", target, total);

        return total;
    }
}
