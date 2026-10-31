using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.CustomerSync;
using OrderDeck.LicenseServer.Services.Sync;

namespace OrderDeck.LicenseServer.Tools;

/// <summary>
/// Konteyner içinde elle koşulur:
///   docker exec orderdeck-license dotnet OrderDeck.LicenseServer.dll person-balance &lt;verify|enable|disable&gt; [seçenekler]
/// Ortak bakiye denetimi ve havuz kipinin anahtarı (spec 2026-10-11 §7.2, §7.3; çekirdekler
/// <see cref="PersonBalanceVerifier"/> ve <see cref="PersonBalanceEnableGate"/>):
/// <list type="bullet">
///   <item><c>verify [--license &lt;guid&gt;] [--details]</c> — salt okunur rapor (§7.3 maddeleri 1–10). Lisans
///   verilmezse bütün lisanslar, verisizler dahil (plan fark 16). <c>--details</c> şu satırların
///   <c>WpfCustomerId</c>'lerini basar: eksi, D1 ve kopya (her biri tabanda ve taban dışı ayrı), grubu dağılmış
///   silinmiş üye, kural bozan grubun ve yetim bacağın satırı, daha da eksiye inen ve D1 farkı değişen taban satırı;
///   ayrıca kural bozan grupların çapa ve yetim bacakların HAREKET Id'lerini. Yalnız GUID (ad, telefon, numara yok),
///   yalnız konsola; dosyaya yazılmaz.</item>
///   <item><c>--capture-baseline [--apply]</c> — taban çizgisi (§7.1 adım 2): tabanı olmayan lisansın taban dışı eksi,
///   D1 ve kopya satırları o anki Balance ve LedgerSum'la, lisans başına bir işaret satırıyla. Var olan taban satırına
///   dokunmaz; tabanı olan lisansın taban dışı satırı yakalamadan sonra doğmuş yeni sapmadır, tabana alınmaz (plan
///   fark 20, <see cref="PersonBalanceVerifier.BaselineRowsToCapture"/>).</item>
///   <item><c>--mark-reviewed --license &lt;guid&gt; [--details] [--apply]</c> — sahip incelemesi (§7.1 adım 4):
///   lisansın incelenmemiş taban satırlarının <c>ReviewedAt</c>'i. <c>--details</c> işaretlenecek satırların
///   <c>WpfCustomerId</c>'lerini de basar (işaret hariç).</item>
///   <item><c>enable --license &lt;guid&gt; [--apply]</c> — havuz kipini açar (§7.2, §7.1 adım 7): kuru koşu kapının dört
///   koşulunu (<see cref="PersonBalanceEnableGate"/>) sayılarla basar, koşul tutmasa da 0 döner; <c>--apply</c>
///   koşulları yeniden değerlendirir (önceki kuru koşuya güvenmez), biri tutmazsa hiçbir şey yazmadan 1; tutarsa
///   <c>PersonBalancePoolSince</c> = şimdi, tek koşullu UPDATE'le (yalnız bayrak hâlâ boşsa).</item>
///   <item><c>disable --license &lt;guid&gt; [--apply]</c> — kill switch (§7.2, §7.4): <c>PersonBalancePoolSince</c>
///   boşalır, lisans satır kipine iner. Kapı yok, yapılandırma okunmaz, veri işlemi yok; çok bacaklı geçmiş S1
///   okuyucularıyla okunur ve geri alınır.</item>
/// </list>
/// Yazan yollar <c>--apply</c> olmadan yazacağını basar. <c>verify</c>'ın bayrakları yalnız
/// <c>PersonBalanceBaselines</c>'a, <c>enable</c>/<c>disable</c> yalnız <c>Licenses.PersonBalancePoolSince</c>'a yazar.
/// Rapora <c>--apply</c> vermek kullanım hatasıdır. Çıkış: 0 tamam, kuru koşu ya da yapılacak bir şey yok (zaten
/// açıkken <c>enable</c>, zaten kapalıyken <c>disable</c>; S2 planı fark 18e); 1 eşzamanlı yazım ya da silme, işaretsiz
/// lisansta inceleme, ya da <c>enable --apply</c>'da tutmayan koşul — hiçbir şey yazılmadı; 2 kullanım, yapılandırma
/// ya da bilinmeyen lisans. Öbür veritabanı hataları yutulmaz: işlenmemiş istisna olarak yükselir (yığın izi, 0/1/2
/// dışı çıkış kodu). Argümanlar katı (<see cref="SalesSync"/> deseni). Bağlantı dizesi web host'unkiyle aynı kaynaktan
/// (<c>ConnectionStrings:LicenseDb</c>); <c>enable</c> ayrıca <c>PersonBalance:*</c> ve <c>SalesSync:*</c> bölümlerini
/// okur. Çıktıda ad, telefon, makine adı, parmak izi YOK — yalnız sayılar, tutarlar, sürümler, Id'ler.
/// </summary>
public static class PersonBalanceCommand
{
    private const string Usage =
        "Kullanım:\n"
        + "  person-balance verify [--license <guid>] [--details]\n"
        + "  person-balance verify [--license <guid>] [--details] --capture-baseline [--apply]\n"
        + "  person-balance verify --license <guid> [--details] --mark-reviewed [--apply]\n"
        + "  person-balance enable --license <guid> [--apply]\n"
        + "  person-balance disable --license <guid> [--apply]";

    /// <summary>Ayrıştırılmış <c>verify</c>.</summary>
    public sealed record VerifyArgs(Guid? License, bool Details, bool CaptureBaseline, bool MarkReviewed, bool Apply);

    /// <summary>Ayrıştırılmış <c>enable</c>/<c>disable</c>.</summary>
    /// <param name="Enable">true = <c>enable</c>, false = <c>disable</c>.</param>
    /// <param name="Apply">false = kuru koşu.</param>
    public sealed record ToggleArgs(bool Enable, Guid License, bool Apply);

    public static async Task<int> RunAsync(string[] args)
    {
        // enable/disable kendi ayrıştırıcısında; verify'ınki onları reddetmeye devam eder (S2 planı fark 13).
        if (!TryParseAnyArgs(args, out var verify, out var toggle))
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
        var output = Console.Out;
        var error = Console.Error;
        var ct = CancellationToken.None;
        if (toggle is null) return await RunAsync(db, verify!, output, error, ct);
        // disable kill switch'tir: yapılandırma okumaz, kullanmadığı bir ayarın bozukluğuyla kilitlenmez.
        if (!toggle.Enable) return await RunDisableAsync(db, toggle, output, error, ct);

        // Web host'un ValidateOnStart'ı CLI'da koşmaz; pencerenin denetimi RunEnableAsync'in başında (gövdede).
        var personBalance = config.GetSection(PersonBalanceOptions.Section).Get<PersonBalanceOptions>()
            ?? new PersonBalanceOptions();
        var salesSync = config.GetSection(SalesSyncOptions.Section).Get<SalesSyncOptions>() ?? new SalesSyncOptions();
        return await RunEnableAsync(db, toggle, personBalance, salesSync, output, error, ct);
    }

    /// <summary>Gövde: argümanlar ayrıştırılmış, bağlam kurulmuş. Testler buradan girer. Ayrıştırıcının reddedeceği
    /// bir kayıt (<see cref="IsConsistent"/>) programlama hatasıdır: veritabanına gitmeden
    /// <see cref="ArgumentException"/>.</summary>
    public static async Task<int> RunAsync(
        LicenseDbContext db, VerifyArgs args, TextWriter output, TextWriter error, CancellationToken ct)
    {
        if (!IsConsistent(args))
            throw new ArgumentException($"Ayrıştırıcının reddedeceği argümanlar: {args}. TryParseArgs çıktısını geçir.", nameof(args));

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
        if (args.MarkReviewed)
            return await MarkReviewedAsync(db, args.License!.Value, args.Details, args.Apply, now, output, error, ct);
        return 0;
    }

    private static async Task<int> CaptureAsync(
        LicenseDbContext db, PersonBalanceVerifier.Snapshot snapshot, PersonBalanceVerifier.Report report, bool apply,
        DateTimeOffset now, TextWriter output, TextWriter error, CancellationToken ct)
    {
        var rows = new List<PersonBalanceBaseline>();
        var data = PersonBalanceVerifier.ByLicense.Of(snapshot);
        foreach (var license in report.Licenses)
        {
            var add = PersonBalanceVerifier.BaselineRowsToCapture(data, license, now);
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
        catch (DbUpdateException ex) when (IsDuplicateBaseline(ex))
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

    /// <summary>Taban tablosunun birincil anahtar ihlali (2627/2601 + anahtarın adı): yalnız eşzamanlı yakalama.
    /// Öbür hatalar (arada silinen lisansın FK'sı, zaman aşımı, taşma, öbür tekil yarışlar) yükselir; teşhisleri
    /// "yeniden çalıştır" iletisine gömülmesin (kalıp <see cref="PersonBalance.IsDuplicateBalanceRow"/>).</summary>
    private static bool IsDuplicateBaseline(DbUpdateException ex) =>
        ex.InnerException is SqlException sql
        && sql.Number is 2601 or 2627
        && sql.Message.Contains(BaselineKey, StringComparison.Ordinal);

    /// <summary>Göçün adı (<c>PersonBalanceS1</c>): <c>PK_PersonBalanceBaselines</c> (LicenseId, WpfCustomerId).</summary>
    private const string BaselineKey = "PK_PersonBalanceBaselines";

    private static async Task<int> MarkReviewedAsync(
        LicenseDbContext db, Guid licenseId, bool details, bool apply, DateTimeOffset now, TextWriter output,
        TextWriter error, CancellationToken ct)
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
        // §7.1 adım 4 "her satır için karar": sahip işaretleyeceği satırları görür (yalnız GUID).
        if (details)
            WriteIds(output, "incelenecek taban satırı (işaret hariç)", pending
                .Where(b => b.WpfCustomerId != PersonBalanceVerifier.LicenseMarker)
                .Select(b => b.WpfCustomerId).ToList());
        if (!apply)
        {
            output.WriteLine("Kuru çalıştırma: hiçbir şey yazılmadı. Yazmak için --apply ekle.");
            return 0;
        }

        foreach (var row in pending) row.ReviewedAt = now;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Okunan bir satır yazımdan önce silindi (lisans silindi, taban cascade ile gitti). SaveChanges tek işlemdir.
            db.ChangeTracker.Clear();
            error.WriteLine("Taban satırları bu arada silindi ya da değişti; hiçbir şey yazılmadı. Komutu yeniden çalıştır.");
            return 1;
        }
        output.WriteLine($"UYGULANDI: {pending.Count} satır incelendi olarak işaretlendi.");
        return 0;
    }

    // ── enable / disable (§7.2) ──────────────────────────────────────────────

    /// <summary><c>enable</c>'ın gövdesi: argümanlar ayrıştırılmış, bağlam ve iki ayar bölümü verilmiş. Testler buradan
    /// girer. Pencere (<c>PersonBalance:ActivePcWindowDays</c>) 0 ya da eksiyse lisans okunmadan 2: kapı
    /// (<see cref="PersonBalanceEnableGate"/>) 1. koşulu bu durumda zaten kapalı-hata ile tutmaz sayar; bu denetim ek
    /// güvencedir ve operatöre "koşul 1 tutmuyor" yerine asıl sebebi (yapılandırma hatası) gösterir. Zaten açıksa
    /// kapıyı değerlendirmez, yazmaz, 0 (S2 planı fark 18e). Kuru koşu dört koşulu basar ve koşul tutmasa da 0 döner;
    /// <c>--apply</c> koşulları bu koşuda yeniden değerlendirir, biri tutmazsa 1. Yazım tek koşullu UPDATE'tir (yalnız
    /// bayrak hâlâ boşsa): eşzamanlı bir <c>enable</c> önce yazdıysa 0 satır → 1, hiçbir şey yazılmadı.</summary>
    public static async Task<int> RunEnableAsync(
        LicenseDbContext db, ToggleArgs args, PersonBalanceOptions personBalance, SalesSyncOptions salesSync,
        TextWriter output, TextWriter error, CancellationToken ct)
    {
        // Web host'ta ValidateOnStart bunu açılışta yakalar; CLI'da ve doğrudan çağıranda savunma burada.
        if (personBalance.ActivePcWindowDays <= 0)
        {
            error.WriteLine("PersonBalance:ActivePcWindowDays 0'dan büyük olmalı.");
            return 2;
        }

        var now = DateTimeOffset.UtcNow;
        var license = await db.Licenses.AsNoTracking()
            .Where(l => l.Id == args.License)
            .Select(l => new { l.PersonBalancePoolSince })
            .SingleOrDefaultAsync(ct);
        if (license is null)
        {
            error.WriteLine($"Lisans bulunamadı: {args.License}. Hiçbir şey yazılmadı.");
            return 2;
        }

        output.WriteLine($"{(args.Apply ? "UYGULAMA" : "KURU ÇALIŞTIRMA")} — person-balance enable — {Time(now)}");
        output.WriteLine($"Lisans: {args.License}");
        if (license.PersonBalancePoolSince is { } since)
        {
            output.WriteLine($"Havuz kipi zaten açık (PersonBalancePoolSince={Time(since)}); hiçbir şey yazılmadı.");
            return 0;
        }

        var gate = await PersonBalanceEnableGate.EvaluateAsync(db, args.License, personBalance, salesSync, now, ct);
        WriteGate(output, gate);

        if (!args.Apply)
        {
            output.WriteLine("Kuru çalıştırma: hiçbir şey yazılmadı. Açmak için --apply ekle.");
            return 0;
        }
        if (!gate.Passes)
        {
            error.WriteLine($"Havuz kipi açılmadı: koşul {string.Join(", ", gate.Failing)} tutmuyor; hiçbir şey yazılmadı.");
            return 1;
        }

        // Tek koşullu deyim (atomik): yalnız bayrak hâlâ boşsa yazar. Eşzamanlı bir enable önce yazdıysa 0 satır.
        var updated = await db.Licenses
            .Where(l => l.Id == args.License && l.PersonBalancePoolSince == null)
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.PersonBalancePoolSince, now), ct);
        if (updated == 0)
        {
            error.WriteLine("Havuz kipi bu arada değişti (başka bir enable?); hiçbir şey yazılmadı, yeniden çalıştır.");
            return 1;
        }
        output.WriteLine($"UYGULANDI: havuz kipi açık, PersonBalancePoolSince={Time(now)}");
        return 0;
    }

    /// <summary><c>disable</c>'ın gövdesi — kill switch (§7.2, §7.4): kapı yok, veri işlemi yok, yalnız
    /// <c>PersonBalancePoolSince = NULL</c>. Zaten kapalıysa yazmaz, 0 (S2 planı fark 18e). Yazım tek koşullu UPDATE'tir
    /// (yalnız bayrak hâlâ doluysa): eşzamanlı bir <c>disable</c> önce boşalttıysa 0 satır → 1.</summary>
    public static async Task<int> RunDisableAsync(
        LicenseDbContext db, ToggleArgs args, TextWriter output, TextWriter error, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var license = await db.Licenses.AsNoTracking()
            .Where(l => l.Id == args.License)
            .Select(l => new { l.PersonBalancePoolSince })
            .SingleOrDefaultAsync(ct);
        if (license is null)
        {
            error.WriteLine($"Lisans bulunamadı: {args.License}. Hiçbir şey yazılmadı.");
            return 2;
        }

        output.WriteLine($"{(args.Apply ? "UYGULAMA" : "KURU ÇALIŞTIRMA")} — person-balance disable — {Time(now)}");
        output.WriteLine($"Lisans: {args.License}");
        if (license.PersonBalancePoolSince is not { } since)
        {
            output.WriteLine("Havuz kipi zaten kapalı; hiçbir şey yazılmadı.");
            return 0;
        }
        if (!args.Apply)
        {
            output.WriteLine($"Kuru çalıştırma: havuz kipi kapatılacak (PersonBalancePoolSince={Time(since)} → boş); "
                + "veri işlemi yok. Kapatmak için --apply ekle.");
            return 0;
        }

        var updated = await db.Licenses
            .Where(l => l.Id == args.License && l.PersonBalancePoolSince != null)
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.PersonBalancePoolSince, (DateTimeOffset?)null), ct);
        if (updated == 0)
        {
            error.WriteLine("Havuz kipi bu arada değişti (başka bir disable?); hiçbir şey yazılmadı, yeniden çalıştır.");
            return 1;
        }
        output.WriteLine("UYGULANDI: havuz kipi kapalı (satır kipi); çok bacaklı geçmiş okunur ve geri alınır, "
            + "veri işlemi yapılmadı.");
        return 0;
    }

    /// <summary>Kapının dört koşulu (§7.2) ve sonuç satırı. Yalnız sayı, sürüm ve etkinleştirme Id'si; makine adı,
    /// parmak izi, müşteri yok (kapı onları okumaz).</summary>
    private static void WriteGate(TextWriter o, PersonBalanceEnableGate.Result gate)
    {
        var clients = gate.Clients;
        o.WriteLine($"Koşul 1 — istemci sürümü (son {gate.WindowDays} gün): {Verdict(clients.Passes)}");
        // Ham değer (kırpılmış) 4. koşulun satırıyla aynı; yazılı ama ayrıştırılamıyorsa (R06 "bozuk") Clients'ın
        // alt sınırı null'dır, operatör yazdığı değeri "tanımsız" görmesin.
        var limit = gate.Superset.PersonBalanceMin;
        o.WriteLine($"  PersonBalance:MinClientVersion: {limit ?? "tanımsız"}"
            + (limit is not null && clients.MinClientVersion is null ? " (ayrıştırılamıyor)" : ""));
        o.WriteLine($"  görülen etkin etkinleştirme: {clients.RecentActivations}; alt sınırın altında ya da sürümsüz: "
            + (clients.MinClientVersion is null
                ? "karşılaştırılamadı"
                : clients.Old.Count.ToString(CultureInfo.InvariantCulture)));
        foreach (var a in clients.Old.OrderBy(a => a.Id.ToString(), StringComparer.Ordinal))
            o.WriteLine($"    {a.Id} (sürüm={a.AppVersion ?? "bilinmiyor"})");

        var v = gate.Verify;
        o.WriteLine($"Koşul 2 — verify: D4={v.D4} D5={v.D5} D6={v.D6} yetim={v.OrphanLegs} kırpma={v.TrimMismatches} "
            + $"kontrol karakteri={v.ControlCharGroups}: {Verdict(v.Passes)}");

        var b = gate.Baseline;
        o.WriteLine($"Koşul 3 — taban: {(b.HasBaseline ? "var" : "YOK — önce verify --capture-baseline")}; "
            + $"incelenmemiş {b.Unreviewed}; taban dışı eksi {b.NegativeOutside}; daha da eksiye inen {b.Worsened}; "
            + $"taban dışı D1 {b.LedgerMismatchOutside}; farkı değişen D1 {b.ChangedMismatch}; "
            + $"taban dışı kopya {b.CopyOutside}: {Verdict(b.Passes)}");

        var s = gate.Superset;
        o.WriteLine($"Koşul 4 — Wh ⊇ Ws: PersonBalance:MinClientVersion={s.PersonBalanceMin ?? "tanımsız"} "
            + $"SalesSync:MinClientVersion={s.SalesSyncMin ?? "tanımsız"}: {Verdict(s.Passes)}");

        o.WriteLine(gate.Passes
            ? "Sonuç: bütün koşullar tutuyor"
            : $"Sonuç: koşul {string.Join(", ", gate.Failing)} tutmuyor");
    }

    private static string Verdict(bool passes) => passes ? "TUTUYOR" : "TUTMUYOR";

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
        WriteIds(o, "kopya (tabanda)", r.Details.CopyInBaseline);
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

    /// <summary>args[0] komut adı, args[1] alt komut (yalnız <c>verify</c>; <c>enable</c>/<c>disable</c>
    /// <see cref="TryParseToggleArgs"/>'ta, bu ayrıştırıcı onları reddeder — S2 planı fark 13). Tanınmayan argüman, ikinci
    /// kez verilen seçenek, değersiz ya da GUID olmayan <c>--license</c>, rapora
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

        var candidate = new VerifyArgs(license, flags.Contains("--details"), flags.Contains("--capture-baseline"),
            flags.Contains("--mark-reviewed"), flags.Contains("--apply"));
        if (!IsConsistent(candidate)) return false;
        parsed = candidate;
        return true;
    }

    /// <summary>Bayrakların birbirine kuralları — ayrıştırıcı ve gövde aynısını kullanır: yakalama ile inceleme
    /// birlikte olmaz, <c>--apply</c> yalnız yazan bayrakla, <c>--mark-reviewed</c> lisans ister.</summary>
    private static bool IsConsistent(VerifyArgs a) =>
        !(a.CaptureBaseline && a.MarkReviewed)
        && (!a.Apply || a.CaptureBaseline || a.MarkReviewed)
        && (!a.MarkReviewed || a.License is not null);

    /// <summary>Dağıtım (S2 planı fark 13): <c>args[1]</c> <c>enable</c> ya da <c>disable</c> ise
    /// <see cref="TryParseToggleArgs"/>, değilse <see cref="TryParseArgs"/>. true'da çıktılardan tam biri dolu; false'ta
    /// ikisi de boş. Tek kullanıcısı <c>RunAsync(string[])</c>; <c>public</c>: dağıtım yapılandırma ve veritabanı
    /// olmadan saf fonksiyon olarak sınanır.</summary>
    public static bool TryParseAnyArgs(string[] args, out VerifyArgs? verify, out ToggleArgs? toggle)
    {
        verify = null;
        toggle = null;
        return args.Length > 1 && args[1] is ("enable" or "disable")
            ? TryParseToggleArgs(args, out toggle)
            : TryParseArgs(args, out verify);
    }

    /// <summary><c>enable</c>/<c>disable</c>'ın ayrıştırıcısı (args[0] komut adı, args[1] alt komut): <c>--license
    /// &lt;guid&gt;</c> tam bir kez ve zorunlu, <c>--apply</c> en çok bir kez. Başka her argüman, ikinci kez verilen
    /// seçenek, değersiz ya da GUID olmayan <c>--license</c> → false (<see cref="TryParseArgs"/>'ın katılığı).
    /// <c>public</c>: kabul edilen biçimler saf fonksiyon olarak sınanır.</summary>
    public static bool TryParseToggleArgs(string[] args, [NotNullWhen(true)] out ToggleArgs? parsed)
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
