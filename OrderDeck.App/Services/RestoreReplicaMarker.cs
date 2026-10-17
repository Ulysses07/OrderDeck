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
/// <para><b>İşaretin varlığı geri yüklemenin kanıtıdır</b> (§4.3, M20: geri yüklenmiş kopyada yerel kazanmaz).
/// Önceki kimlik bilinmiyorsa (<see cref="RestoreService"/> değiştirilen veritabanını okuyamadı) ya da işaretin
/// içeriği bozuksa kopya yine geri yüklenmiş bildirilir: rastgele bir nöbetçi kimlikle. Sunucu yalnız
/// <c>PreviousReplicaId</c>'nin dolu olmasına bakar; bilinmeyen kimlik hiçbir oturuma sahip olmadığından devralma
/// zinciri orada durur (zararsız).</para>
///
/// <para><b>Uygulama idempotent:</b> açılış göçünden hemen sonra (<c>AppHost</c>) bekleyen önceki kimlik yazılır,
/// işaret silinir. Silme başarısız olursa sonraki açılış aynı değeri yeniden yazar (zararsız; sunucu önceki
/// kimliği bir kez yazar). Kimlik bozuksa ya da önceki kimlikle aynıysa (beklenmez) ayrıca döndürülür: geri
/// yüklenen dosya hiçbir zaman başka bir veritabanının kimliğiyle çalışmaz.</para>
///
/// <para><b>Uygulanamazsa</b> (işaret okunamadı, kimlik yazılamadı) işaret kalır, sonraki açılış yeniden dener; o
/// süreçte satış senkronunun v2 istekleri kapalıdır (<see cref="RestoreMarkerOutcome.Unresolved"/>,
/// <c>SalesSyncEnvelope.HoldUntilRestart</c>): geri yüklenmiş kopya sunucuya özgün görünmemeli.</para>
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
    public static RestoreMarkerOutcome ApplyAtBoot(string databaseFile, ReplicaIdentityRepository identity, ILogger log)
    {
        var path = PathFor(databaseFile);
        if (!File.Exists(path)) return new RestoreMarkerOutcome(null, Unresolved: false);

        Guid previous;
        try
        {
            if (!Guid.TryParse(File.ReadAllText(path).Trim(), out previous) || previous == Guid.Empty)
            {
                previous = Guid.NewGuid();
                log.LogWarning(
                    "Geri yükleme kimlik işareti bozuk; kopya yine geri yüklenmiş bildirilecek, önceki kimlik bilinmiyor (nöbetçi previous={PreviousReplicaId})",
                    previous);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogError(ex,
                "Geri yükleme kimlik işareti okunamadı; bu süreçte satış senkronu sunucuya istek göndermeyecek, sonraki açılışta yeniden denenecek");
            return new RestoreMarkerOutcome(null, Unresolved: true);
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
            log.LogError(ex,
                "Geri yükleme kimlik işareti uygulanamadı; bu süreçte satış senkronu sunucuya istek göndermeyecek, sonraki açılışta yeniden denenecek");
            return new RestoreMarkerOutcome(null, Unresolved: true);
        }

        TryDelete(path, log);
        return new RestoreMarkerOutcome(previous, Unresolved: false);
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

/// <summary>Açılıştaki işaret uygulamasının sonucu (<see cref="RestoreReplicaMarker.ApplyAtBoot"/>).</summary>
/// <param name="Previous">Bekleyen yapılan önceki kimlik (bilinmiyorsa nöbetçi); işaret yoksa ya da uygulanamadıysa
/// boş.</param>
/// <param name="Unresolved">İşaret var ama bu açılışta uygulanamadı: veritabanı geri yüklenmiş, sunucuya henüz öyle
/// bildirilemiyor. Çağıran bu süreçte satış senkronunun v2 isteklerini kapatır; işaret sonraki açılışa kalır.</param>
public readonly record struct RestoreMarkerOutcome(Guid? Previous, bool Unresolved);
