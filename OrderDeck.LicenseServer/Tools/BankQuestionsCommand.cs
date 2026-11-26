using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;

namespace OrderDeck.LicenseServer.Tools;

/// <summary>
/// Konteyner içinde elle koşulur:
///   docker exec orderdeck-license dotnet OrderDeck.LicenseServer.dll bank-questions &lt;enable|disable&gt; --license &lt;guid&gt; [--apply]
/// Havale sorularının lisans anahtarı (spec 2026-10-10 §2.7, §14; S2a planı Karar 8). Lisansın üç banka alanını
/// (<c>BankQuestionsEnabled</c>, <c>BankQuestionsSince</c>, <c>BankAutoApproveEnabled</c>) yalnız CLI yazar; bu iki alt
/// komut otomatik onay bayrağına dokunmaz (onun komutu S6'da).
/// <list type="bullet">
///   <item><c>enable --license &lt;guid&gt; [--apply]</c> — iki koşul: lisansın satış senkronu kipi On ve karar katmanı bu
///   sürümde (<see cref="DecisionLayerReady"/>). Kuru koşu koşulları basar, koşul tutmasa da 0 döner; <c>--apply</c>
///   koşulları bu koşuda yeniden değerlendirir, biri tutmazsa hiçbir şey yazmadan 1. Yazım tek koşullu UPDATE'tir (bayrak
///   kapalı ve kip On, ikisi de WHERE'de): bayrak açılır, <c>BankQuestionsSince</c> yalnız boşsa şimdi olur (COALESCE:
///   kapatıp açmak ilk açılış anını sıfırlamaz, §3). Zaten açıksa koşullar değerlendirilmez, 0.</item>
///   <item><c>disable --license &lt;guid&gt; [--apply]</c> — kill switch: koşul yok, yapılandırma okunmaz, veri işlemi
///   yok; yalnız bayrak kapanır, <c>BankQuestionsSince</c> korunur. Verilmiş kararları düzelten uçlar açık kalır (§2.7).
///   Zaten kapalıysa 0.</item>
/// </list>
/// Çıkış: 0 tamam, kuru koşu ya da yapılacak bir şey yok; 1 koşul tutmadı ya da eşzamanlı değişiklik — hiçbir şey
/// yazılmadı; 2 kullanım, bağlantı dizesi yok ya da bilinmeyen lisans. Öbür veritabanı hataları yutulmaz. Argümanlar katı
/// (<see cref="PersonBalanceCommand"/> deseni). Bağlantı dizesi web host'unkiyle aynı kaynaktan
/// (<c>ConnectionStrings:LicenseDb</c>); başka ayar okunmaz. Çıktıda kişisel veri, makine adı, anahtar YOK — yalnız
/// lisans Id'si, kip ve zaman.
/// </summary>
public static class BankQuestionsCommand
{
    /// <summary>Karar katmanı (soru listeleri, karar uçları, tarama) bu sürümde mi. S2a'da kapalı: şema var, okuyan yok.
    /// Erken bir <c>enable --apply</c> kapatıp açmakla sıfırlanmayan <c>BankQuestionsSince</c>'i yazar, S3a çıkınca o
    /// andan sonraki havaleler sorulurdu. Pilottan önceki son sunucu PR'ı (S4) açar.</summary>
    public const bool DecisionLayerReady = false;

    private const string Usage =
        "Kullanım:\n"
        + "  bank-questions enable --license <guid> [--apply]\n"
        + "  bank-questions disable --license <guid> [--apply]";

    /// <summary>Ayrıştırılmış <c>enable</c>/<c>disable</c>.</summary>
    /// <param name="Enable">true = <c>enable</c>, false = <c>disable</c>.</param>
    /// <param name="Apply">false = kuru koşu.</param>
    public sealed record ToggleArgs(bool Enable, Guid License, bool Apply);

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
        return parsed.Enable
            ? await RunEnableAsync(db, parsed, DecisionLayerReady, Console.Out, Console.Error, CancellationToken.None)
            : await RunDisableAsync(db, parsed, Console.Out, Console.Error, CancellationToken.None);
    }

    /// <summary><c>enable</c>'ın gövdesi: argümanlar ayrıştırılmış, bağlam kurulmuş. Testler buradan girer;
    /// <paramref name="decisionLayerReady"/> CLI'da <see cref="DecisionLayerReady"/>'dir. Zaten açıksa koşulları
    /// değerlendirmez, yazmaz, 0. Kuru koşu iki koşulu basar ve koşul tutmasa da 0 döner; <c>--apply</c>'da biri tutmazsa
    /// 1. Yazım tek koşullu UPDATE'tir: araya başka bir <c>enable</c>, kipi kapatan <c>sales-sync disable</c> ya da
    /// lisansın silinmesi girerse 0 satır → 1, hiçbir şey yazılmadı.</summary>
    public static async Task<int> RunEnableAsync(
        LicenseDbContext db, ToggleArgs args, bool decisionLayerReady, TextWriter output, TextWriter error,
        CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var license = await db.Licenses.AsNoTracking()
            .Where(l => l.Id == args.License)
            .Select(l => new { l.SalesSyncMode, l.BankQuestionsEnabled, l.BankQuestionsSince, l.BankAutoApproveEnabled })
            .SingleOrDefaultAsync(ct);
        if (license is null)
        {
            error.WriteLine($"Lisans bulunamadı: {args.License}. Hiçbir şey yazılmadı.");
            return 2;
        }

        WriteHeader(output, args, now);
        if (license.BankQuestionsEnabled)
        {
            output.WriteLine(
                $"Havale soruları zaten açık (BankQuestionsSince={Time(license.BankQuestionsSince)}); hiçbir şey yazılmadı.");
            return 0;
        }

        var modeOn = license.SalesSyncMode == SalesSyncMode.On;
        output.WriteLine($"Koşul 1 — satış senkronu modu On: {Verdict(modeOn)} ({license.SalesSyncMode})");
        output.WriteLine($"Koşul 2 — karar katmanı bu sürümde: {Verdict(decisionLayerReady)}");
        output.WriteLine("BankQuestionsSince: "
            + (license.BankQuestionsSince is { } since ? $"{Time(since)}, korunur" : "boş, açılışta yazılacak")
            + $"; otomatik onay bayrağı: {(license.BankAutoApproveEnabled ? "açık" : "kapalı")}");
        var failing = new List<int>(2);
        if (!modeOn) failing.Add(1);
        if (!decisionLayerReady) failing.Add(2);
        output.WriteLine(failing.Count == 0
            ? "Sonuç: bütün koşullar tutuyor"
            : $"Sonuç: koşul {string.Join(", ", failing)} tutmuyor");

        if (!args.Apply)
        {
            output.WriteLine("Kuru çalıştırma: hiçbir şey yazılmadı. Açmak için --apply ekle.");
            return 0;
        }
        if (failing.Count > 0)
        {
            error.WriteLine($"Havale soruları açılmadı: koşul {string.Join(", ", failing)} tutmuyor; hiçbir şey yazılmadı.");
            return 1;
        }

        // Tek koşullu deyim (atomik): bayrak hâlâ kapalı ve kip hâlâ On ise yazar. Since yalnız boşsa şimdi olur.
        var updated = await db.Licenses
            .Where(l => l.Id == args.License && !l.BankQuestionsEnabled && l.SalesSyncMode == SalesSyncMode.On)
            .ExecuteUpdateAsync(s => s
                .SetProperty(l => l.BankQuestionsEnabled, true)
                .SetProperty(l => l.BankQuestionsSince, l => l.BankQuestionsSince ?? (DateTimeOffset?)now), ct);
        if (updated == 0)
        {
            error.WriteLine("Lisans bu arada değişti (başka bir enable, kapanan satış senkronu ya da silinen lisans); "
                + "hiçbir şey yazılmadı, yeniden çalıştır.");
            return 1;
        }

        // Kayıtlı değer yeniden okunur: COALESCE önceki açılışın anını korumuş olabilir.
        var stored = await db.Licenses.AsNoTracking()
            .Where(l => l.Id == args.License)
            .Select(l => l.BankQuestionsSince)
            .SingleAsync(ct);
        output.WriteLine($"UYGULANDI: havale soruları açık, BankQuestionsSince={Time(stored)}");
        return 0;
    }

    /// <summary><c>disable</c>'ın gövdesi — kill switch (§2.7): koşul yok, veri işlemi yok, yalnız
    /// <c>BankQuestionsEnabled = 0</c>; <c>BankQuestionsSince</c> ve otomatik onay bayrağı değişmez. Zaten kapalıysa
    /// yazmaz, 0. Yazım tek koşullu UPDATE'tir (yalnız bayrak hâlâ açıksa): eşzamanlı bir <c>disable</c> önce kapattıysa
    /// 0 satır → 1.</summary>
    public static async Task<int> RunDisableAsync(
        LicenseDbContext db, ToggleArgs args, TextWriter output, TextWriter error, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var license = await db.Licenses.AsNoTracking()
            .Where(l => l.Id == args.License)
            .Select(l => new { l.BankQuestionsEnabled, l.BankQuestionsSince })
            .SingleOrDefaultAsync(ct);
        if (license is null)
        {
            error.WriteLine($"Lisans bulunamadı: {args.License}. Hiçbir şey yazılmadı.");
            return 2;
        }

        WriteHeader(output, args, now);
        if (!license.BankQuestionsEnabled)
        {
            output.WriteLine("Havale soruları zaten kapalı; hiçbir şey yazılmadı.");
            return 0;
        }
        if (!args.Apply)
        {
            output.WriteLine($"Kuru çalıştırma: havale soruları kapatılacak (BankQuestionsSince={Time(license.BankQuestionsSince)} "
                + "korunur); veri işlemi yok. Kapatmak için --apply ekle.");
            return 0;
        }

        var updated = await db.Licenses
            .Where(l => l.Id == args.License && l.BankQuestionsEnabled)
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.BankQuestionsEnabled, false), ct);
        if (updated == 0)
        {
            error.WriteLine("Havale soruları bu arada değişti (başka bir disable?); hiçbir şey yazılmadı, yeniden çalıştır.");
            return 1;
        }
        output.WriteLine("UYGULANDI: havale soruları kapalı; verilmiş kararların geri alınması açık kalır, "
            + "veri işlemi yapılmadı.");
        return 0;
    }

    private static void WriteHeader(TextWriter o, ToggleArgs args, DateTimeOffset now)
    {
        o.WriteLine($"{(args.Apply ? "UYGULAMA" : "KURU ÇALIŞTIRMA")} — bank-questions "
            + $"{(args.Enable ? "enable" : "disable")} — {Time(now)}");
        o.WriteLine($"Lisans: {args.License}");
    }

    private static string Verdict(bool passes) => passes ? "TUTUYOR" : "TUTMUYOR";

    private static string Time(DateTimeOffset? at) => at?.ToString("u", CultureInfo.InvariantCulture) ?? "boş";

    // ── Ayrıştırma ────────────────────────────────────────────────────────────

    /// <summary>args[0] komut adı, args[1] alt komut (<c>enable</c> ya da <c>disable</c>): <c>--license &lt;guid&gt;</c>
    /// tam bir kez ve zorunlu, <c>--apply</c> en çok bir kez. Başka alt komut, başka her argüman, ikinci kez verilen
    /// seçenek, değersiz ya da GUID olmayan <c>--license</c> → false (<see cref="PersonBalanceCommand.TryParseToggleArgs"/>
    /// gövdesinin kopyası). <c>public</c>: kabul edilen biçimler saf fonksiyon olarak sınanır.</summary>
    public static bool TryParseArgs(string[] args, [NotNullWhen(true)] out ToggleArgs? parsed)
    {
        parsed = null;
        if (args.Length < 2 || args[1] is not ("enable" or "disable")) return false;

        Guid? license = null;
        var apply = false;
        for (var i = 2; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg == "--license")
            {
                if (license is not null || i + 1 >= args.Length || !Guid.TryParse(args[i + 1], out var id)) return false;
                license = id;
                i++;
            }
            else if (arg == "--apply")
            {
                if (apply) return false;
                apply = true;
            }
            else
            {
                return false;
            }
        }

        if (license is not { } licenseId) return false;
        parsed = new ToggleArgs(args[1] == "enable", licenseId, apply);
        return true;
    }
}
