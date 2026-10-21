using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Services.Bank;

namespace OrderDeck.LicenseServer.Tools;

/// <summary>
/// Konteyner içinde elle koşulur:
///   docker exec orderdeck-license dotnet OrderDeck.LicenseServer.dll bank-data-cleanup [--license &lt;guid&gt;] [--apply] [--reset-iban-memory]
/// KVKK düzeltmelerinden önce yazılmış banka verisini yeni kurallara indirir; kurallar <see cref="BankDataCleanupJob"/>'ta.
/// --apply yoksa kuru koşu: uygulamanın sayılarını hesaplar, hiçbir şey yazmaz. Önce kuru koşu, çıktı tutanağa. P1'den sonra
/// uygulama DataDestructionRecords'a da yazar. Saklama işi (04:57 UTC), shopper silmesi ya da banka veri talebi silmesi
/// sürerken koşturulmaz: maskeleme adımları oku-değiştir-yaz yapar, arada boşaltılan metni eski hâliyle geri yazabilirdi.
/// <para>Çıkış kodları: 0 tamam; 1 bazı partiler eşzamanlı bir yazı (eşleştirme ya da bağlantı kimliği değişimi) yüzünden geri
/// alındı ya da atlandı — yeniden çalıştır (idempotent); 2 kullanım/yapılandırma.</para>
/// <para>Argümanlar katı (<see cref="GroupCustomers"/> kalıbı): tanınmayan, yinelenen ya da değeri eksik argüman → 2,
/// yapılandırmaya ve veritabanına dokunmadan. Komut prod'da elle yazılır: yazım hatalı <c>--apply</c> kuru çalıştırmaya
/// dönüşmemeli. Çıktıda kişisel veri yok — yalnız sayılar ve lisans kimliği.</para>
/// <para><c>--reset-iban-memory</c>: IBAN hafızasını (lisans verilirse yalnız onunkini) siler; özeti aynı lisansta geçerli
/// IBAN'lı (CounterpartyIbanHashValid) bir harekette görünen bağ kalır — maskeli değerin özeti geçerli IBAN'ınkine eşit
/// olamaz, P1'den sonra öğrenilmiş bağlar korunur, komut ne zaman koşulursa koşulsun güvenli ve idempotent. Hareket özetleri
/// KALIR (IBAN talep aracı onlarla arar) ve eski hareketten yeniden öğrenilmez (CounterpartyIbanHashValid kapısı) — maskeli
/// kaynaklı özetler saklı veriden tam anlaşılamadığı için Faz 2'den önce (KVKK Ö-7, karar Burak'ın). Bayraksız komut yalnız
/// maskesi maskeli kaynağı ele veren özetleri ve bağları temizler.</para>
/// </summary>
public static class BankDataCleanup
{
    private const string Usage = "Kullanım: bank-data-cleanup [--license <guid>] [--apply] [--reset-iban-memory]";

    public sealed record Options(Guid? License, bool Apply, bool ResetIbanMemory = false);

    public static async Task<int> RunAsync(string[] args)
    {
        if (!TryParseArgs(args, out var options))
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
        var dbOptions = new DbContextOptionsBuilder<LicenseDbContext>().UseSqlServer(conn).Options;
        await using var db = new LicenseDbContext(dbOptions);
        using var logs = LoggerFactory.Create(b => b.AddSimpleConsole());
        return await RunAsync(db, options, Console.Out, logs, CancellationToken.None);
    }

    /// <summary>Komutun gövdesi: argümanlar ayrıştırılmış, bağlantı nesnesi kurulmuş. Testler gerçek SQL Server'a buradan
    /// girer.</summary>
    public static async Task<int> RunAsync(LicenseDbContext db, Options options, TextWriter output, ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var job = new BankDataCleanupJob(db, loggerFactory.CreateLogger<BankDataCleanupJob>());
        var r = await job.RunAsync(options.License, options.Apply, ct, options.ResetIbanMemory);
        output.WriteLine($"{(options.Apply ? "UYGULANDI" : "KURU ÇALIŞTIRMA")} — lisans={options.License?.ToString() ?? "hepsi"}: {Format(r)}");
        if (r.FailedBatches > 0)
        {
            output.WriteLine($"{r.FailedBatches} adım eşzamanlı bir yazı yüzünden yarıda kaldı; komutu yeniden çalıştırın (idempotent).");
            return 1;
        }
        return 0;
    }

    /// <summary>args[0] komut adıdır. Bkz. sınıf dokümanı: katı ayrıştırma. <c>public</c>: sunucu projesinde
    /// <c>InternalsVisibleTo</c> yok (<see cref="GroupCustomers.TryParseArgs"/> kalıbı).</summary>
    public static bool TryParseArgs(string[] args, out Options options)
    {
        Guid? license = null;
        var apply = false;
        var reset = false;
        options = new Options(null, false);
        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--apply" when !apply:
                    apply = true;
                    break;
                case "--reset-iban-memory" when !reset:
                    reset = true;
                    break;
                case "--license" when license is null:
                    if (i + 1 >= args.Length || !Guid.TryParse(args[i + 1], out var parsed)) return false;
                    license = parsed;
                    i++;
                    break;
                default:
                    return false;
            }
        }
        options = new Options(license, apply, reset);
        return true;
    }

    public static string Format(BankDataCleanupJob.Report r)
        => $"giden/sıfır silinen={r.OutgoingOrZeroDeleted} (kararlı atlanan={r.OutgoingOrZeroSkippedDecided}) | " +
           $"çapa yazılan={r.AnchorsSet} çapa öncesi silinen={r.PreAnchorDeleted} (bağlı kalan={r.PreAnchorSkippedLinked}) | " +
           $"eşleşme silinen={r.MatchesDeleted} | VKN/TCKN kodu boşaltılan={r.TaxIdHashesCleared} | " +
           $"ham kayıt daraltılan={r.RawJsonRewritten} (okunamayan boşaltılan={r.RawJsonUnreadableCleared}) | " +
           $"açıklama maskelenen={r.DescriptionsMasked} | kısa maske çevrilen={r.ShortMasksReplaced} | " +
           $"maskeli kaynaklı özet boşaltılan={r.MaskedSourceHashesCleared} (hafıza silinen={r.MaskedSourceMemoriesDeleted}) | " +
           $"sıfırlanan IBAN hafızası={r.IbanMemoriesReset} (geçerli IBAN'lı kalan={r.IbanMemoriesKeptValid}) | " +
           $"IBAN bağı başlangıcı yazılan={r.IbanConfirmationsBackfilled} | düşen parti={r.FailedBatches}";
}
