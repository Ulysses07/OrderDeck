using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Hangfire;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Services.Email;
using OrderDeck.LicenseServer.Services.Observability;

namespace OrderDeck.LicenseServer.Services.Backup;

/// <summary>
/// Weekly automated backup-restore drill, scheduled via Hangfire from
/// <see cref="OrderDeck.LicenseServer.Program.Main"/> at 04:30 UTC every
/// Monday. Picks the most-recently-modified backup blob, decrypts +
/// integrity-checks it, and (on failure) emails the configured admin
/// alert address so a silent recovery break can't go unnoticed.
///
/// <para>If <c>Admin:AlertEmail</c> is empty, the job still runs and
/// logs the result — it just doesn't send mail. That's the recommended
/// posture for a brand-new deployment that hasn't wired up SMTP yet.
/// Adding the value later requires no code change.</para>
///
/// <para>Cron: <c>30 4 * * MON</c> UTC (~07:30 Türkiye, before any live
/// stream traffic kicks in). One job, one customer-blob — fast and light
/// enough to share Hangfire's worker pool with other recurring tasks.</para>
/// </summary>
public sealed class BackupRestoreDrillJob
{
    private readonly BackupStorageService _storage;
    private readonly BackupOptions _opts;
    private readonly IEmailSender _email;
    private readonly IConfiguration _config;
    private readonly ILogger<BackupRestoreDrillJob> _log;

    public BackupRestoreDrillJob(
        BackupStorageService storage,
        IOptions<BackupOptions> opts,
        IEmailSender email,
        IConfiguration config,
        ILogger<BackupRestoreDrillJob> log)
    {
        _storage = storage;
        _opts = opts.Value;
        _email = email;
        _config = config;
        _log = log;
    }

    /// <summary>Hangfire entry point. Hangfire serializes the call so any
    /// captured CancellationToken comes from the worker — we accept it for
    /// signature symmetry but the drill is so short it's effectively
    /// non-cancellable in practice.</summary>
    [AutomaticRetry(Attempts = 0)] // a failed drill is itself the signal — don't drown alerts in retries
    public async Task RunAsync(CancellationToken ct)
    {
        var blobPath = RestoreDrillCore.FindLatestBlob(_opts.StorageRoot);
        if (blobPath is null)
        {
            _log.LogInformation(
                "[Backup Drill] No blobs under {Root} — nothing to verify (fresh deployment)",
                _opts.StorageRoot);
            return;
        }

        // Match the active key version in production, NOT a hard-coded 0.
        // A drill that always tries v0 would falsely pass on a deployment
        // that has rotated to v1+ (the default-version envelope blocks
        // catch the mismatch, but only if we look up the right key).
        // We don't know the per-blob KeyVersion without the DB row, so
        // we sample the active version — the most recent blob was almost
        // certainly written under it.
        var keyVersion = _opts.ActiveKeyVersion;

        var workdir = Path.Combine(Path.GetTempPath(),
            "orderdeck-drill-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workdir);

        RestoreDrillCore.DrillResult result;
        try
        {
            result = await RestoreDrillCore.RunAsync(_storage, blobPath, keyVersion, workdir, ct);
        }
        finally
        {
            // Always wipe — plaintext customer data must not linger.
            try { if (Directory.Exists(workdir)) Directory.Delete(workdir, recursive: true); }
            catch (Exception ex) { _log.LogWarning(ex, "[Backup Drill] cleanup failed"); }
        }

        if (result.Passed)
        {
            _log.LogInformation(
                "[Backup Drill] PASSED — {Blob} (keyVersion={Version})",
                result.BlobPath, result.KeyVersion);
            return;
        }

        // Drill failed — log + alert.
        _log.LogError(
            "[Backup Drill] FAILED — {Blob}\n{Report}",
            result.BlobPath, result.ToReport());

        var alertTo = _config["Admin:AlertEmail"];
        if (string.IsNullOrWhiteSpace(alertTo))
        {
            _log.LogWarning(
                "[Backup Drill] FAIL detected but Admin:AlertEmail is unset — alert email skipped");
            return;
        }

        var subject = "[OrderDeck] Backup restore drill FAILED";
        var plain = $"The weekly backup-restore drill failed.\n\n{result.ToReport()}\n\n" +
                    "Investigate immediately — recovery from this customer's blob is not assured. " +
                    "Re-run the manual drill (`bash /opt/orderdeck/restore-test.sh`) once you have a fix in mind.";
        var html = $"<h2>Backup restore drill FAILED</h2>" +
                   $"<pre style=\"font-family:monospace\">{System.Net.WebUtility.HtmlEncode(result.ToReport())}</pre>" +
                   $"<p>Investigate immediately — recovery from this customer's blob is not assured. " +
                   $"Re-run the manual drill (<code>bash /opt/orderdeck/restore-test.sh</code>) once you have a fix in mind.</p>";

        try
        {
            await _email.SendAsync(alertTo, "OrderDeck Admin", subject, html, plain, ct);
            // PII: alert recipient masked — KVKK + 3rd-party log retention guard.
            _log.LogInformation("[Backup Drill] Alert email sent to {Email}", PiiMasker.MaskEmail(alertTo));
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[Backup Drill] Failed to send alert email to {Email}", PiiMasker.MaskEmail(alertTo));
        }
    }
}
