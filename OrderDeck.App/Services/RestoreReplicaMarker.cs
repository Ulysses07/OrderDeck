using System.IO;
using Microsoft.Extensions.Logging;
using OrderDeck.Core.Storage.Repositories;

namespace OrderDeck.App.Services;

/// <summary>
/// Göç 049'dan ÖNCEKİ bir yedeğin geri yüklenmesinde kopya kimliği devri (spec 2026-10-13 §8.7) — açılışa
/// bırakılan işaret dosyası (<c>orderdeck.db.replica-previous</c>, içinde değiştirilen veritabanının kimliği).
///
/// <para><b>Neden işaret, neden geri yüklemede göç değil:</b> eski yedekte <c>_meta.ReplicaId</c> kolonu yok;
/// döndürme ancak 049 koştuktan sonra yapılabilir. Göçü <see cref="RestoreService"/>'te koşmak geri yüklemeye
/// açılış göçünün bütün yükünü (eski yedekte birçok göç) ve hatasını taşırdı; uygulama zaten geri yüklemeden hemen
/// sonra yeniden başlıyor ve açılışta göçü koşuyor. 049 o dosyaya yeni, rastgele bir kimlik tohumlar; bu işaret
/// yalnız önceki kimliği ekler. Göç 049 sonrası yedekte işaret yazılmaz: <see cref="RestoreService"/> kimliği
/// dosyada, değiştirmeden önce döndürür.</para>
///
/// <para><b>Uygulama idempotent:</b> açılış göçünden hemen sonra (<c>AppHost</c>) bekleyen önceki kimlik yazılır,
/// işaret silinir. Silme başarısız olursa sonraki açılış aynı değeri yeniden yazar (zararsız; sunucu önceki
/// kimliği bir kez yazar). Kimlik bozuksa ya da önceki kimlikle aynıysa (beklenmez) ayrıca döndürülür: geri
/// yüklenen dosya hiçbir zaman başka bir veritabanının kimliğiyle çalışmaz.</para>
/// </summary>
public static class RestoreReplicaMarker
{
    public const string Suffix = ".replica-previous";

    public static string PathFor(string databaseFile) => databaseFile + Suffix;

    internal static void Write(string databaseFile, Guid previous) =>
        File.WriteAllText(PathFor(databaseFile), previous.ToString("N"));

    internal static void Delete(string databaseFile)
    {
        var path = PathFor(databaseFile);
        if (File.Exists(path)) File.Delete(path);
    }

    /// <summary>
    /// Açılış göçünden SONRA çağrılır. İşaret yoksa hiçbir şey yapmaz.
    /// </summary>
    /// <returns>Bekleyen yapılan önceki kimlik; işaret yok, bozuk ya da uygulanamadıysa <c>null</c>.</returns>
    public static Guid? ApplyAtBoot(string databaseFile, ReplicaIdentityRepository identity, ILogger log)
    {
        var path = PathFor(databaseFile);
        if (!File.Exists(path)) return null;

        Guid previous;
        try
        {
            if (!Guid.TryParse(File.ReadAllText(path).Trim(), out previous) || previous == Guid.Empty)
            {
                log.LogWarning("Geri yükleme kimlik işareti bozuk; önceki kopya kimliği bildirilemeyecek, işaret siliniyor");
                TryDelete(path, log);
                return null;
            }
        }
        catch (IOException ex)
        {
            log.LogWarning(ex, "Geri yükleme kimlik işareti okunamadı; sonraki açılışta yeniden denenecek");
            return null;
        }

        try
        {
            var current = identity.GetReplicaId();
            if (current is null || current == previous)
            {
                var next = identity.Rotate(previous);
                log.LogWarning(
                    "Geri yüklenen veritabanının kopya kimliği geçersizdi ya da öncekiyle aynıydı; yenisi üretildi (replica={ReplicaId}, previous={PreviousReplicaId})",
                    next, previous);
            }
            else
            {
                identity.SetPendingPreviousReplicaId(previous);
                log.LogInformation(
                    "Geri yüklenen eski yedeğe önceki kopya kimliği eklendi (replica={ReplicaId}, previous={PreviousReplicaId}); ilk sales/hello taşıyacak",
                    current, previous);
            }
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Geri yükleme kimlik işareti uygulanamadı; sonraki açılışta yeniden denenecek");
            return null;
        }

        TryDelete(path, log);
        return previous;
    }

    private static void TryDelete(string path, ILogger log)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogWarning(ex, "Geri yükleme kimlik işareti silinemedi; sonraki açılışta aynı değer yeniden yazılır (zararsız)");
        }
    }
}
