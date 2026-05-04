using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Services.Backup;

namespace OrderDeck.LicenseServer.Tools;

/// <summary>
/// In-process restore drill — invoked from <c>Program.Main</c> when the
/// first CLI argument is <c>restore-verify</c>. The web host never starts.
///
/// <para>Reuses <see cref="RestoreDrillCore"/> with a real
/// <see cref="BackupStorageService"/> built from the same env-driven
/// <see cref="BackupOptions"/> the running server uses, so a successful
/// drill proves "the running deployment can decrypt this blob" — not
/// just "this code path is well-formed".</para>
///
/// <para>Output is structured for shell parsing: <see cref="RestoreDrillCore.DrillResult.ToReport"/>
/// emits one line per check (each beginning with <c>[OK]</c> or
/// <c>[FAIL]</c>) plus a final <c>RESTORE DRILL PASSED/FAILED</c> line.
/// Process exits 0 only when the drill passed.</para>
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

        try
        {
            // Build BackupOptions from env vars exactly the way the host does
            // (configuration uses the "Backup__*" prefix in docker-compose).
            var config = new ConfigurationBuilder().AddEnvironmentVariables().Build();
            var opts = new BackupOptions();
            config.GetSection("Backup").Bind(opts);
            // We don't write under StorageRoot, but BackupStorageService
            // requires it to exist for its directory check. Point it at the
            // workdir so the constructor is happy.
            opts.StorageRoot = workDir;

            var svc = new BackupStorageService(
                Options.Create(opts),
                NullLogger<BackupStorageService>.Instance);

            var result = await RestoreDrillCore.RunAsync(svc, blobPath, keyVersion, workDir);

            // Emit the structured report (one [OK]/[FAIL] line per step).
            Console.WriteLine(result.ToReport());

            return result.Passed ? 0 : 1;
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
    }
}
