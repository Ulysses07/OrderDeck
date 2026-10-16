using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Sync;

namespace OrderDeck.LicenseServer.Tools;

/// <summary>
/// Konteyner içinde elle koşulur:
///   docker exec orderdeck-license dotnet OrderDeck.LicenseServer.dll sales-sync &lt;alt komut&gt; [seçenekler]
/// Yayın ve etiket senkronunun yönetim komutu (spec 2026-10-13 §4.10). Alt komutlar:
/// <list type="bullet">
///   <item><c>status --license &lt;guid&gt;</c> — salt okunur durum: mod, sürüm kapısı, eski sürüm izi, kargo ayarı ve
///   lisansın bütün kopyaları (§4.10, §6.2, §8.6). Gövdesi <c>SalesSync.Status.cs</c>'te.</item>
///   <item><c>shipping set --license &lt;guid&gt; (--threshold &lt;tutar&gt; --fee &lt;tutar&gt; | --off) [--apply]</c> —
///   lisans geneli kargo ayarı (§4.8, §6.2, sahip kararı Y4). Aşağıda.</item>
///   <item><c>force-end --session &lt;guid&gt; [--apply]</c> — sahibi hiç dönmeyecek canlı ya da devrediliyor yayını
///   bitirir ve devreder (§2 "Devir" b, §4.10, Y7, Y8, risk 4). Gövdesi <c>SalesSync.ForceEnd.cs</c>'te.</item>
///   <item><c>adopt-orders --license &lt;guid&gt; --orders &lt;guid,…&gt; --replica &lt;guid&gt; [--apply]</c> — kargo bağı
///   bilinmeyen siparişi <c>attach-shipment</c> adayı yapar (§4.6, M22, §15-44). Gövdesi
///   <c>SalesSync.AdoptOrders.cs</c>'te.</item>
///   <item><c>enable --license &lt;guid&gt; [--accept-shipment &lt;guid&gt;]… [--apply]</c> — mod kapısı: ön koşullar
///   1–6, bilgi raporu 7, kabul edilen kargo dosyası ve açılışta dosyaların türetilmesi (§4.10, M1). Gövdesi
///   <c>SalesSync.Enable.cs</c>'te; koşullar <see cref="SalesModeGate"/>'te.</item>
/// </list>
/// <c>disable</c> S3'ün sonraki adımında gelir; şimdilik kullanım hatasıdır.
///
/// <para><b>Kuru koşu varsayılan.</b> Yazan alt komutlar <c>--apply</c> olmadan hiçbir şey yazmaz; yazılacak olanı
/// basar. <c>status</c> hiç yazmaz; ona <c>--apply</c> vermek kullanım hatasıdır.</para>
///
/// <para>Çıkış kodları: 0 tamam (rapor, kuru koşu, yazıldı ya da yapılacak bir şey yok); 1 eşzamanlı bir değişiklik
/// kazandı ya da (<c>enable --apply</c>) mod kapısının bir koşulu tutmuyor, hiçbir şey yazılmadı; 2 kullanım,
/// yapılandırma ya da bilinmeyen lisans, yayın, kopya veya kabul edilen dosya.</para>
///
/// <para>Argümanlar katı (<see cref="GroupCustomers"/>, <see cref="SalesAudit"/> deseni): tanınmayan alt komut ya da
/// argüman, değeri eksik ya da değer yerine seçenek, ikinci kez verilen seçenek (bayrak dahil), GUID olmayan Id,
/// eksik zorunlu seçenek → 2, yapılandırmaya ve veritabanına dokunmadan. Alt komutlara özgü kurallar kendi
/// ayrıştırıcılarında.</para>
///
/// Bağlantı dizesi ve <c>SalesSync</c> ayarları web host'unkiyle aynı kaynaktan (appsettings.json ve ortam
/// değişkenleri: <c>ConnectionStrings:LicenseDb</c>, <c>SalesSync__MinClientVersion</c>). Çıktıda kişisel veri ve
/// makine adı YOK — yalnız sayılar, Id'ler, tutarlar ve zamanlar; GUID olmayan ham müşteri Id'si de basılmaz.
///
/// <para><b>shipping set.</b> Kuru koşu varsayılan: şu anki satırı (sürüm, değerler, yazan), son 14 günde görülmüş
/// kopyaların hello'da bildirdiği farklı değerleri (kopya Id'siyle) ve yazılacak değeri basar, hiçbir şey yazmaz.
/// <c>--apply</c> değeri <c>UpdatedBy = cli</c> ve <c>Version + 1</c> ile yazar. İki kipte de çalışır: mod Off'ta mod
/// açılmadan önceki hazırlıktır ve lisans geneli ayarın tek yazıcısıdır (mod kapısının 4. koşulu satırın varlığını
/// ister); mod On'da WPF ayar ekranıyla aynı satırı yazar. Eşzamanlılık: komut okuduğu sürümü bekler; okuma ile yazma
/// arasında başka bir yazım (WPF'in PUT'u) gelirse onun üzerine yazmaz, 1 döner. Yalnız eşik ya da yalnız ücret,
/// değerle birlikte <c>--off</c> ve geçersiz tutar da kullanım hatasıdır. Tutar <c>InvariantCulture</c> ile, yalnız
/// rakam ve ondalık nokta: <c>500,5</c> ya da <c>1,500</c> sessizce başka bir sayıya dönmez, reddedilir. Geçerli tutar
/// <see cref="LicenseShippingSettings.IsValid"/>: &gt; 0, en çok iki ondalık.</para>
/// </summary>
public static partial class SalesSync
{
    private const string Usage =
        "Kullanım:\n"
        + "  sales-sync status --license <guid>\n"
        + "  sales-sync shipping set --license <guid> (--threshold <tutar> --fee <tutar> | --off) [--apply]\n"
        + "  sales-sync force-end --session <guid> [--apply]\n"
        + "  sales-sync adopt-orders --license <guid> --orders <guid,…> --replica <guid> [--apply]\n"
        + "  sales-sync enable --license <guid> [--accept-shipment <guid>]... [--apply]";

    /// <summary>Kopyaların bildirdiği değerlerin penceresi: mod kapısının "son 14 günde görülen" kopyaları (§4.10-2).</summary>
    public static readonly TimeSpan RecentWindow = SalesModeGate.RecentWindow;

    /// <summary>Ayrıştırılmış alt komut.</summary>
    public abstract record SalesSyncArgs;

    /// <summary><c>shipping set</c>'in ayrıştırılmış argümanları.</summary>
    /// <param name="Threshold"><c>--off</c>'ta null.</param>
    /// <param name="Fee"><c>--off</c>'ta null.</param>
    /// <param name="Apply">false = kuru koşu.</param>
    public sealed record ShippingSetArgs(Guid License, decimal? Threshold, decimal? Fee, bool Apply) : SalesSyncArgs;

    /// <summary><c>status</c>'un ayrıştırılmış argümanları (salt okunur; <c>--apply</c> yok).</summary>
    public sealed record StatusArgs(Guid License) : SalesSyncArgs;

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
        var salesSync = config.GetSection(SalesSyncOptions.Section).Get<SalesSyncOptions>() ?? new SalesSyncOptions();

        var options = new DbContextOptionsBuilder<LicenseDbContext>().UseSqlServer(conn).Options;
        await using var db = new LicenseDbContext(options);
        var output = Console.Out;
        var error = Console.Error;
        var ct = CancellationToken.None;
        return parsed switch
        {
            ShippingSetArgs shipping => await RunAsync(db, shipping, output, error, ct),
            StatusArgs status => await RunAsync(db, status, salesSync, output, error, ct),
            ForceEndArgs forceEnd => await RunAsync(db, forceEnd, output, error, ct),
            AdoptOrdersArgs adopt => await RunAsync(db, adopt, output, error, ct),
            EnableArgs enable => await RunAsync(db, enable, salesSync, output, error, ct),
            _ => throw new InvalidOperationException($"Bilinmeyen alt komut: {parsed.GetType().Name}"),
        };
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
            .Select(r => new ReportedShipping(r.ReplicaId, r.ReportedThreshold, r.ReportedFee))
            .ToListAsync(ct);

        output.WriteLine(
            $"{(args.Apply ? "UYGULAMA" : "KURU ÇALIŞTIRMA")} — sales-sync shipping set — lisans {args.License} — "
            + Time(now));
        output.WriteLine($"Mod: {license.SalesSyncMode}");
        WriteSettings(output, "Şu anki ayar", current);
        WriteReportedShipping(output, reported);

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
    }

    // ── Ayrıştırma ────────────────────────────────────────────────────────────

    /// <summary>
    /// args[0] komut adıdır, args[1] alt komut (<c>shipping set</c> iki kelime), ardından seçenekler. Bkz. sınıf
    /// dokümanı: katı ayrıştırma. <c>public</c>: geçerli argümanlı yol yapılandırmaya ve veritabanına gider; kabul
    /// edilen biçimler yalnız saf fonksiyon olarak sınanabilir.
    /// </summary>
    public static bool TryParseArgs(string[] args, [NotNullWhen(true)] out SalesSyncArgs? parsed)
    {
        parsed = null;
        if (args.Length < 2) return false;
        return args[1] switch
        {
            "shipping" => args.Length >= 3 && args[2] == "set" && TryParseShippingSet(args, 3, out parsed),
            "status" => TryParseStatus(args, 2, out parsed),
            "force-end" => TryParseForceEnd(args, 2, out parsed),
            "adopt-orders" => TryParseAdoptOrders(args, 2, out parsed),
            "enable" => TryParseEnable(args, 2, out parsed),
            _ => false,
        };
    }

    private static bool TryParseShippingSet(string[] args, int start, [NotNullWhen(true)] out SalesSyncArgs? parsed)
    {
        parsed = null;
        if (!TryReadOptions(args, start, ["--license", "--threshold", "--fee"], ["--off", "--apply"],
                out var values, out var flags)
            || !TryGuidOption(values, "--license", out var license))
            return false;

        decimal? threshold = null;
        decimal? fee = null;
        if (values.TryGetValue("--threshold", out var text))
        {
            if (!TryParseAmount(text, out var t)) return false;
            threshold = t;
        }
        if (values.TryGetValue("--fee", out text))
        {
            if (!TryParseAmount(text, out var f)) return false;
            fee = f;
        }

        // Ya eşik ile ücret birlikte ya da yalnız --off.
        var valuesGiven = threshold is not null || fee is not null;
        if (flags.Contains("--off") == valuesGiven) return false;
        if (!LicenseShippingSettings.IsValid(threshold, fee)) return false;

        parsed = new ShippingSetArgs(license, threshold, fee, flags.Contains("--apply"));
        return true;
    }

    /// <summary><c>status</c>: yalnız <c>--license</c>; salt okunur olduğu için <c>--apply</c> tanınmaz.</summary>
    private static bool TryParseStatus(string[] args, int start, [NotNullWhen(true)] out SalesSyncArgs? parsed)
    {
        parsed = null;
        if (!TryReadOptions(args, start, ["--license"], [], out var values, out _)
            || !TryGuidOption(values, "--license", out var license))
            return false;

        parsed = new StatusArgs(license);
        return true;
    }

    /// <summary>
    /// Seçenekleri okur: <paramref name="valued"/> değer alır (bir sonraki argüman; var olmalı ve seçenek olmamalı),
    /// <paramref name="flags"/> değer almaz. Tanınmayan argüman ya da ikinci kez verilen seçenek → false.
    /// </summary>
    private static bool TryReadOptions(
        string[] args, int start, string[] valued, string[] flags,
        out Dictionary<string, string> values, out HashSet<string> present)
    {
        values = new Dictionary<string, string>(StringComparer.Ordinal);
        present = new HashSet<string>(StringComparer.Ordinal);
        for (var i = start; i < args.Length; i++)
        {
            var arg = args[i];
            if (flags.Contains(arg, StringComparer.Ordinal))
            {
                if (!present.Add(arg)) return false;
            }
            else if (valued.Contains(arg, StringComparer.Ordinal))
            {
                if (values.ContainsKey(arg) || !TryValue(args, i, out var value)) return false;
                values[arg] = value;
                i++;
            }
            else
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Zorunlu GUID seçeneği: verilmiş ve GUID olmalı.</summary>
    private static bool TryGuidOption(Dictionary<string, string> values, string name, out Guid value)
    {
        value = Guid.Empty;
        return values.TryGetValue(name, out var text) && Guid.TryParse(text, out value);
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

    // ── Ortak çıktı ───────────────────────────────────────────────────────────

    /// <summary>Kopyanın hello'da bildirdiği kargo ayarı (§6.2).</summary>
    private sealed record ReportedShipping(Guid ReplicaId, decimal? Threshold, decimal? Fee);

    /// <summary>Lisans geneli kargo ayarı tek satırda; satır yoksa "yok (sürüm 0)".</summary>
    private static void WriteSettings(TextWriter output, string label, LicenseShippingSettingsStore.Snapshot current) =>
        output.WriteLine(current == LicenseShippingSettingsStore.Missing
            ? $"{label}: yok (sürüm 0)"
            : $"{label}: sürüm {current.Version}, {Values(current.FreeShippingThreshold, current.Fee)}, "
                + $"yazan={current.UpdatedBy ?? "boş"}, zaman={Time(current.UpdatedAt)}");

    /// <summary>Kopyaların bildirdiği farklı kargo değerleri, her biri onu bildiren kopya Id'leriyle (§6.2). Satırlar
    /// son <see cref="RecentWindow"/>'da görülen kopyalardan olmalı; değerler sıralı, boş değer sonda.</summary>
    private static void WriteReportedShipping(TextWriter output, IEnumerable<ReportedShipping> recent)
    {
        var groups = recent
            .GroupBy(r => (r.Threshold, r.Fee))
            .OrderBy(g => g.Key.Threshold is null)
            .ThenBy(g => g.Key.Threshold)
            .ThenBy(g => g.Key.Fee)
            .ToList();
        output.WriteLine(
            $"Kopyaların bildirdiği kargo ayarı (son {RecentWindow.Days} gün): {groups.Count} farklı değer");
        foreach (var g in groups)
        {
            var ids = g.Select(r => r.ReplicaId.ToString()).Order(StringComparer.Ordinal);
            output.WriteLine($"   eşik={Amount(g.Key.Threshold)} ücret={Amount(g.Key.Fee)}: " + string.Join(", ", ids));
        }
    }

    private static string Amount(decimal? value) => value?.ToString("0.00", CultureInfo.InvariantCulture) ?? "boş";

    private static string Values(decimal? threshold, decimal? fee) => $"eşik={Amount(threshold)}, ücret={Amount(fee)}";

    /// <summary>Sunucu saati, UTC ("u"); boşsa "yok".</summary>
    private static string Time(DateTimeOffset? at) => at?.ToString("u", CultureInfo.InvariantCulture) ?? "yok";

    /// <summary>Id'ler tek girintili satırda, sıralı; küme boşsa satır yok.</summary>
    private static void WriteIds(TextWriter output, IEnumerable<Guid> ids)
    {
        var list = ids.Select(id => id.ToString()).Order(StringComparer.Ordinal).ToList();
        if (list.Count > 0) output.WriteLine("   " + string.Join(", ", list));
    }
}
