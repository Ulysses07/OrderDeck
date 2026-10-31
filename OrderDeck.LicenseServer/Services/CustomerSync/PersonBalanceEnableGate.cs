using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Services.Sync;

namespace OrderDeck.LicenseServer.Services.CustomerSync;

/// <summary>
/// Havuz kipinin açılış kapısı: spec 2026-10-11 §7.2 "<c>enable</c> ön koşulları" 1–4. CLI
/// <c>person-balance enable</c> kuru koşuda her koşulu sayı olarak basar. <c>--apply</c> her seferinde yeniden
/// değerlendirir ve biri tutmazsa reddeder. Salt okunurdur: sorgular izlemesiz, hiçbir şey yazılmaz; makine adı,
/// parmak izi, müşteri adı ve telefon okunmaz.
/// <list type="number">
///   <item><b>İstemci sürümü.</b> Son <see cref="PersonBalanceOptions.ActivePcWindowDays"/> günde görülmüş ve devre
///   dışı bırakılmamış her etkinleştirmenin <c>AppVersion</c>'ı <c>PersonBalance:MinClientVersion</c>'ı
///   <see cref="ClientVersion.IsAtLeast"/> ile karşılar. Sürüm bildirmeyen ya da ayrıştırılamayan PC eski sayılır.
///   Alt sınır boş ya da bozuksa, ya da pencere ≤ 0 ise koşul tutmaz (kapalı-hata). Ölçüt satış senkronunun kapısıyla
///   aynıdır. Sorgu onun kopyasıdır, çünkü <see cref="SalesModeGate"/>'in sorgusu özeldir ve
///   <see cref="SalesSyncOptions"/>'a bağlıdır. Satış senkronu koduna dokunulmaz (plan fark 14); yalnız kayıtları
///   ortaktır.</item>
///   <item><b>Kural sayıları.</b> <see cref="PersonBalanceVerifier"/>'ın D4, D5, D6, yetim bacak ve kırpma farkı
///   sayıları 0'dır. Kırpma farkı, spec §7.3-5'in SQL'i değil, S1'in <c>TrimMismatches</c> + <c>ControlCharGroups</c>
///   sayısıdır (plan fark 15).</item>
///   <item><b>Taban.</b> Lisansın tabanı vardır (<c>HasBaseline</c>; yoksa önce <c>verify --capture-baseline</c>).
///   İşaret dahil bütün taban satırlarının <c>ReviewedAt</c>'i doludur. Tabana göre yeni sapma yoktur; bunlar günlük
///   uyarının (§8.1) taban kalemleridir: taban dışı eksi, daha da eksiye inen, taban dışı D1, farkı değişen D1, taban
///   dışı kopya. Farkı düzeltilip 0'a inen taban D1 satırı da "farkı değişen" sayılır ve açılışı engeller (uyarıyla
///   aynı kural, plan sahip kararı 2).</item>
///   <item><b>Wh ⊇ Ws.</b> <c>SalesSync:MinClientVersion</c> doludur ve <c>PersonBalance:MinClientVersion</c> ona
///   eşit ya da ondan büyüktür. İki metin yan yana döner; böylece 1. koşulu geçen her PC satış senkronunu da
///   taşır.</item>
/// </list>
/// Lisans yoksa <see cref="InvalidOperationException"/> atılır; lisansı çağıran (CLI) önce doğrular.
/// </summary>
public static class PersonBalanceEnableGate
{
    /// <summary>2. koşul: verifier'ın kural sayıları (§7.3-5, §7.3-7).</summary>
    public sealed record VerifyCheck(int D4, int D5, int D6, int OrphanLegs, int TrimMismatches, int ControlCharGroups)
    {
        public bool Passes => D4 + D5 + D6 + OrphanLegs + TrimMismatches + ControlCharGroups == 0;
    }

    /// <summary>3. koşul.</summary>
    /// <param name="HasMarker">Lisansın işaret satırı var.</param>
    /// <param name="HasBaseline">İşaret var ya da lisans ilk yakalamadan sonra verilmiş (verifier'ın kuralı).</param>
    /// <param name="Unreviewed">Lisansın <c>ReviewedAt</c>'i boş taban satırları (işaret dahil).</param>
    /// <param name="NegativeOutside">Taban dışı eksi satır.</param>
    /// <param name="Worsened">Tabandakinin altına inen eksi satır.</param>
    /// <param name="LedgerMismatchOutside">Taban dışı D1.</param>
    /// <param name="ChangedMismatch">Tabandaki <c>Balance − LedgerSum</c> farkı değişen satır.</param>
    /// <param name="CopyOutside">Taban dışı kopya satır.</param>
    public sealed record BaselineCheck(bool HasMarker, bool HasBaseline, int Unreviewed, int NegativeOutside, int Worsened,
        int LedgerMismatchOutside, int ChangedMismatch, int CopyOutside)
    {
        public bool Passes => HasBaseline && Unreviewed == 0
            && NegativeOutside + Worsened + LedgerMismatchOutside + ChangedMismatch + CopyOutside == 0;
    }

    /// <summary>4. koşul. Metinler kırpılmış; boşsa null. Ayrıştırılamayan değer de olduğu gibi gösterilir, ama
    /// koşul tutmaz.</summary>
    public sealed record SupersetCheck(string? PersonBalanceMin, string? SalesSyncMin, bool Passes);

    /// <param name="WindowDays">1. koşulun penceresi (<c>PersonBalance:ActivePcWindowDays</c>).</param>
    public sealed record Result(int WindowDays, SalesModeGate.ClientVersionCheck Clients, VerifyCheck Verify,
        BaselineCheck Baseline, SupersetCheck Superset)
    {
        public bool Passes => Clients.Passes && Verify.Passes && Baseline.Passes && Superset.Passes;

        /// <summary>Tutmayan koşulların numaraları, artan sırada.</summary>
        public IReadOnlyList<int> Failing =>
            new[] { (Clients.Passes, 1), (Verify.Passes, 2), (Baseline.Passes, 3), (Superset.Passes, 4) }
                .Where(c => !c.Item1).Select(c => c.Item2).ToList();
    }

    public static async Task<Result> EvaluateAsync(LicenseDbContext db, Guid licenseId, PersonBalanceOptions personBalance,
        SalesSyncOptions salesSync, DateTimeOffset now, CancellationToken ct)
    {
        // 1. Pencere ≤ 0 ise sorgu hiçbir etkinleştirme döndürmez ve koşul açık-hata ile geçerdi (eski PC'ler
        // görünmez). Kapalı-hata: alt sınır tanımsızmış gibi döner, Passes false. Web host'ta ValidateOnStart, CLI'da
        // enable'ın kendi denetimi bunu zaten engeller; kapı yine de çağırana güvenmez.
        var clients = personBalance.ActivePcWindowDays > 0
            ? await ClientVersionsAsync(db, licenseId, personBalance, now - personBalance.ActivePcWindow, ct)
            : new SalesModeGate.ClientVersionCheck(null, 0, []);

        // 2–3: S1 denetiminin aynı sayıları. LoadAsync ilk yakalama anını bulmak için bütün lisansların taban
        // satırlarını yükler; incelenmemiş satırlar bu yüzden lisansa göre süzülür.
        var snapshot = await new PersonBalanceVerifier(db).LoadAsync(licenseId, now, ct);
        var report = PersonBalanceVerifier.Analyze(snapshot).Licenses.Single(r => r.LicenseId == licenseId);
        var verify = new VerifyCheck(
            D4: report.D4, D5: report.D5, D6: report.D6, OrphanLegs: report.OrphanLegs,
            TrimMismatches: report.TrimMismatches, ControlCharGroups: report.ControlCharGroups);
        var baseline = new BaselineCheck(
            HasMarker: report.HasMarker, HasBaseline: report.HasBaseline,
            Unreviewed: snapshot.Baselines.Count(b => b.LicenseId == licenseId && b.ReviewedAt is null),
            NegativeOutside: report.NegativeRows.Outside, Worsened: report.Details.WorsenedBaselineRows.Count,
            LedgerMismatchOutside: report.LedgerMismatches.Outside,
            ChangedMismatch: report.Details.ChangedMismatchRows.Count,
            CopyOutside: report.CopyRows.Outside);

        // 4.
        var pb = personBalance.ParseMinClientVersion();
        var ss = salesSync.ParseMinClientVersion();
        var superset = new SupersetCheck(
            Text(personBalance.MinClientVersion), Text(salesSync.MinClientVersion),
            pb is not null && ss is not null && ClientVersion.IsAtLeast(pb, ss));

        return new Result(personBalance.ActivePcWindowDays, clients, verify, baseline, superset);
    }

    private static string? Text(string? raw) => string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();

    /// <summary><c>SalesModeGate.ClientVersionsAsync</c>'in kalıbı, ortak bakiyenin alt sınırı ve penceresiyle.</summary>
    private static async Task<SalesModeGate.ClientVersionCheck> ClientVersionsAsync(
        LicenseDbContext db, Guid licenseId, PersonBalanceOptions options, DateTimeOffset since, CancellationToken ct)
    {
        // Makine adı ve parmak izi okunmaz.
        var recent = await db.Activations.AsNoTracking()
            .Where(a => a.LicenseId == licenseId && a.DeactivatedAt == null && a.LastSeenAt >= since)
            .OrderBy(a => a.Id)
            .Select(a => new SalesModeGate.OldActivation(a.Id, a.AppVersion))
            .ToListAsync(ct);

        var min = options.ParseMinClientVersion();
        if (min is null) return new SalesModeGate.ClientVersionCheck(null, recent.Count, []);

        var old = recent.Where(a => !ClientVersion.IsAtLeast(ClientVersion.Parse(a.AppVersion), min)).ToList();
        return new SalesModeGate.ClientVersionCheck(options.MinClientVersion!.Trim(), recent.Count, old);
    }
}
