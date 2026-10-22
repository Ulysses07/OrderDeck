using Hangfire;
using OrderDeck.LicenseServer.Services.Email;

namespace OrderDeck.LicenseServer.Services.CustomerSync;

/// <summary>
/// <c>person-balance-verify</c> — ortak bakiye günlük denetimi (spec 2026-10-11 §8.1). <c>person-balance verify</c>
/// ile aynı çekirdek (<see cref="PersonBalanceVerifier"/>): her lisansın sayıları günlüğe (kişisel veri yok); tabana
/// göre YENİ sapma varsa <c>Admin:AlertEmail</c>'e tek e-posta (kalıp <c>BackupRestoreDrillJob</c>). Taban
/// alınmamış lisansta eksi/D1/kopya kalemleri yalnız sayılır — kabul edilmiş eski veri her gün uyarıp gerçek bir
/// bozulmayı gizlemesin. Kazanan tasarımın "değişmez kuralı çalışırken denetleyen iş yok" riskini kapatır.
/// Takvim <see cref="Program.SchedulePersonBalanceJobs"/>'ta.
/// </summary>
[DisableConcurrentExecution(timeoutInSeconds: 600)]
[AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
public sealed class PersonBalanceVerifyJob
{
    /// <summary>Hangfire işinin adı (recurring Id).</summary>
    public const string JobId = "person-balance-verify";

    /// <summary>04:58 UTC — yayın penceresi dışında, 04:57 banka saklamasından sonra, ızgara dışında.</summary>
    public const string Cron = "58 4 * * *";

    private readonly PersonBalanceVerifier _verifier;
    private readonly IEmailSender _email;
    private readonly IConfiguration _config;
    private readonly ILogger<PersonBalanceVerifyJob> _log;

    public PersonBalanceVerifyJob(
        PersonBalanceVerifier verifier, IEmailSender email, IConfiguration config, ILogger<PersonBalanceVerifyJob> log)
    {
        _verifier = verifier;
        _email = email;
        _config = config;
        _log = log;
    }

    /// <summary>Hangfire girişi. Raporu döndürmez: Hangfire dönüş değerini depoya yazardı.</summary>
    public async Task RunAsync(CancellationToken ct) => await VerifyAndAlertAsync(ct);

    /// <summary>Denetler, sayıları günlüğe yazar ve yalnız yeni sapmada uyarır. Testler buradan girer. Günlük satırı
    /// kırpma ile sekme/satır sonu sayısını birlikte (S2'nin <c>enable</c>'ı ikisini kullanır, plan fark 15) ve
    /// havuzu &lt; 0 kişilerin yanında tabanla açıklananları (fark 8) taşır.</summary>
    public async Task<PersonBalanceVerifier.Report> VerifyAndAlertAsync(CancellationToken ct)
    {
        var report = PersonBalanceVerifier.Analyze(await _verifier.LoadAsync(null, DateTimeOffset.UtcNow, ct));
        foreach (var l in report.Licenses)
            _log.LogInformation(
                "person-balance-verify: lisans {LicenseId} — taban {HasBaseline}, eksi {NegIn}/{NegOut}, D1 {MisIn}/{MisOut}, "
                + "kopya {CopyIn}/{CopyOut}, D4 {D4} D5 {D5} D6 {D6} yetim {Orphans}, "
                + "havuzu<0 kişi {NegativePersons} (tabanla açıklanan {NegativePersonsExplained}), "
                + "grubu dağılmış silinmiş {Stranded}, kırpma {Trim}, sekme/satır sonu {ControlChars}",
                l.LicenseId, l.HasBaseline, l.NegativeRows.InBaseline, l.NegativeRows.Outside,
                l.LedgerMismatches.InBaseline, l.LedgerMismatches.Outside, l.CopyRows.InBaseline, l.CopyRows.Outside,
                l.D4, l.D5, l.D6, l.OrphanLegs, l.NegativePersons, l.NegativePersonsExplained,
                l.StrandedPurged, l.TrimMismatches, l.ControlCharGroups);

        var alerting = report.Licenses.Where(l => l.Alerts.Count > 0).ToList();
        if (alerting.Count == 0) return report;

        var lines = alerting.Select(l => $"Lisans {l.LicenseId}: {string.Join("; ", l.Alerts)}").ToList();
        foreach (var line in lines) _log.LogWarning("person-balance-verify: YENİ SAPMA — {Line}", line);

        var to = _config["Admin:AlertEmail"];
        if (string.IsNullOrWhiteSpace(to))
        {
            _log.LogWarning("person-balance-verify: yeni sapma var ama Admin:AlertEmail tanımsız — e-posta gönderilmedi");
            return report;
        }

        var plain = "Ortak bakiye günlük denetimi tabana göre yeni sapma buldu (yalnız sayılar; kişisel veri yok).\n\n"
                    + string.Join("\n", lines)
                    + "\n\nAyrıntı: docker exec orderdeck-license dotnet OrderDeck.LicenseServer.dll "
                    + "person-balance verify --license <id> --details";
        var html = "<h2>Ortak bakiye denetimi: yeni sapma</h2><ul>"
                   + string.Concat(lines.Select(l => $"<li>{System.Net.WebUtility.HtmlEncode(l)}</li>"))
                   + "</ul><p>Ayrıntı: <code>person-balance verify --license &lt;id&gt; --details</code></p>";
        try
        {
            await _email.SendAsync(to, "OrderDeck Admin", "[OrderDeck] Ortak bakiye denetimi: yeni sapma", html, plain, ct);
            _log.LogInformation("person-balance-verify: uyarı e-postası gönderildi ({Count} lisans)", alerting.Count);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "person-balance-verify: uyarı e-postası gönderilemedi ({Count} lisans)", alerting.Count);
        }
        return report;
    }
}
