using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Sync;

namespace OrderDeck.LicenseServer.Tools;

/// <summary>
/// Konteyner içinde elle koşulur:
///   docker exec orderdeck-license dotnet OrderDeck.LicenseServer.dll \
///       sales-sync shipping set --license &lt;guid&gt; (--threshold &lt;tutar&gt; --fee &lt;tutar&gt; | --off) [--apply]
/// Yayın ve etiket senkronunun yönetim komutu (spec 2026-10-13 §4.10). S2'de tek alt komut var: lisans geneli kargo
/// ayarını yazmak (§4.8, §6.2, sahip kararı Y4). <c>status</c>, <c>enable</c>, <c>disable</c>, <c>force-end</c> ve
/// <c>adopt-orders</c> S3'te gelir; şimdilik kullanım hatasıdır.
///
/// <para><b>shipping set.</b> Kuru koşu varsayılan: şu anki satırı (sürüm, değerler, yazan), son 14 günde görülmüş
/// kopyaların hello'da bildirdiği farklı değerleri (kopya Id'siyle) ve yazılacak değeri basar, hiçbir şey yazmaz.
/// <c>--apply</c> değeri <c>UpdatedBy = cli</c> ve <c>Version + 1</c> ile yazar. İki kipte de çalışır: mod Off'ta mod
/// açılmadan önceki hazırlıktır ve lisans geneli ayarın tek yazıcısıdır (mod kapısının 4. koşulu satırın varlığını
/// ister); mod On'da WPF ayar ekranıyla aynı satırı yazar. Eşzamanlılık: komut okuduğu sürümü bekler; okuma ile yazma
/// arasında başka bir yazım (WPF'in PUT'u) gelirse onun üzerine yazmaz, 1 döner.</para>
///
/// <para>Çıkış kodları: 0 tamam (kuru koşu ya da yazıldı); 1 eşzamanlı bir yazım kazandı, hiçbir şey yazılmadı —
/// yeniden çalıştır; 2 kullanım, yapılandırma ya da bilinmeyen lisans.</para>
///
/// <para>Argümanlar katı (<see cref="GroupCustomers"/>, <see cref="SalesAudit"/> deseni): tanınmayan argüman, değeri
/// eksik ya da değer yerine seçenek, ikinci kez verilen seçenek, GUID olmayan <c>--license</c>, yalnız eşik ya da yalnız
/// ücret, değerle birlikte <c>--off</c>, geçersiz tutar → 2, yapılandırmaya ve veritabanına dokunmadan. Tutar
/// <c>InvariantCulture</c> ile, yalnız rakam ve ondalık nokta: <c>500,5</c> ya da <c>1,500</c> sessizce başka bir sayıya
/// dönmez, reddedilir. Geçerli tutar <see cref="LicenseShippingSettings.IsValid"/>: &gt; 0, en çok iki ondalık.</para>
///
/// Bağlantı dizesi web host'unkiyle aynı kaynaktan (ConnectionStrings:LicenseDb, ortam değişkeni dahil). Çıktıda
/// kişisel veri ve makine adı YOK — yalnız lisans ve kopya Id'leri, tutarlar ve sürüm.
/// </summary>
public static class SalesSync
{
    private const string Usage =
        "Kullanım: sales-sync shipping set --license <guid> (--threshold <tutar> --fee <tutar> | --off) [--apply]";

    /// <summary>Kopyaların bildirdiği değerlerin penceresi: mod kapısının "son 14 günde görülen" kopyaları (§4.10-2).</summary>
    public static readonly TimeSpan RecentWindow = TimeSpan.FromDays(14);

    /// <summary><c>shipping set</c>'in ayrıştırılmış argümanları.</summary>
    /// <param name="Threshold"><c>--off</c>'ta null.</param>
    /// <param name="Fee"><c>--off</c>'ta null.</param>
    /// <param name="Apply">false = kuru koşu.</param>
    public sealed record ShippingSetArgs(Guid License, decimal? Threshold, decimal? Fee, bool Apply);

    public static async Task<int> RunAsync(string[] args)
    {
        if (!TryParseArgs(args, out var parsed))
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
        return await RunAsync(db, parsed, Console.Out, Console.Error, CancellationToken.None);
    }

    /// <summary><c>shipping set</c>'in gövdesi: argümanlar ayrıştırılmış, bağlantı nesnesi kurulmuş. Testler gerçek SQL
    /// Server'a buradan girer.</summary>
    public static async Task<int> RunAsync(
        LicenseDbContext db, ShippingSetArgs args, TextWriter output, TextWriter error, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var license = await db.Licenses.AsNoTracking()
            .Where(l => l.Id == args.License)
            .Select(l => new { l.SalesSyncMode })
            .SingleOrDefaultAsync(ct);
        if (license is null)
        {
            error.WriteLine($"Lisans bulunamadı: {args.License}. Hiçbir şey yazılmadı.");
            return 2;
        }

        var current = await LicenseShippingSettingsStore.ReadAsync(db, args.License, ct);
        var since = now - RecentWindow;
        var reported = await db.WpfReplicas.AsNoTracking()
            .Where(r => r.LicenseId == args.License && r.LastSeenAt >= since)
            .Select(r => new { r.ReplicaId, r.ReportedThreshold, r.ReportedFee })
            .ToListAsync(ct);
        var groups = reported
            .GroupBy(r => (r.ReportedThreshold, r.ReportedFee))
            .OrderBy(g => g.Key.ReportedThreshold is null)
            .ThenBy(g => g.Key.ReportedThreshold)
            .ThenBy(g => g.Key.ReportedFee)
            .ToList();

        var inv = CultureInfo.InvariantCulture;
        output.WriteLine(
            $"{(args.Apply ? "UYGULAMA" : "KURU ÇALIŞTIRMA")} — sales-sync shipping set — lisans {args.License} — "
            + now.ToString("u", inv));
        output.WriteLine($"Mod: {license.SalesSyncMode}");
        output.WriteLine(current == LicenseShippingSettingsStore.Missing
            ? "Şu anki ayar: yok (sürüm 0)"
            : $"Şu anki ayar: sürüm {current.Version}, {Values(current.FreeShippingThreshold, current.Fee)}, "
                + $"yazan={current.UpdatedBy ?? "boş"}, zaman={current.UpdatedAt!.Value.ToString("u", inv)}");
        output.WriteLine(
            $"Kopyaların bildirdiği kargo ayarı (son {RecentWindow.Days} gün): {groups.Count} farklı değer");
        foreach (var g in groups)
        {
            var ids = g.Select(r => r.ReplicaId.ToString()).Order(StringComparer.Ordinal);
            output.WriteLine($"   eşik={Amount(g.Key.ReportedThreshold)} ücret={Amount(g.Key.ReportedFee)}: "
                + string.Join(", ", ids));
        }

        var next = current.Version + 1;
        output.WriteLine(args.Threshold is null
            ? $"Yazılacak: kargo kapalı (eşik ve ücret boş), sürüm {next}, yazan={LicenseShippingSettings.UpdatedByCli}"
            : $"Yazılacak: {Values(args.Threshold, args.Fee)}, sürüm {next}, yazan={LicenseShippingSettings.UpdatedByCli}");

        if (!args.Apply)
        {
            output.WriteLine("Kuru çalıştırma: hiçbir şey yazılmadı. Yazmak için --apply ekle.");
            return 0;
        }

        var result = await LicenseShippingSettingsStore.WriteAsync(
            db, args.License, current.Version, args.Threshold, args.Fee, LicenseShippingSettings.UpdatedByCli, now, ct);
        if (result is LicenseShippingSettingsStore.Stale stale)
        {
            error.WriteLine(
                $"Ayar bu arada başka bir yazımla değişti (şimdi sürüm {stale.Current.Version}, "
                + $"{Values(stale.Current.FreeShippingThreshold, stale.Current.Fee)}, "
                + $"yazan={stale.Current.UpdatedBy ?? "boş"}); hiçbir şey yazılmadı. Komutu yeniden çalıştır.");
            return 1;
        }

        output.WriteLine($"UYGULANDI: sürüm {result.Current.Version}, {Values(result.Current.FreeShippingThreshold, result.Current.Fee)}, "
            + $"yazan={result.Current.UpdatedBy}");
        return 0;

        string Amount(decimal? value) => value?.ToString("0.00", inv) ?? "boş";
        string Values(decimal? threshold, decimal? fee) => $"eşik={Amount(threshold)}, ücret={Amount(fee)}";
    }

    /// <summary>
    /// args[0] komut adıdır, ardından alt komut (<c>shipping set</c>) ve seçenekler. Bkz. sınıf dokümanı: katı
    /// ayrıştırma. <c>public</c>: geçerli argümanlı yol yapılandırmaya ve veritabanına gider; kabul edilen biçimler
    /// yalnız saf fonksiyon olarak sınanabilir.
    /// </summary>
    public static bool TryParseArgs(string[] args, out ShippingSetArgs parsed)
    {
        parsed = new ShippingSetArgs(Guid.Empty, null, null, false);
        if (args.Length < 3 || args[1] != "shipping" || args[2] != "set") return false;

        Guid? license = null;
        decimal? threshold = null;
        decimal? fee = null;
        var off = false;
        var apply = false;
        for (var i = 3; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--apply" when !apply:
                    apply = true;
                    break;
                case "--off" when !off:
                    off = true;
                    break;
                case "--license" when license is null:
                    if (!TryValue(args, i, out var text) || !Guid.TryParse(text, out var id)) return false;
                    license = id;
                    i++;
                    break;
                case "--threshold" when threshold is null:
                    if (!TryValue(args, i, out text) || !TryParseAmount(text, out var t)) return false;
                    threshold = t;
                    i++;
                    break;
                case "--fee" when fee is null:
                    if (!TryValue(args, i, out text) || !TryParseAmount(text, out var f)) return false;
                    fee = f;
                    i++;
                    break;
                default:
                    return false;
            }
        }

        // Ya eşik ile ücret birlikte ya da yalnız --off.
        var valuesGiven = threshold is not null || fee is not null;
        if (license is null || off == valuesGiven) return false;
        if (!LicenseShippingSettings.IsValid(threshold, fee)) return false;

        parsed = new ShippingSetArgs(license.Value, threshold, fee, apply);
        return true;
    }

    /// <summary>Seçeneğin değeri: bir sonraki argüman, var olmalı ve bir seçenek olmamalı ("--fee --apply" ücretsiz bir
    /// kuru koşuya dönüşmesin).</summary>
    private static bool TryValue(string[] args, int i, out string value)
    {
        value = i + 1 < args.Length ? args[i + 1] : "";
        return value.Length > 0 && !value.StartsWith("--", StringComparison.Ordinal);
    }

    /// <summary>Yalnız rakam ve ondalık nokta (işaret, binlik ayraç, üs, boşluk yok).</summary>
    private static bool TryParseAmount(string text, out decimal value) =>
        decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value);
}
