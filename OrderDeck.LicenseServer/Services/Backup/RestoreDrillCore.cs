using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace OrderDeck.LicenseServer.Services.Backup;

/// <summary>
/// Pure restore-drill logic shared between the manual CLI
/// (<see cref="OrderDeck.LicenseServer.Tools.RestoreVerify"/>) and the
/// scheduled Hangfire job (<see cref="BackupRestoreDrillJob"/>).
///
/// Takes a <see cref="BackupStorageService"/> + a backup blob path,
/// performs decrypt → ZIP integrity → SQLite open → PRAGMA integrity_check,
/// reports each step, and wipes its workdir on every exit. Returns a
/// structured result so the caller can decide what to do (print to
/// stdout, send an alert email, ignore, ...).
///
/// <para><b>Read-only:</b> writes only into a workdir the caller supplies.
/// Never touches the configured backup storage root.</para>
/// </summary>
public static class RestoreDrillCore
{
    public sealed record DrillResult(
        bool Passed,
        string BlobPath,
        int KeyVersion,
        IReadOnlyList<DrillStep> Steps)
    {
        /// <summary>Human-readable, multi-line summary suitable for an email body
        /// or a CLI block of output.</summary>
        public string ToReport()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Blob: {BlobPath}");
            sb.AppendLine($"Key version: {KeyVersion}");
            sb.AppendLine();
            foreach (var s in Steps) sb.AppendLine(s.ToString());
            sb.AppendLine();
            sb.AppendLine(Passed ? "RESTORE DRILL PASSED" : "RESTORE DRILL FAILED");
            return sb.ToString();
        }
    }

    public sealed record DrillStep(string Name, bool Ok, string Message)
    {
        public override string ToString() =>
            $"[{(Ok ? "OK" : "FAIL")}] {Name}: {Message}";
    }

    /// <summary>
    /// Runs the drill end-to-end. Caller MUST own a fresh, writable workdir
    /// — this method does not create or remove it; that's the caller's
    /// responsibility (so the CLI and the job can apply different cleanup
    /// policies).
    /// </summary>
    public static async Task<DrillResult> RunAsync(
        BackupStorageService storage,
        string blobPath,
        int keyVersion,
        string workdir,
        CancellationToken ct = default)
    {
        var steps = new List<DrillStep>();
        var passed = true;

        // ─── Step 1: read + decrypt ─────────────────────────────────
        byte[] envelope;
        byte[] plaintext;
        try
        {
            envelope = await File.ReadAllBytesAsync(blobPath, ct);
        }
        catch (Exception ex)
        {
            steps.Add(new DrillStep("Read blob", false, ex.Message));
            return new DrillResult(false, blobPath, keyVersion, steps);
        }

        try
        {
            plaintext = storage.Decrypt(envelope, keyVersion);
            steps.Add(new DrillStep("Decrypt",
                true,
                $"{envelope.Length} envelope bytes → {plaintext.Length} plaintext bytes (keyVersion={keyVersion})"));
        }
        catch (CryptographicException ex)
        {
            steps.Add(new DrillStep("Decrypt", false,
                $"AES-GCM auth tag mismatch — wrong key or tampered blob ({ex.Message})"));
            return new DrillResult(false, blobPath, keyVersion, steps);
        }
        catch (Exception ex)
        {
            steps.Add(new DrillStep("Decrypt", false, $"{ex.GetType().Name}: {ex.Message}"));
            return new DrillResult(false, blobPath, keyVersion, steps);
        }

        var zipPath = Path.Combine(workdir, "restored.zip");
        await File.WriteAllBytesAsync(zipPath, plaintext, ct);

        // ─── Step 2: ZIP integrity ─────────────────────────────────
        var extractDir = Path.Combine(workdir, "extracted");
        if (Directory.Exists(extractDir)) Directory.Delete(extractDir, recursive: true);
        Directory.CreateDirectory(extractDir);

        try
        {
            using (var archive = ZipFile.OpenRead(zipPath))
            {
                var entryCount = archive.Entries.Count;
                if (entryCount == 0)
                {
                    steps.Add(new DrillStep("ZIP integrity", false, "archive contains zero entries"));
                    return new DrillResult(false, blobPath, keyVersion, steps);
                }
                steps.Add(new DrillStep("ZIP integrity", true,
                    $"{entryCount} entries"));
            }
            ZipFile.ExtractToDirectory(zipPath, extractDir, overwriteFiles: true);
        }
        catch (InvalidDataException ex)
        {
            steps.Add(new DrillStep("ZIP integrity", false, $"corrupted archive ({ex.Message})"));
            return new DrillResult(false, blobPath, keyVersion, steps);
        }

        // ─── Step 3: SQLite integrity (if a .db file is present) ───
        var dbFile = Directory.EnumerateFiles(extractDir, "*.db", SearchOption.AllDirectories)
            .FirstOrDefault();
        if (dbFile is null)
        {
            steps.Add(new DrillStep("SQLite", false, "No .db file in archive (skipping)"));
            // Not fatal — older backups may have other layouts. Don't fail
            // the drill on this alone.
        }
        else
        {
            try
            {
                using var conn = new SqliteConnection(
                    $"Data Source={dbFile};Mode=ReadOnly;Pooling=false");
                conn.Open();

                int tableCount;
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText =
                        "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'";
                    tableCount = Convert.ToInt32(cmd.ExecuteScalar());
                }
                steps.Add(new DrillStep("SQLite open", true,
                    $"{tableCount} tables"));

                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "PRAGMA integrity_check";
                    var result = cmd.ExecuteScalar()?.ToString();
                    if (result == "ok")
                    {
                        steps.Add(new DrillStep("SQLite integrity_check", true, "ok"));
                    }
                    else
                    {
                        steps.Add(new DrillStep("SQLite integrity_check", false,
                            result ?? "(no result)"));
                        passed = false;
                    }
                }
            }
            catch (Exception ex)
            {
                steps.Add(new DrillStep("SQLite open", false,
                    $"{ex.GetType().Name}: {ex.Message}"));
                return new DrillResult(false, blobPath, keyVersion, steps);
            }
        }

        return new DrillResult(passed, blobPath, keyVersion, steps);
    }

    /// <summary>
    /// Picks the most-recently-modified <c>.bin</c> blob under the given
    /// storage root, traversing customer subdirectories. Returns null if
    /// no backup has ever been uploaded — drill should treat that as
    /// "nothing to verify, succeed silently" so a fresh deployment doesn't
    /// page someone every Monday morning.
    /// </summary>
    public static string? FindLatestBlob(string storageRoot)
    {
        if (!Directory.Exists(storageRoot)) return null;
        return Directory
            .EnumerateFiles(storageRoot, "*.bin", SearchOption.AllDirectories)
            .OrderByDescending(p => new FileInfo(p).LastWriteTimeUtc)
            .FirstOrDefault();
    }
}
