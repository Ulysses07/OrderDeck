using System.Collections.Immutable;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Services.CustomerSync;

namespace OrderDeck.LicenseServer.Tools;

/// <summary>
/// Konteyner içinde elle koşulur:
///   docker exec orderdeck-license dotnet OrderDeck.LicenseServer.dll \
///       merge-customer-identities (--all | --license &lt;guid&gt;) [--at-twins] [--apply]
/// --apply yoksa kuru çalıştırma (hiçbir şey yazmaz; taşınacak kayıtları,
/// platform başına grupları ve asıl kayıtla kopya arasındaki çelişkileri sayar,
/// B1 kapısının ve zincirlerin bugünkü sayısını yazar). --apply sırası KODDA:
/// anahtar onarımı → birleştirme → anahtar onarımı → son koşul (uyuşmaz
/// anahtar 0 VE B1'in birebir SQL kapısı 0 VE zincir — kopyanın kopyası — 0).
/// --apply sırasında TÜM bilgisayarlarda OrderDeck
/// kapalı olmalı: arada gelen bir gönderim kopyaya yazılıp boşaltılabilir ya
/// da zincir bırakabilir.
///
/// <para><c>--at-twins</c> ("@" ikizi kipi, 2026-10-08): 2026-08-05'ten
/// v0.9.9'a dek Instagram resmi API yolu kullanıcı adını "@ad" diye yazdı; form
/// ve eski eklenti "ad" yazıyordu. Kimlik anahtarları farklı olduğundan aynı
/// kişinin iki asıl kaydı oldu (prod'da 512 Instagram kişisi) ve olağan kip
/// onları görmez. Bu kip yalnız Instagram/TikTok/Facebook'ta, baştaki '@'leri
/// atılmış anahtarla gruplar (YouTube hiç) ve "@"SIZ yazımı asıl kayıt yapar:
/// form ve yeni yorumlar o yazımı kullanır — "@"lı satır asıl kalsaydı sonraki
/// gönderim aynı kişiye yeniden ayrı kayıt açardı (canlı satır yine önce gelir;
/// "@"lı asıl kayıtla biten grup ayrıca yazılır). İkizler farklı kişi
/// gruplarındaysa grupların bütün üyeleri tek numarada toplanır — kişinin grubu
/// bölünmez. Bilgisayarlar yönlendirmeyi (kopya → asıl kayıt) değişiklik
/// akışından alır ve yerel kayıtlarını (etiket, kargo, ödeme işi) asıl kayda
/// taşır. Sıra, son koşullar ve "bilgisayarlar kapalı" kuralı aynı; ek son koşul:
/// aynı gruplamayla yeniden sayılan @ ikizi grubu (bu koşunun lisansları) 0.
/// Ayrıntı: <see cref="CustomerIdentityMergeJob"/>.</para>
///
/// <para>Çıkış kodları: 0 tamam; 1 bazı gruplar eşzamanlı değişiklik ya da
/// veritabanı hatası yüzünden geri alınıp atlandı — yeniden çalıştır (biten
/// gruplar kalıcı; kalan kopyalar B1 kapısında ve kalan ikiz sayımında görünür,
/// beklenen); 2 kullanım/yapılandırma; 3 son koşul tutmadı (uyuşmaz anahtar ya da
/// atlanan grup yokken B1 kapısı sıfır değil — B1 göçü bu hâlde düşer — ya da
/// zincir ya da <c>--at-twins</c>'te @ ikizi grubu kaldı). Kapı TÜM
/// lisansları sayar: <c>--license</c> ile koşulduysa öbür lisansların
/// kopyaları da içindedir.</para>
///
/// <para>Argümanlar katı: tanınmayan argüman, değeri eksik ya da GUID olmayan
/// <c>--license</c>, ikinci kez verilen seçenek (<c>--at-twins</c> dahil), <c>--all</c> ile
/// <c>--license</c> birlikte ya da ikisi de yok → 2, yapılandırmaya ve
/// veritabanına dokunmadan. Komut prod'da elle yazılır: bozuk bir
/// <c>--license</c> değeri <c>--all</c>'un yanında sessizce yok sayılıp tüm
/// lisanslara, yazım hatalı <c>--apply</c> kuru çalıştırmaya dönüşmemeli.</para>
///
/// Bağlantı dizesi web host'unkiyle aynı kaynaktan (ConnectionStrings:LicenseDb,
/// ortam değişkeni dahil). Çıktıda kişisel veri YOK — yalnız sayılar, platform
/// adları ve lisans Id'leri (anahtar onarımının uyarıları satır Id'si
/// taşıyabilir, ad/telefon/kullanıcı adı asla).
/// </summary>
public static class MergeCustomerIdentities
{
    private const string Usage = "Kullanım: merge-customer-identities (--all | --license <guid>) [--at-twins] [--apply]";

    public static async Task<int> RunAsync(string[] args)
    {
        if (!TryParseArgs(args, out _, out var single, out var apply, out var atTwins))
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
        return await RunAsync(db, single, apply, Console.Out, Console.Error, logs, CancellationToken.None, atTwins);
    }

    /// <summary>Komutun gövdesi: argümanlar ayrıştırılmış, bağlantı kurulmuş.
    /// <paramref name="license"/> null ise projeksiyonu olan bütün lisanslar
    /// (<c>--all</c>). Testler gerçek SQL Server'a buradan girer.</summary>
    /// <param name="atTwins"><c>--at-twins</c>: "@" ikizi kipi (bkz. sınıf dokümanı).</param>
    public static async Task<int> RunAsync(
        LicenseDbContext db, Guid? license, bool apply, TextWriter output, TextWriter error,
        ILoggerFactory loggerFactory, CancellationToken ct, bool atTwins = false)
    {
        var job = new CustomerIdentityMergeJob(db, new CustomerIdentityMerger(db),
            loggerFactory.CreateLogger<CustomerIdentityMergeJob>());
        var repair = new IdentityKeyRepairJob(db, job, loggerFactory.CreateLogger<IdentityKeyRepairJob>());

        var licenses = license is { } one ? new[] { one } : (await job.LicenseIdsAsync(ct)).ToArray();

        if (apply)
        {
            var fixedBefore = await repair.RunAsync(ct);
            db.ChangeTracker.Clear();
            output.WriteLine($"Anahtar onarımı (önce): {fixedBefore} satır");
        }
        else
        {
            output.WriteLine($"Uyuşmaz kimlik anahtarı: {await job.CountMismatchedKeysAsync(ct)} satır (--apply önce onarır)");
            output.WriteLine($"B1 kapısı (SQL, tüm lisanslar): yinelenen asıl kayıt grubu {await job.CountDuplicateHeadsAsync(ct)} (--apply sonrası 0 olmalı)");
            output.WriteLine($"Zincir (kopyanın kopyası, SQL, tüm lisanslar): {await job.CountChainsAsync(ct)} (--apply sonrası 0 olmalı; iş yalnız birleştirdiği grubu düzleştirir)");
        }

        var total = new CustomerIdentityMergeJob.Report(0, 0, 0, 0, 0, 0, 0, 0);
        foreach (var licenseId in licenses)
        {
            var r = await job.RunAsync(licenseId, apply, ct, atTwins);
            if (r.Groups > 0)
                output.WriteLine($"{licenseId}: {Format(r, atTwins)}");
            total = Sum(total, r);
        }
        output.WriteLine($"{(apply ? "UYGULANDI" : "KURU ÇALIŞTIRMA")} — TOPLAM ({licenses.Length} lisans): {Format(total, atTwins)}");

        if (!apply) return 0;

        var fixedAfter = await repair.RunAsync(ct);
        db.ChangeTracker.Clear();
        var mismatched = await job.CountMismatchedKeysAsync(ct);
        var duplicates = await job.CountDuplicateHeadsAsync(ct);
        var chains = await job.CountChainsAsync(ct);
        output.WriteLine($"Anahtar onarımı (sonra): {fixedAfter} satır; kalan uyuşmaz anahtar: {mismatched}; B1 kapısı (SQL, tüm lisanslar): yinelenen asıl kayıt grubu {duplicates}; zincir (kopyanın kopyası): {chains}");
        // "@" ikizi kipinin son koşulu: aynı gruplamayla yeniden sayım — yalnız bu
        // koşunun lisansları (B1 kapısı gibi bir göçe bağlı değil).
        var twinsLeft = 0;
        if (atTwins)
        {
            foreach (var licenseId in licenses)
                twinsLeft += await job.CountGroupsAsync(licenseId, atTwins: true, ct);
            output.WriteLine($"Kalan @ ikizi grubu ({licenses.Length} lisans): {twinsLeft} (0 olmalı)");
        }
        if (mismatched > 0)
        {
            error.WriteLine("SON KOŞUL TUTMADI: uyuşmaz anahtar var — B1 (tekil indeks) göçü bu hâlde düşer. Komutu yeniden çalıştırın; sürerse inceleyin.");
            return 3;
        }
        // Atlanan grup B1 kapısından (ve kalan ikiz sayımından) ÖNCE: atlanan
        // grubun kopyaları orada zaten görünür; önce onlar denetlenseydi çıkış 1
        // hiç dönmezdi.
        if (total.FailedGroups > 0)
        {
            error.WriteLine($"{total.FailedGroups} grup eşzamanlı değişiklik ya da veritabanı hatası yüzünden geri alınıp atlandı; komutu yeniden çalıştırın (biten gruplar kalıcı; B1 kapısındaki ve kalan ikiz sayısı atlanan grupları da içerir).");
            return 1;
        }
        if (duplicates > 0)
        {
            error.WriteLine($"SON KOŞUL TUTMADI: B1 kapısı {duplicates} yinelenen asıl kayıt grubu buluyor (tüm lisanslar) — B1 göçü bu hâlde düşer. --license ile koşulduysa --all ile koşun; sürerse inceleyin.");
            return 3;
        }
        if (chains > 0)
        {
            error.WriteLine($"SON KOŞUL TUTMADI: {chains} zincir (kopyanın kopyası) var (tüm lisanslar) — bir yarışın izi: --apply sırasında açık kalan bir bilgisayar. Bilgisayarları kapatıp inceleyin.");
            return 3;
        }
        if (twinsLeft > 0)
        {
            error.WriteLine($"SON KOŞUL TUTMADI: {twinsLeft} @ ikizi grubu kaldı — --apply sırasında açık kalan bir bilgisayarın ya da eski (v0.9.9 öncesi) sürümün yeni gönderimi olabilir. Bilgisayarları kapatıp komutu yeniden çalıştırın; sürerse inceleyin.");
            return 3;
        }
        return 0;
    }

    /// <summary>args[0] komut adıdır. Bkz. sınıf dokümanı: katı ayrıştırma.
    /// <para><b>Neden <c>public</c>:</b> geçerli argümanlı yol yapılandırmaya ve
    /// veritabanına gider; kabul edilen biçimler (ör. <c>--at-twins</c>) yalnız
    /// saf fonksiyon olarak sınanabilir. Sunucu projesinde
    /// <c>InternalsVisibleTo</c> yok — kalıp <c>PanelNetgsmAccountController</c>'daki
    /// gibi.</para></summary>
    public static bool TryParseArgs(string[] args, out bool all, out Guid? license, out bool apply, out bool atTwins)
    {
        all = false;
        apply = false;
        atTwins = false;
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
                case "--at-twins" when !atTwins:
                    atTwins = true;
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

    /// <param name="atTwins">"@" ikizi kipi satırın başında görünür: iki kipin
    /// sayıları farklı şeyleri sayar, karıştırılmasın. Bu kipte "harf/boşluk
    /// farklı" anlamsız (her ikiz yazımca farklıdır) — yerine "@"lı asıl kayıtla
    /// biten grup yazılır.</param>
    private static string Format(CustomerIdentityMergeJob.Report r, bool atTwins)
    {
        var platforms = r.GroupsByPlatform.Count == 0
            ? ""
            : " (" + string.Join(", ", r.GroupsByPlatform.OrderBy(p => p.Key, StringComparer.Ordinal)
                .Select(p => $"{p.Key}={p.Value}")) + ")";
        var mode = atTwins
            ? $"@ ikizi kipi | kopyalı kişi={r.Groups}{platforms} \"@\"lı asıl kayıt={r.AtSpelledCanonicals} "
            : $"kopyalı kişi={r.Groups}{platforms} harf/boşluk farklı={r.VariantGroups} ";
        return mode + $"kopya satır={r.CopyRows} " +
               $"sipariş={r.OrdersToMove} kargo={r.ShipmentsToMove} Shopper bağlantısı={r.LinksToMove} " +
               $"toplanacak bakiye={r.BalancesToSum} bakiye hareketi={r.BalanceTransactionsToMove} " +
               $"IBAN hafızası={r.IbanMemoriesToMove} ödeme eşleşmesi={r.PaymentMatchesToMove} " +
               $"WhatsApp sohbeti={r.WaConversationsToMove} silinmiş kişi={r.PurgedGroups} | " +
               $"asıl kayıtla kopyada ikisi de dolu ama farklı (grup): telefon={r.PhoneConflicts} adres={r.AddressConflicts} " +
               $"e-posta={r.EmailConflicts} ad={r.NameConflicts} not={r.NotesConflicts} | " +
               $"{(atTwins ? "birleştirilen kişi grubu" : "farklı grup numaralı kişi")}={r.GroupConflicts} | " +
               $"atlanan grup={r.FailedGroups} beklemeye düşen Shopper bağlantısı={r.LinksUnbound}";
    }

    private static CustomerIdentityMergeJob.Report Sum(CustomerIdentityMergeJob.Report a, CustomerIdentityMergeJob.Report b) => new(
        a.Groups + b.Groups, a.CopyRows + b.CopyRows, a.OrdersToMove + b.OrdersToMove,
        a.ShipmentsToMove + b.ShipmentsToMove, a.LinksToMove + b.LinksToMove,
        a.BalancesToSum + b.BalancesToSum, a.PurgedGroups + b.PurgedGroups, a.FailedGroups + b.FailedGroups,
        a.LinksUnbound + b.LinksUnbound)
    {
        GroupsByPlatform = a.GroupsByPlatform.Concat(b.GroupsByPlatform)
            .GroupBy(p => p.Key, StringComparer.Ordinal)
            .ToImmutableSortedDictionary(g => g.Key, g => g.Sum(p => p.Value), StringComparer.Ordinal),
        VariantGroups = a.VariantGroups + b.VariantGroups,
        PhoneConflicts = a.PhoneConflicts + b.PhoneConflicts,
        AddressConflicts = a.AddressConflicts + b.AddressConflicts,
        EmailConflicts = a.EmailConflicts + b.EmailConflicts,
        NameConflicts = a.NameConflicts + b.NameConflicts,
        NotesConflicts = a.NotesConflicts + b.NotesConflicts,
        GroupConflicts = a.GroupConflicts + b.GroupConflicts,
        AtSpelledCanonicals = a.AtSpelledCanonicals + b.AtSpelledCanonicals,
        BalanceTransactionsToMove = a.BalanceTransactionsToMove + b.BalanceTransactionsToMove,
        IbanMemoriesToMove = a.IbanMemoriesToMove + b.IbanMemoriesToMove,
        PaymentMatchesToMove = a.PaymentMatchesToMove + b.PaymentMatchesToMove,
        WaConversationsToMove = a.WaConversationsToMove + b.WaConversationsToMove,
    };
}
