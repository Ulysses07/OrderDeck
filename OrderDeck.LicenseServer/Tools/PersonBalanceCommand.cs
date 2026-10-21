using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.CustomerSync;

namespace OrderDeck.LicenseServer.Tools;

/// <summary>
/// Konteyner içinde elle koşulur:
///   docker exec orderdeck-license dotnet OrderDeck.LicenseServer.dll person-balance verify [seçenekler]
/// Ortak bakiye denetimi (spec 2026-10-11 §7.3; çekirdek <see cref="PersonBalanceVerifier"/>):
/// <list type="bullet">
///   <item><c>verify [--license &lt;guid&gt;] [--details]</c> — salt okunur rapor (§7.3 maddeleri 1–10). Lisans
///   verilmezse bütün lisanslar, verisizler dahil (plan fark 16). <c>--details</c> eksi, D1, kopya ve kural bozan
///   satırların <c>WpfCustomerId</c>'lerini, kural bozan grupların ve yetim bacakların hareket Id'lerini (GUID; ad,
///   telefon yok) konsola basar; dosyaya yazılmaz.</item>
///   <item><c>--capture-baseline [--apply]</c> — taban çizgisi (§7.1 adım 2): tabanı olmayan lisansın taban dışı eksi,
///   D1 ve kopya satırları o anki Balance ve LedgerSum'la, lisans başına bir işaret satırıyla. Var olan taban satırına
///   dokunmaz; tabanı olan lisansın taban dışı satırı yakalamadan sonra doğmuş yeni sapmadır, tabana alınmaz (plan
///   fark 20, <see cref="PersonBalanceVerifier.BaselineRowsToCapture"/>).</item>
///   <item><c>--mark-reviewed --license &lt;guid&gt; [--apply]</c> — sahip incelemesi (§7.1 adım 4): lisansın
///   incelenmemiş taban satırlarının <c>ReviewedAt</c>'i.</item>
/// </list>
/// Yazan bayraklar <c>--apply</c> olmadan yazacağını basar; yalnız <c>PersonBalanceBaselines</c>'a yazar. Rapora
/// <c>--apply</c> vermek kullanım hatasıdır. Çıkış: 0 tamam; 1 eşzamanlı yazım ya da işaretsiz lisansta inceleme,
/// hiçbir şey yazılmadı; 2 kullanım, yapılandırma ya da bilinmeyen lisans. Argümanlar katı (<see cref="SalesSync"/>
/// deseni). Bağlantı dizesi web host'unkiyle aynı kaynaktan (<c>ConnectionStrings:LicenseDb</c>). Çıktıda ad, telefon,
/// makine adı YOK — yalnız sayılar, tutarlar, Id'ler. <c>enable</c>/<c>disable</c> S2'de bu sınıfa eklenir.
/// </summary>
public static class PersonBalanceCommand
{
    private const string Usage =
        "Kullanım:\n"
        + "  person-balance verify [--license <guid>] [--details] [--capture-baseline | --mark-reviewed] [--apply]";

    /// <summary>Ayrıştırılmış <c>verify</c>.</summary>
    public sealed record VerifyArgs(Guid? License, bool Details, bool CaptureBaseline, bool MarkReviewed, bool Apply);

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

    /// <summary>Gövde: argümanlar ayrıştırılmış, bağlam kurulmuş. Testler buradan girer.</summary>
    public static async Task<int> RunAsync(
        LicenseDbContext db, VerifyArgs args, TextWriter output, TextWriter error, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        if (args.License is { } only && !await db.Licenses.AsNoTracking().AnyAsync(l => l.Id == only, ct))
        {
            error.WriteLine($"Lisans bulunamadı: {only}. Hiçbir şey yazılmadı.");
            return 2;
        }

        var snapshot = await new PersonBalanceVerifier(db).LoadAsync(args.License, now, ct);
        var report = PersonBalanceVerifier.Analyze(snapshot);

        var mode = !(args.CaptureBaseline || args.MarkReviewed) ? "RAPOR (salt okunur)"
            : args.Apply ? "UYGULAMA" : "KURU ÇALIŞTIRMA";
        output.WriteLine($"{mode} — person-balance verify — {Time(now)}");
        foreach (var license in report.Licenses) WriteLicense(output, license, args.Details);
        output.WriteLine(
            $"10. Satır sayıları: WpfCustomerProjections {report.ProjectionCount}, CustomerBalanceTransactions {report.TransactionCount}");

        if (args.CaptureBaseline) return await CaptureAsync(db, snapshot, report, args.Apply, now, output, error, ct);
        if (args.MarkReviewed) return await MarkReviewedAsync(db, args.License!.Value, args.Apply, now, output, error, ct);
        return 0;
    }

    private static async Task<int> CaptureAsync(
        LicenseDbContext db, PersonBalanceVerifier.Snapshot snapshot, PersonBalanceVerifier.Report report, bool apply,
        DateTimeOffset now, TextWriter output, TextWriter error, CancellationToken ct)
    {
        var rows = new List<PersonBalanceBaseline>();
        foreach (var license in report.Licenses)
        {
            var add = PersonBalanceVerifier.BaselineRowsToCapture(snapshot, license, now);
            var marker = add.Any(r => r.WpfCustomerId == PersonBalanceVerifier.LicenseMarker);
            var captured = add.Where(r => r.WpfCustomerId != PersonBalanceVerifier.LicenseMarker)
                .Select(r => r.WpfCustomerId).ToHashSet();
            // Döküm yakalanacak satırlardan (plan fark 20): tabanı olan lisansta taban dışı satır yazılmaz, Details'in
            // sayıları ise onları da sayar. Bir satır birden çok kalemde olabilir (eksi ve D1).
            var line = $"Taban — lisans {license.LicenseId}: eklenecek {captured.Count} satır "
                + $"({Breakdown(license.Details, captured.Contains)}), işaret {(marker ? "eklenecek" : "zaten var")}";
            var left = license.Details.NegativeOutside
                .Concat(license.Details.LedgerMismatchOutside)
                .Concat(license.Details.CopyOutside)
                .Any(id => !captured.Contains(id));
            if (left)
                line += $"; taban var; yeni sapma tabana alınmaz (taban dışı: {Breakdown(license.Details, id => !captured.Contains(id))})";
            output.WriteLine(line);
            rows.AddRange(add);
        }

        if (!apply)
        {
            output.WriteLine("Kuru çalıştırma: hiçbir şey yazılmadı. Yazmak için --apply ekle.");
            return 0;
        }
        if (rows.Count == 0)
        {
            output.WriteLine("Yazılacak satır yok.");
            return 0;
        }

        db.PersonBalanceBaselines.AddRange(rows);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Anahtar (LicenseId, WpfCustomerId): eşzamanlı bir yakalama aynı satırı yazdıysa birincil anahtar ihlali.
            // SaveChanges tek işlemdir; bu koşunun hiçbir satırı yazılmadı.
            db.ChangeTracker.Clear();
            error.WriteLine("Taban bu arada başka bir koşuyla yazıldı; hiçbir şey yazılmadı. Komutu yeniden çalıştır.");
            return 1;
        }
        output.WriteLine($"UYGULANDI: {rows.Count} taban satırı yazıldı (işaretler dahil).");
        return 0;
    }

    private static async Task<int> MarkReviewedAsync(
        LicenseDbContext db, Guid licenseId, bool apply, DateTimeOffset now, TextWriter output, TextWriter error,
        CancellationToken ct)
    {
        if (!await db.PersonBalanceBaselines.AnyAsync(
                b => b.LicenseId == licenseId && b.WpfCustomerId == PersonBalanceVerifier.LicenseMarker, ct))
        {
            error.WriteLine($"Lisansın taban işareti yok: {licenseId}. Önce --capture-baseline --apply. Hiçbir şey yazılmadı.");
            return 1;
        }

        var pending = await db.PersonBalanceBaselines
            .Where(b => b.LicenseId == licenseId && b.ReviewedAt == null)
            .ToListAsync(ct);
        output.WriteLine($"İnceleme — lisans {licenseId}: incelenmemiş {pending.Count} taban satırı (işaret dahil)");
        if (!apply)
        {
            output.WriteLine("Kuru çalıştırma: hiçbir şey yazılmadı. Yazmak için --apply ekle.");
            return 0;
        }

        foreach (var row in pending) row.ReviewedAt = now;
        await db.SaveChangesAsync(ct);
        output.WriteLine($"UYGULANDI: {pending.Count} satır incelendi olarak işaretlendi.");
        return 0;
    }

    private static string Breakdown(PersonBalanceVerifier.Details d, Func<Guid, bool> include) =>
        $"eksi {d.NegativeOutside.Count(include)}, D1 {d.LedgerMismatchOutside.Count(include)}, "
        + $"kopya {d.CopyOutside.Count(include)}";

    private static void WriteLicense(TextWriter o, PersonBalanceVerifier.LicenseReport r, bool details)
    {
        var baseline = r.HasMarker ? "alındı" : r.HasBaseline ? "yeni lisans (boş taban)" : "YOK";
        o.WriteLine($"Lisans {r.LicenseId} — taban: {baseline}");
        o.WriteLine($"  1. Davranışı değişecek kişi: {r.SpreadPersons}, havuz toplamı {Amount(r.SpreadPoolTotal)}");
        o.WriteLine($"  2. Eksi satır: tabanda {r.NegativeRows.InBaseline}, taban dışı {r.NegativeRows.Outside}, toplam {Amount(r.NegativeRows.Total)}");
        o.WriteLine($"  3. D1 bozuk satır: tabanda {r.LedgerMismatches.InBaseline}, taban dışı {r.LedgerMismatches.Outside}, fark toplamı {Amount(r.LedgerMismatches.Total)}");
        o.WriteLine($"  4. Silinmiş üyede bakiye: {r.PurgedWithBalance} ({Amount(r.PurgedBalanceTotal)}); grubunda canlı asıl üye kalmamış: {r.StrandedPurged} ({Amount(r.StrandedPurgedTotal)})");
        o.WriteLine($"  5. Kırpma farkı: {r.TrimMismatches}; sekme/satır sonu içeren numara: {r.ControlCharGroups}");
        o.WriteLine($"  6. Havuzu < 0 kişi: {r.NegativePersons} (tabanla açıklanan {r.NegativePersonsExplained})");
        o.WriteLine($"  7. D4 {r.D4}, D5 {r.D5}, D6 {r.D6}, yetim bacak {r.OrphanLegs}");
        o.WriteLine($"  8. Kopya satır: hareketli {r.CopyRowsWithTransactions} ({r.CopyTransactionCount} hareket), bakiyeli {r.CopyRowsWithBalance}; tabanda {r.CopyRows.InBaseline}, taban dışı {r.CopyRows.Outside}");
        o.WriteLine($"  9. Aktif PC sürümleri (son {PersonBalanceVerifier.ActivePcWindow.Days} gün): "
            + (r.ActivePcVersions.Count == 0
                ? "yok"
                : string.Join(", ", r.ActivePcVersions.OrderBy(v => v.Key, StringComparer.Ordinal).Select(v => $"{v.Key} × {v.Value}"))));
        if (r.Alerts.Count > 0) o.WriteLine($"  YENİ SAPMA: {string.Join("; ", r.Alerts)}");
        if (!details) return;
        WriteIds(o, "eksi (taban dışı)", r.Details.NegativeOutside);
        WriteIds(o, "eksi (tabanda)", r.Details.NegativeInBaseline);
        WriteIds(o, "D1 (taban dışı)", r.Details.LedgerMismatchOutside);
        WriteIds(o, "D1 (tabanda)", r.Details.LedgerMismatchInBaseline);
        WriteIds(o, "kopya (taban dışı)", r.Details.CopyOutside);
        WriteIds(o, "grubu dağılmış silinmiş", r.Details.StrandedPurged);
        WriteIds(o, "kural bozan grup (çapa hareketi)", r.Details.RuleBreakingGroups);
        WriteIds(o, "kural bozan satır", r.Details.RuleBreakingRows);
        WriteIds(o, "yetim bacak (hareket)", r.Details.OrphanLegs);
        WriteIds(o, "yetim bacağın satırı", r.Details.OrphanLegRows);
        WriteIds(o, "daha da eksiye inen", r.Details.WorsenedBaselineRows);
        WriteIds(o, "D1 farkı değişen", r.Details.ChangedMismatchRows);
    }

    private static void WriteIds(TextWriter o, string label, IReadOnlyList<Guid> ids)
    {
        if (ids.Count == 0) return;
        o.WriteLine($"    {label}: " + string.Join(", ", ids.Select(id => id.ToString()).Order(StringComparer.Ordinal)));
    }

    private static string Amount(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    private static string Time(DateTimeOffset at) => at.ToString("u", CultureInfo.InvariantCulture);

    // ── Ayrıştırma ────────────────────────────────────────────────────────────

    /// <summary>args[0] komut adı, args[1] alt komut (yalnız <c>verify</c>; S2 <c>enable</c>/<c>disable</c>
    /// ekler). Tanınmayan argüman, ikinci kez verilen seçenek, değersiz ya da GUID olmayan <c>--license</c>, rapora
    /// <c>--apply</c>, birlikte <c>--capture-baseline</c> ve <c>--mark-reviewed</c>, lisanssız <c>--mark-reviewed</c>
    /// → false. <c>public</c>: geçerli argümanlı yol yapılandırmaya ve veritabanına gider; kabul edilen biçimler yalnız
    /// saf fonksiyon olarak sınanabilir.</summary>
    public static bool TryParseArgs(string[] args, [NotNullWhen(true)] out VerifyArgs? parsed)
    {
        parsed = null;
        if (args.Length < 2 || args[1] != "verify") return false;

        Guid? license = null;
        var flags = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 2; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg == "--license")
            {
                if (license is not null || i + 1 >= args.Length || !Guid.TryParse(args[i + 1], out var id)) return false;
                license = id;
                i++;
            }
            else if (arg is "--details" or "--capture-baseline" or "--mark-reviewed" or "--apply")
            {
                if (!flags.Add(arg)) return false;
            }
            else
            {
                return false;
            }
        }

        var capture = flags.Contains("--capture-baseline");
        var mark = flags.Contains("--mark-reviewed");
        var apply = flags.Contains("--apply");
        if (capture && mark) return false;
        if (apply && !capture && !mark) return false;
        if (mark && license is null) return false;

        parsed = new VerifyArgs(license, flags.Contains("--details"), capture, mark, apply);
        return true;
    }
}
