using System.IO;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace OrderDeck.App.Services;

/// <summary>
/// Phase 5a: detects orphan .pre-restore.bak files at app start.
/// If found AND main DB looks empty/corrupt, prompts user to roll back.
/// In v1: only logs a warning. Future: UI prompt.
/// </summary>
public sealed class RestoreRecoveryService : IHostedService
{
    private readonly string _databaseFile;
    private readonly ILogger<RestoreRecoveryService> _log;

    public RestoreRecoveryService(string databaseFile, ILogger<RestoreRecoveryService> log)
    {
        _databaseFile = databaseFile;
        _log = log;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var bakPath = _databaseFile + RestoreService.PreRestoreBakSuffix;
        if (!File.Exists(bakPath)) return Task.CompletedTask;

        var mainExists = File.Exists(_databaseFile);
        var mainSize = mainExists ? new FileInfo(_databaseFile).Length : 0;

        if (mainExists && mainSize >= 1024)
        {
            _log.LogInformation("Cleaning up successful pre-restore backup: {Path}", bakPath);
            try { File.Delete(bakPath); } catch (Exception ex) { _log.LogWarning(ex, "Failed to delete bak"); }
        }
        else
        {
            _log.LogWarning("Detected pre-restore backup at {Path} but main DB is empty/missing — possible interrupted restore", bakPath);
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
