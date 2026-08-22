using System.IO;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OrderDeck.Core.Storage;

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

        // Bu dosya operatörün elindeki SON geri dönüş kopyası; silmek geri
        // alınamaz. Eskiden ölçüt "var ve 1 KB'den büyük"tü — yarım yazılmış
        // ya da sayfaları bozuk büyük bir dosya bu eşiği rahatça geçiyor ve
        // tek sağlam kopyayı sessizce yok ediyordu. Artık ölçüt, veritabanının
        // gerçekten açılıp quick_check'ten geçmesi.
        if (SqliteFile.IsIntactDatabase(_databaseFile, out var error))
        {
            _log.LogInformation("Cleaning up successful pre-restore backup: {Path}", bakPath);
            try { File.Delete(bakPath); } catch (Exception ex) { _log.LogWarning(ex, "Failed to delete bak"); }
        }
        else
        {
            _log.LogWarning(
                "Geri yükleme yedeği duruyor ({Path}) çünkü aktif veritabanı doğrulanamadı: {Error} — " +
                "yarım kalmış bir geri yükleme olabilir, yedek KORUNDU",
                bakPath, error);
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
