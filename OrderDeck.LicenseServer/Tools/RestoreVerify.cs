using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Services.Backup;

namespace OrderDeck.LicenseServer.Tools;

/// <summary>
/// In-process restore drill — invoked from <c>Program.Main</c> when the
/// first CLI argument is <c>restore-verify</c>. The web host never starts.
///
/// <para>Reuses <see cref="BackupStorageService"/> with the exact same
/// <see cref="BackupOptions"/> the running server uses (env vars are
/// already in scope inside the container), so a successful drill proves
/// "the running deployment can decrypt this blob" — not just "this code
/// path is well-formed".</para>
///
/// <para>Output is structured for shell parsing: every check prints a
/// single line beginning with <c>[OK]</c> or <c>[FAIL]</c>, and the
/// process exits 0 only when every check passed. The wrapper script
/// (deploy/restore-test.sh) treats the exit code as authoritative.</para>
///
/// <para><b>Read-only:</b> writes only into a temp directory the caller
/// supplies (or <c>/tmp/orderdeck-restore-test</c>); never touches the
/// configured backup storage root.</para>
/// </summary>
public static class RestoreVerify
{
    /// <summary>Entry point. Returns process exit code.</summary>
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine(
                "usage: dotnet OrderDeck.LicenseServer.dll restore-verify <blob-path> [<key-version>] [--workdir=PATH]");
            return 2;
        }

        var blobPath = args[1];
        int keyVersion = 0;
        string workDir = "/tmp/orderdeck-restore-test";

        for (var i = 2; i < args.Length; i++)
        {
            if (int.TryParse(args[i], out var v)) { keyVersion = v; continue; }
            if (args[i].StartsWith("--workdir=", StringComparison.Ordinal))
                workDir = args[i].Substring("--workdir=".Length);
        }

        if (!File.Exists(blobPath))
        {
            Console.Error.WriteLine($"[FAIL] Blob not found: {blobPath}");
            return 3;
        }

        Directory.CreateDirectory(workDir);
        var allOk = true;

        try
        {
            // Build BackupOptions from env vars exactly the way the host does
            // (configuration uses the "Backup__*" prefix in docker-compose).
            var config = new ConfigurationBuilder().AddEnvironmentVariables().Build();
            var opts = new BackupOptions();
            config.GetSection("Backup").Bind(opts);
            // We don't write anywhere under StorageRoot, but BackupStorageService
            // requires it to exist for its directory check. Point it at the workdir.
            opts.StorageRoot = workDir;

            var svc = new BackupStorageService(
                Options.Create(opts),
                NullLogger<BackupStorageService>.Instance);

            // ─── Step 1: read + decrypt ─────────────────────────────────
            var envelope = await File.ReadAllBytesAsync(blobPath);
            byte[] plaintext;
            try
            {
                plaintext = svc.Decrypt(envelope, keyVersion);
                Console.WriteLine($"[OK] Decrypt: {envelope.Length} envelope bytes → {plaintext.Length} plaintext bytes (keyVersion={keyVersion})");
            }
            catch (CryptographicException ex)
            {
                Console.Error.WriteLine($"[FAIL] Decrypt: AES-GCM auth tag mismatch — wrong key or tampered blob ({ex.Message})");
                return 4;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[FAIL] Decrypt: {ex.GetType().Name}: {ex.Message}");
                return 4;
            }

            var zipPath = Path.Combine(workDir, "restored.zip");
            await File.WriteAllBytesAsync(zipPath, plaintext);

            // ─── Step 2: ZIP integrity ─────────────────────────────────
            var extractDir = Path.Combine(workDir, "extracted");
            if (Directory.Exists(extractDir)) Directory.Delete(extractDir, recursive: true);
            Directory.CreateDirectory(extractDir);

            try
            {
                using var archive = ZipFile.OpenRead(zipPath);
                var entryCount = archive.Entries.Count;
                if (entryCount == 0)
                {
                    Console.Error.WriteLine("[FAIL] ZIP: archive contains zero entries");
                    return 5;
                }
                ZipFile.ExtractToDirectory(zipPath, extractDir, overwriteFiles: true);
                Console.WriteLine($"[OK] ZIP integrity: {entryCount} entries extracted to {extractDir}");
            }
            catch (InvalidDataException ex)
            {
                Console.Error.WriteLine($"[FAIL] ZIP: corrupted archive ({ex.Message})");
                return 5;
            }

            // ─── Step 3: SQLite integrity (if a .db file is present) ───
            var dbFile = Directory.EnumerateFiles(extractDir, "*.db", SearchOption.AllDirectories)
                .FirstOrDefault();
            if (dbFile is null)
            {
                Console.WriteLine("[WARN] No .db file in archive — skipping SQLite checks");
            }
            else
            {
                try
                {
                    using var conn = new SqliteConnection($"Data Source={dbFile};Mode=ReadOnly;Pooling=false");
                    conn.Open();

                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name";
                        using var reader = cmd.ExecuteReader();
                        var tables = new System.Collections.Generic.List<string>();
                        while (reader.Read()) tables.Add(reader.GetString(0));
                        Console.WriteLine($"[OK] SQLite open: {tables.Count} tables — {string.Join(", ", tables)}");
                    }

                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = "PRAGMA integrity_check";
                        var result = cmd.ExecuteScalar()?.ToString();
                        if (result == "ok")
                        {
                            Console.WriteLine("[OK] SQLite PRAGMA integrity_check: ok");
                        }
                        else
                        {
                            Console.Error.WriteLine($"[FAIL] SQLite PRAGMA integrity_check: {result}");
                            allOk = false;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[FAIL] SQLite open: {ex.GetType().Name}: {ex.Message}");
                    return 6;
                }
            }
        }
        finally
        {
            // Always wipe the workdir — leaving plaintext customer data on
            // disk after a drill would be a much bigger problem than a
            // failed verification.
            try
            {
                if (Directory.Exists(workDir))
                {
                    Directory.Delete(workDir, recursive: true);
                    Console.WriteLine($"[OK] Cleanup: {workDir} removed");
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[WARN] Cleanup failed: {ex.Message}");
            }
        }

        if (allOk)
        {
            Console.WriteLine("RESTORE DRILL PASSED");
            return 0;
        }
        else
        {
            Console.Error.WriteLine("RESTORE DRILL FAILED");
            return 1;
        }
    }
}
