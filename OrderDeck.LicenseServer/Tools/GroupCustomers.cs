using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Services.CustomerSync;

namespace OrderDeck.LicenseServer.Tools;

/// <summary>
/// Konteyner içinde elle koşulur (eşleşme dosyası konteynerin içinden okunur —
/// önce ör. <c>docker cp</c> ile kopyalanır):
///   docker exec orderdeck-license dotnet OrderDeck.LicenseServer.dll \
///       group-customers --license &lt;guid&gt; --pairs &lt;dosya&gt; [--apply]
/// Yayıncının elle seçtiği (alıcı satırı, kayıtlı satır) çiftlerini sunucuda
/// aynı kişi grubuna DAMGALI olarak koyar; bilgisayarlar değişiklik akışıyla
/// benimser. Kurallar <see cref="CustomerGroupingJob"/>'ta. --apply yoksa kuru
/// çalıştırma: aynı sayıları hesaplar, hiçbir şey yazmaz. --apply sırasında
/// bilgisayarlarda OrderDeck'in kapalı olması önerilir (birleştirmeyle aynı
/// bakım penceresi): arada bir bilgisayarda yapılan grup değişikliği bu
/// yazımla yarışabilir.
///
/// <para><b>Eşleşme dosyası</b> UTF-8 metin. Boş satırlar ve '#' ile başlayan
/// satırlar atlanır; öbür her satır boşlukla ayrılmış İKİ GUID'dir (N ya da D
/// biçimi): <c>&lt;alıcı projeksiyon Id&gt; &lt;kayıtlı projeksiyon Id&gt;</c>.
/// Tek bir bozuk satır bütün dosyayı reddeder → 2, veritabanına dokunmadan;
/// iletide satırın NUMARASI vardır, içeriği yoktur. Yarım dosyayla yarım
/// gruplama yapılmaz.</para>
///
/// <para>Çıkış kodları: 0 tamam; 1 bazı bileşenler eşzamanlı değişiklik ya da
/// veritabanı hatası yüzünden geri alınıp atlandı — yeniden çalıştır (biten
/// bileşenler kalıcı; yeniden koşu idempotent, ikinci uygulama hiçbir satırı
/// değiştirmez); 2 kullanım/yapılandırma/bozuk dosya.</para>
///
/// <para>Argümanlar katı: tanınmayan argüman, değeri eksik ya da GUID olmayan
/// <c>--license</c>, değeri eksik <c>--pairs</c>, ikinci kez verilen seçenek,
/// <c>--license</c> ya da <c>--pairs</c> yok → 2, yapılandırmaya ve veritabanına
/// dokunmadan. Komut prod'da elle yazılır: yazım hatalı <c>--apply</c> kuru
/// çalıştırmaya dönüşmemeli; <c>--pairs --apply</c> gibi bir yazım <c>--apply</c>'ı
/// dosya adı sanmamalı.</para>
///
/// Bağlantı dizesi web host'unkiyle aynı kaynaktan (ConnectionStrings:LicenseDb,
/// ortam değişkeni dahil). Çıktıda kişisel veri YOK — yalnız sayılar ve lisans
/// Id'si; dosya yolu ve satır içeriği de yazılmaz.
/// </summary>
public static class GroupCustomers
{
    private const string Usage = "Kullanım: group-customers --license <guid> --pairs <dosya> [--apply]";

    public static async Task<int> RunAsync(string[] args)
    {
        if (!TryParseArgs(args, out var license, out var pairsPath, out var apply))
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
        return await RunAsync(db, license, pairsPath, apply, Console.Out, Console.Error, logs, CancellationToken.None);
    }

    /// <summary>Komutun gövdesi: argümanlar ayrıştırılmış, bağlantı nesnesi
    /// kurulmuş. Eşleşme dosyası veritabanına dokunmadan ÖNCE baştan sona
    /// okunup doğrulanır. Testler gerçek SQL Server'a buradan girer.</summary>
    public static async Task<int> RunAsync(
        LicenseDbContext db, Guid license, string pairsPath, bool apply, TextWriter output, TextWriter error,
        ILoggerFactory loggerFactory, CancellationToken ct)
    {
        if (!TryReadPairs(pairsPath, error, out var pairs))
            return 2;

        var job = new CustomerGroupingJob(db, loggerFactory.CreateLogger<CustomerGroupingJob>());
        var r = await job.RunAsync(license, pairs, apply, ct);
        output.WriteLine($"{(apply ? "UYGULANDI" : "KURU ÇALIŞTIRMA")} — {license}: {Format(r)}");
        if (r.FailedComponents > 0)
        {
            error.WriteLine($"{r.FailedComponents} bileşen eşzamanlı değişiklik ya da veritabanı hatası yüzünden geri alınıp atlandı; komutu yeniden çalıştırın (biten bileşenler kalıcı, yeniden koşu idempotent).");
            return 1;
        }
        return 0;
    }

    /// <summary>args[0] komut adıdır. Bkz. sınıf dokümanı: katı ayrıştırma.
    /// <para><b>Neden <c>public</c>:</b> geçerli argümanlı yol yapılandırmaya ve
    /// veritabanına gider; kabul edilen biçimler yalnız saf fonksiyon olarak
    /// sınanabilir. Sunucu projesinde <c>InternalsVisibleTo</c> yok — kalıp
    /// <c>PanelNetgsmAccountController</c>'daki gibi.</para></summary>
    public static bool TryParseArgs(string[] args, out Guid license, out string pairsPath, out bool apply)
    {
        Guid? parsedLicense = null;
        string? path = null;
        apply = false;
        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--apply" when !apply:
                    apply = true;
                    break;
                case "--license" when parsedLicense is null:
                    if (i + 1 >= args.Length || !Guid.TryParse(args[i + 1], out var parsed))
                        return Fail(out license, out pairsPath);
                    parsedLicense = parsed;
                    i++;
                    break;
                case "--pairs" when path is null:
                    // Değer bir seçenek olamaz: "--pairs --apply" --apply'ı dosya adı yapıp
                    // kuru çalıştırmaya dönüşmesin.
                    if (i + 1 >= args.Length || string.IsNullOrWhiteSpace(args[i + 1])
                        || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                        return Fail(out license, out pairsPath);
                    path = args[i + 1];
                    i++;
                    break;
                default:
                    return Fail(out license, out pairsPath);
            }
        }
        license = parsedLicense ?? Guid.Empty;
        pairsPath = path ?? "";
        return parsedLicense.HasValue && path is not null;

        static bool Fail(out Guid license, out string pairsPath)
        {
            license = Guid.Empty;
            pairsPath = "";
            return false;
        }
    }

    /// <summary>Eşleşme dosyasını okur ve doğrular (biçim: sınıf dokümanı).
    /// Okunamayan dosya ya da tek bir bozuk satır → false; iletide istisna tipi
    /// ya da 1'den sayılan satır NUMARASI var — dosya yolu ve satırın içeriği
    /// yazılmaz.</summary>
    public static bool TryReadPairs(string path, TextWriter error, out IReadOnlyList<CustomerGroupingJob.Pair> pairs)
    {
        pairs = [];
        string[] lines;
        try
        {
            lines = File.ReadAllLines(path, Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            error.WriteLine($"Eşleşme dosyası okunamadı (--pairs): {ex.GetType().Name}");
            return false;
        }

        var read = new List<CustomerGroupingJob.Pair>();
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2 || !TryParseId(parts[0], out var buyer) || !TryParseId(parts[1], out var registered))
            {
                error.WriteLine($"Eşleşme dosyası bozuk: satır {i + 1} — beklenen \"<alıcı Id> <kayıtlı Id>\" (iki GUID, N ya da D biçimi). Hiçbir şey yazılmadı.");
                return false;
            }
            read.Add(new CustomerGroupingJob.Pair(buyer, registered));
        }
        pairs = read;
        return true;
    }

    private static bool TryParseId(string text, out Guid id)
        => Guid.TryParseExact(text, "N", out id) || Guid.TryParseExact(text, "D", out id);

    private static string Format(CustomerGroupingJob.Report r)
        => $"çift={r.PairsRead} çözülen={r.PairsResolved} çözülemeyen (yok/başka lisans/silinmiş)={r.PairsUnresolved} " +
           $"zaten birlikte={r.AlreadyTogether} | bileşen={r.Components} grubu değişecek satır={r.RowsToChange} " +
           $"birden çok kayıtlı tarafı birleştiren bileşen={r.ComponentsJoiningRegistered} " +
           $"kara liste yayılımı (satır)={r.BlacklistPropagations} | atlanan bileşen={r.FailedComponents}";
}
