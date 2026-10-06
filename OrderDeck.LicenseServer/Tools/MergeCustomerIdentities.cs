using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Services.CustomerSync;

namespace OrderDeck.LicenseServer.Tools;

/// <summary>
/// Konteyner içinde elle koşulur:
///   docker exec orderdeck-license dotnet OrderDeck.LicenseServer.dll \
///       merge-customer-identities (--all | --license &lt;guid&gt;) [--apply]
/// --apply yoksa kuru çalıştırma (hiçbir şey yazmaz). --apply sırası KODDA:
/// anahtar onarımı → birleştirme → anahtar onarımı → son koşul (uyuşmaz anahtar
/// 0). Çıkış kodları: 0 tamam, 1 bazı gruplar eşzamanlılık yüzünden atlandı
/// (yeniden çalıştır), 2 kullanım/yapılandırma, 3 son koşul tutmadı.
///
/// <para>Argümanlar katı: tanınmayan argüman, değeri eksik ya da GUID olmayan
/// <c>--license</c>, ikinci kez verilen seçenek, <c>--all</c> ile
/// <c>--license</c> birlikte ya da ikisi de yok → 2, yapılandırmaya ve
/// veritabanına dokunmadan. Komut prod'da elle yazılır: bozuk bir
/// <c>--license</c> değeri <c>--all</c>'un yanında sessizce yok sayılıp tüm
/// lisanslara, yazım hatalı <c>--apply</c> kuru çalıştırmaya dönüşmemeli.</para>
///
/// Bağlantı dizesi web host'unkiyle aynı kaynaktan (ConnectionStrings:LicenseDb,
/// ortam değişkeni dahil). Çıktıda kişisel veri YOK — yalnız sayılar ve lisans
/// Id'leri (anahtar onarımının uyarıları satır Id'si taşıyabilir, ad/telefon/
/// kullanıcı adı asla).
/// </summary>
public static class MergeCustomerIdentities
{
    private const string Usage = "Kullanım: merge-customer-identities (--all | --license <guid>) [--apply]";

    public static async Task<int> RunAsync(string[] args)
    {
        if (!TryParseArgs(args, out var all, out var single, out var apply))
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }

        var config = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json", optional: true)
            .AddEnvironmentVariables()
            .Build();
        var conn = config.GetConnectionString("LicenseDb");
        if (string.IsNullOrWhiteSpace(conn))
        {
            Console.Error.WriteLine("ConnectionStrings:LicenseDb bulunamadı.");
            return 2;
        }

        var options = new DbContextOptionsBuilder<LicenseDbContext>().UseSqlServer(conn).Options;
        await using var db = new LicenseDbContext(options);
        using var logs = LoggerFactory.Create(b => b.AddSimpleConsole());
        var job = new CustomerIdentityMergeJob(db, new CustomerIdentityMerger(db));
        var repair = new IdentityKeyRepairJob(db, logs.CreateLogger<IdentityKeyRepairJob>());
        var ct = CancellationToken.None;

        var licenses = single is { } one ? new[] { one } : (await job.LicenseIdsAsync(ct)).ToArray();

        if (apply)
        {
            var fixedBefore = await repair.RunAsync(ct);
            db.ChangeTracker.Clear();
            Console.WriteLine($"Anahtar onarımı (önce): {fixedBefore} satır");
        }
        else
        {
            Console.WriteLine($"Uyuşmaz kimlik anahtarı: {await job.CountMismatchedKeysAsync(ct)} satır (--apply önce onarır)");
        }

        var total = new CustomerIdentityMergeJob.Report(0, 0, 0, 0, 0, 0, 0, 0);
        foreach (var licenseId in licenses)
        {
            var r = await job.RunAsync(licenseId, apply, ct);
            if (r.Groups > 0)
                Console.WriteLine($"{licenseId}: {Format(r)}");
            total = Sum(total, r);
        }
        Console.WriteLine($"{(apply ? "UYGULANDI" : "KURU ÇALIŞTIRMA")} — TOPLAM ({licenses.Length} lisans): {Format(total)}");

        if (!apply) return 0;

        var fixedAfter = await repair.RunAsync(ct);
        db.ChangeTracker.Clear();
        var mismatched = await job.CountMismatchedKeysAsync(ct);
        Console.WriteLine($"Anahtar onarımı (sonra): {fixedAfter} satır; kalan uyuşmaz anahtar: {mismatched}");
        if (mismatched > 0)
        {
            Console.Error.WriteLine("SON KOŞUL TUTMADI: uyuşmaz anahtar var — B1 (tekil indeks) göçü bu hâlde düşer. Komutu yeniden çalıştırın; sürerse inceleyin.");
            return 3;
        }
        if (total.FailedGroups > 0)
        {
            Console.Error.WriteLine($"{total.FailedGroups} grup eşzamanlı değişiklik yüzünden atlandı; komutu yeniden çalıştırın (biten gruplar kalıcı).");
            return 1;
        }
        return 0;
    }

    /// <summary>args[0] komut adıdır. Bkz. sınıf dokümanı: katı ayrıştırma.</summary>
    private static bool TryParseArgs(string[] args, out bool all, out Guid? license, out bool apply)
    {
        all = false;
        apply = false;
        license = null;
        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--all" when !all:
                    all = true;
                    break;
                case "--apply" when !apply:
                    apply = true;
                    break;
                case "--license" when license is null:
                    if (i + 1 >= args.Length || !Guid.TryParse(args[i + 1], out var parsed))
                        return false;
                    license = parsed;
                    i++;
                    break;
                default:
                    return false;
            }
        }
        return all != license.HasValue;
    }

    private static string Format(CustomerIdentityMergeJob.Report r) =>
        $"kopyalı kişi={r.Groups} kopya satır={r.CopyRows} sipariş={r.OrdersToMove} kargo={r.ShipmentsToMove} " +
        $"Shopper bağlantısı={r.LinksToMove} toplanacak bakiye={r.BalancesToSum} silinmiş kişi={r.PurgedGroups} " +
        $"atlanan grup={r.FailedGroups} beklemeye düşen Shopper bağlantısı={r.LinksUnbound}";

    private static CustomerIdentityMergeJob.Report Sum(CustomerIdentityMergeJob.Report a, CustomerIdentityMergeJob.Report b) => new(
        a.Groups + b.Groups, a.CopyRows + b.CopyRows, a.OrdersToMove + b.OrdersToMove,
        a.ShipmentsToMove + b.ShipmentsToMove, a.LinksToMove + b.LinksToMove,
        a.BalancesToSum + b.BalancesToSum, a.PurgedGroups + b.PurgedGroups, a.FailedGroups + b.FailedGroups,
        a.LinksUnbound + b.LinksUnbound);
}
