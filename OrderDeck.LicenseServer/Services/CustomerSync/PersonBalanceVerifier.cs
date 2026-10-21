using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Sync;

namespace OrderDeck.LicenseServer.Services.CustomerSync;

/// <summary>
/// Ortak bakiye denetimi (spec 2026-10-11 §7.3, §8.1) — <c>person-balance verify</c> ve günlük
/// <c>person-balance-verify</c> işinin ortak çekirdeği. <see cref="LoadAsync"/> salt okunur bir anlık görüntü alır
/// (Id'ler, tutarlar, numaralar; ad ve telefon YOK), <see cref="Analyze"/> saftır.
///
/// <para><b>Rapor:</b> 1 davranışı değişecek kişiler (≥ 2 üyesinde sıfır olmayan bakiye), 2 eksi satırlar, 3 D1
/// bozulmaları (<c>Balance ≠ Σ Amount</c>), 4 silinmiş üyede bakiye (ve grubunda canlı üye kalmamışlar), 5 kırpma
/// farkı (ham ↔ kırpılmış numara; sekme/satır sonu), 6 havuzu &lt; 0 kişi, 7 D4/D5/D6/yetim bacak, 8 kopya satırlar,
/// 9 aktif PC sürümleri, 10 iki tablonun satır sayısı. 2, 3 ve 8 taban içi/dışı ayrılır.</para>
///
/// <para><b>Uyarı (§8.1) yalnız YENİ sapmada:</b> D4/D5/D6/yetim ≠ 0 (her zaman); lisansın tabanı varken taban dışı
/// eksi satır ya da tabandaki eksi satırın daha da eksiye inmesi, taban dışı D1 ya da tabandaki satırın
/// <c>Balance − LedgerSum</c> farkının değişmesi, taban dışı kopya satır. Havuz kipindeki D3 uyarısı S2'de (S1 kip
/// bayrağını okumaz); 6. madde yalnız sayı. 4. maddenin "grubunda canlı asıl üye kalmamış" kalemi de yalnız sayı:
/// uyarıya çevrilmesi spec §10 açık kalanlarda (numara izlemesinin hakemi). <b>Tabanı var</b> ⇔ lisansın işaret
/// satırı (<see cref="LicenseMarker"/>) var ya da lisans ilk yakalamadan SONRA verilmiş (S1 öncesi verisi
/// olamaz).</para>
/// </summary>
public sealed class PersonBalanceVerifier
{
    /// <summary>Aktif PC penceresi: satış senkronu kapısıyla aynı ölçüt (14 gün). Ayarı S2'nin <c>enable</c>'ı getirir.</summary>
    public static readonly TimeSpan ActivePcWindow = SalesModeGate.RecentWindow;

    /// <summary>Taban tablosunda lisansın "taban alındı" işareti.</summary>
    public static readonly Guid LicenseMarker = Guid.Empty;

    private readonly LicenseDbContext _db;

    public PersonBalanceVerifier(LicenseDbContext db) => _db = db;

    // ── Anlık görüntü ────────────────────────────────────────────────────────

    public sealed record LicenseRow(Guid Id, DateTimeOffset IssuedAt);
    public sealed record ProjectionRow(Guid Id, Guid LicenseId, string? GroupId, Guid? MergedIntoId, bool Purged);
    public sealed record BalanceRow(Guid LicenseId, Guid WpfCustomerId, decimal Balance);
    public sealed record LedgerRow(Guid LicenseId, Guid WpfCustomerId, decimal Sum, int Count);
    public sealed record DrawRow(
        Guid Id, Guid LicenseId, Guid WpfCustomerId, decimal Amount, string Kind, Guid DrawGroupId,
        decimal? OriginalAmount, string? SaleScope);
    public sealed record BaselineRow(
        Guid LicenseId, Guid WpfCustomerId, decimal Balance, decimal LedgerSum, DateTimeOffset CapturedAt,
        DateTimeOffset? ReviewedAt);
    public sealed record ActivationRow(Guid LicenseId, string? AppVersion);

    /// <param name="ReversedIds">Bir çekim grubu satırını geri alan satırların hedefleri (D5).</param>
    /// <param name="Baselines">BÜTÜN lisansların taban satırları (ilk yakalama anı için).</param>
    public sealed record Snapshot(
        IReadOnlyList<LicenseRow> Licenses,
        IReadOnlyList<ProjectionRow> Projections,
        IReadOnlyList<BalanceRow> Balances,
        IReadOnlyList<LedgerRow> Ledger,
        IReadOnlyList<DrawRow> DrawRows,
        IReadOnlySet<Guid> ReversedIds,
        IReadOnlyList<BaselineRow> Baselines,
        IReadOnlyList<ActivationRow> ActivePcs,
        long ProjectionCount,
        long TransactionCount);

    /// <param name="licenseId">null = verisi (projeksiyon, bakiye ya da hareket) olan bütün lisanslar.</param>
    public async Task<Snapshot> LoadAsync(Guid? licenseId, DateTimeOffset now, CancellationToken ct)
    {
        var licenses = licenseId is { } only
            ? await _db.Licenses.AsNoTracking().Where(l => l.Id == only)
                .Select(l => new LicenseRow(l.Id, l.IssuedAt)).ToListAsync(ct)
            : await _db.Licenses.AsNoTracking()
                .Where(l => _db.WpfCustomerProjections.IgnoreQueryFilters().Any(p => p.LicenseId == l.Id)
                            || _db.CustomerBalances.Any(b => b.LicenseId == l.Id)
                            || _db.CustomerBalanceTransactions.Any(t => t.LicenseId == l.Id))
                .Select(l => new LicenseRow(l.Id, l.IssuedAt)).ToListAsync(ct);
        var ids = licenses.Select(l => l.Id).ToList();

        var projections = await _db.WpfCustomerProjections.IgnoreQueryFilters().AsNoTracking()
            .Where(p => ids.Contains(p.LicenseId))
            .Select(p => new ProjectionRow(p.Id, p.LicenseId, p.GroupId, p.MergedIntoId, p.PurgedAt != null))
            .ToListAsync(ct);
        var balances = await _db.CustomerBalances.AsNoTracking()
            .Where(b => ids.Contains(b.LicenseId))
            .Select(b => new BalanceRow(b.LicenseId, b.WpfCustomerId, b.Balance))
            .ToListAsync(ct);
        var ledger = await _db.CustomerBalanceTransactions.AsNoTracking()
            .Where(t => ids.Contains(t.LicenseId))
            .GroupBy(t => new { t.LicenseId, t.WpfCustomerId })
            .Select(g => new LedgerRow(g.Key.LicenseId, g.Key.WpfCustomerId, g.Sum(t => t.Amount), g.Count()))
            .ToListAsync(ct);
        var drawRows = await _db.CustomerBalanceTransactions.AsNoTracking()
            .Where(t => ids.Contains(t.LicenseId) && t.DrawGroupId != null)
            .Select(t => new DrawRow(t.Id, t.LicenseId, t.WpfCustomerId, t.Amount, t.Kind, t.DrawGroupId!.Value,
                t.OriginalAmount, t.SaleScope))
            .ToListAsync(ct);
        var reversed = await _db.CustomerBalanceTransactions.AsNoTracking()
            .Where(r => r.ReversesTransactionId != null
                        && _db.CustomerBalanceTransactions.Any(l => l.Id == r.ReversesTransactionId
                                                                    && l.DrawGroupId != null
                                                                    && ids.Contains(l.LicenseId)))
            .Select(r => r.ReversesTransactionId!.Value)
            .ToListAsync(ct);
        var baselines = await _db.PersonBalanceBaselines.AsNoTracking()
            .Select(b => new BaselineRow(b.LicenseId, b.WpfCustomerId, b.Balance, b.LedgerSum, b.CapturedAt, b.ReviewedAt))
            .ToListAsync(ct);
        var since = now - ActivePcWindow;
        var pcs = await _db.Activations.AsNoTracking()
            .Where(a => ids.Contains(a.LicenseId) && a.DeactivatedAt == null && a.LastSeenAt >= since)
            .Select(a => new ActivationRow(a.LicenseId, a.AppVersion))
            .ToListAsync(ct);
        var projectionCount = await _db.WpfCustomerProjections.IgnoreQueryFilters().LongCountAsync(ct);
        var transactionCount = await _db.CustomerBalanceTransactions.LongCountAsync(ct);

        return new Snapshot(licenses, projections, balances, ledger, drawRows, reversed.ToHashSet(), baselines, pcs,
            projectionCount, transactionCount);
    }

    // ── Rapor ────────────────────────────────────────────────────────────────

    /// <summary>Taban içi / dışı sayı ve tutar toplamı.</summary>
    public readonly record struct Tally(int InBaseline, int Outside, decimal Total);

    /// <summary><c>--details</c>'in GUID listeleri (ad/telefon yok).</summary>
    public sealed record Details(
        IReadOnlyList<Guid> NegativeOutside, IReadOnlyList<Guid> NegativeInBaseline,
        IReadOnlyList<Guid> LedgerMismatchOutside, IReadOnlyList<Guid> LedgerMismatchInBaseline,
        IReadOnlyList<Guid> CopyOutside, IReadOnlyList<Guid> StrandedPurged,
        IReadOnlyList<Guid> RuleBreakingGroups, IReadOnlyList<Guid> OrphanLegs,
        IReadOnlyList<Guid> WorsenedBaselineRows, IReadOnlyList<Guid> ChangedMismatchRows);

    public sealed record LicenseReport(
        Guid LicenseId,
        bool HasMarker,
        bool HasBaseline,
        int SpreadPersons, decimal SpreadPoolTotal,
        Tally NegativeRows,
        Tally LedgerMismatches,
        int PurgedWithBalance, decimal PurgedBalanceTotal,
        int StrandedPurged, decimal StrandedPurgedTotal,
        int TrimMismatches, int ControlCharGroups,
        int NegativePersons, int NegativePersonsExplained,
        int D4, int D5, int D6, int OrphanLegs,
        Tally CopyRows, int CopyRowsWithTransactions, int CopyRowsWithBalance,
        IReadOnlyDictionary<string, int> ActivePcVersions,
        IReadOnlyList<string> Alerts,
        Details Details);

    public sealed record Report(IReadOnlyList<LicenseReport> Licenses, long ProjectionCount, long TransactionCount);

    public static Report Analyze(Snapshot s)
    {
        var firstCapture = s.Baselines
            .Where(b => b.WpfCustomerId == LicenseMarker)
            .Select(b => (DateTimeOffset?)b.CapturedAt)
            .Min();
        var reports = s.Licenses.OrderBy(l => l.Id).Select(l => AnalyzeLicense(s, l, firstCapture)).ToList();
        return new Report(reports, s.ProjectionCount, s.TransactionCount);
    }

    private static LicenseReport AnalyzeLicense(Snapshot s, LicenseRow license, DateTimeOffset? firstCapture)
    {
        var id = license.Id;
        var projections = s.Projections.Where(p => p.LicenseId == id).ToDictionary(p => p.Id);
        var balances = s.Balances.Where(b => b.LicenseId == id).ToDictionary(b => b.WpfCustomerId, b => b.Balance);
        var ledger = s.Ledger.Where(l => l.LicenseId == id).ToDictionary(l => l.WpfCustomerId);
        var baselines = s.Baselines.Where(b => b.LicenseId == id && b.WpfCustomerId != LicenseMarker)
            .ToDictionary(b => b.WpfCustomerId);
        var hasMarker = s.Baselines.Any(b => b.LicenseId == id && b.WpfCustomerId == LicenseMarker);
        var hasBaseline = hasMarker || (firstCapture is { } first && license.IssuedAt > first);

        decimal Bal(Guid row) => balances.GetValueOrDefault(row);
        decimal Sum(Guid row) => ledger.TryGetValue(row, out var l) ? l.Sum : 0m;
        bool IsCopy(Guid row) => projections.TryGetValue(row, out var p) && p.MergedIntoId is not null;
        bool Worsened(Guid row) => baselines.TryGetValue(row, out var b) && Bal(row) < b.Balance;

        var rows = balances.Keys.Union(ledger.Keys).ToList();

        // 2. Eksi satırlar (kopyalar 8'de).
        var negative = rows.Where(r => !IsCopy(r) && Bal(r) < 0m).ToList();
        var (negIn, negOut) = Split(negative, baselines);
        // 3. D1.
        var mismatch = rows.Where(r => Bal(r) != Sum(r)).ToList();
        var (misIn, misOut) = Split(mismatch, baselines);
        // 8. Kopya satırlar: kopyaya bağlı hareket ya da sıfır olmayan bakiye.
        var copyTx = rows.Where(r => IsCopy(r) && ledger.TryGetValue(r, out var l) && l.Count > 0).ToList();
        var copyBal = rows.Where(r => IsCopy(r) && Bal(r) != 0m).ToList();
        var copies = copyTx.Union(copyBal).ToList();
        var (copyIn, copyOut) = Split(copies, baselines);

        // Kişiler: asıl kayıtlar, numara ordinal (PersonMembership kuralı); gruplanmamış satır tek başına kişi.
        var canonical = projections.Values.Where(p => p.MergedIntoId is null).ToList();
        var persons = canonical
            .GroupBy(p => PersonMembership.IsGrouped(p.GroupId) ? "g:" + p.GroupId : "c:" + p.Id.ToString("N"),
                StringComparer.Ordinal)
            .Select(g => g.ToList())
            .ToList();
        // 1.
        var spread = persons.Where(m => m.Count(p => Bal(p.Id) != 0m) >= 2).ToList();
        // 4.
        var purgedWithBalance = canonical.Where(p => p.Purged && Bal(p.Id) != 0m).ToList();
        var liveGroups = canonical.Where(p => !p.Purged && PersonMembership.IsGrouped(p.GroupId))
            .Select(p => p.GroupId!).ToHashSet(StringComparer.Ordinal);
        var stranded = purgedWithBalance
            .Where(p => PersonMembership.IsGrouped(p.GroupId) && !liveGroups.Contains(p.GroupId!)).ToList();
        // 5. Ham ↔ kırpılmış (gruplama işlerinin KeyOf'u .Trim() ile, WPF ham karşılaştırır).
        var trim = canonical.Count(p => !string.IsNullOrWhiteSpace(p.GroupId) && p.GroupId != p.GroupId.Trim());
        var control = canonical.Count(p => p.GroupId is { } g && g.IndexOfAny(['\t', '\n', '\r']) >= 0);
        // 6.
        var negativePersons = persons.Where(m => m.Sum(p => Bal(p.Id)) < 0m).ToList();
        var explained = negativePersons.Count(m => m.Where(p => Bal(p.Id) < 0m)
            .All(p => baselines.ContainsKey(p.Id) && !Worsened(p.Id)));
        // 7.
        var (d4, d5, d6, ruleGroups, orphans) = CheckDraws(s, id);
        // 9.
        var versions = s.ActivePcs.Where(a => a.LicenseId == id)
            .GroupBy(a => a.AppVersion ?? "yok", StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        // §8.1: yalnız yeni sapma.
        var worsened = negIn.Where(Worsened).ToList();
        var changedMismatch = baselines.Values
            .Where(b => Bal(b.WpfCustomerId) - Sum(b.WpfCustomerId) != b.Balance - b.LedgerSum)
            .Select(b => b.WpfCustomerId).ToList();
        var alerts = new List<string>();
        if (d4 + d5 + d6 + orphans.Count > 0)
            alerts.Add($"kural: D4={d4} D5={d5} D6={d6} yetim={orphans.Count}");
        if (hasBaseline)
        {
            if (negOut.Count + worsened.Count > 0)
                alerts.Add($"eksi: taban dışı {negOut.Count}, daha da eksiye inen {worsened.Count}");
            if (misOut.Count + changedMismatch.Count > 0)
                alerts.Add($"D1: taban dışı {misOut.Count}, farkı değişen {changedMismatch.Count}");
            if (copyOut.Count > 0)
                alerts.Add($"kopya: taban dışı {copyOut.Count}");
        }

        return new LicenseReport(
            id, hasMarker, hasBaseline,
            spread.Count, spread.Sum(m => m.Sum(p => Bal(p.Id))),
            new Tally(negIn.Count, negOut.Count, negative.Sum(Bal)),
            new Tally(misIn.Count, misOut.Count, mismatch.Sum(r => Bal(r) - Sum(r))),
            purgedWithBalance.Count, purgedWithBalance.Sum(p => Bal(p.Id)),
            stranded.Count, stranded.Sum(p => Bal(p.Id)),
            trim, control,
            negativePersons.Count, explained,
            d4, d5, d6, orphans.Count,
            new Tally(copyIn.Count, copyOut.Count, copies.Sum(Bal)), copyTx.Count, copyBal.Count,
            versions,
            alerts,
            new Details(negOut, negIn, misOut, misIn, copyOut, stranded.Select(p => p.Id).ToList(),
                ruleGroups, orphans, worsened, changedMismatch));
    }

    /// <summary>D4 (bacak türü ve boş alanları), D5 (ya hepsi ya hiçbiri geri alınmış), D6 (−Σ ≤ OriginalAmount),
    /// yetim bacak — yalnız <c>DrawGroupId</c> dolu gruplar. Çekim grubu ⇔ toplamı &lt; 0 (grup geri almaları &gt; 0).</summary>
    private static (int D4, int D5, int D6, List<Guid> RuleGroups, List<Guid> OrphanLegs) CheckDraws(Snapshot s, Guid licenseId)
    {
        int d4 = 0, d5 = 0, d6 = 0;
        var ruleGroups = new List<Guid>();
        var orphans = new List<Guid>();
        foreach (var group in s.DrawRows.Where(r => r.LicenseId == licenseId).GroupBy(r => r.DrawGroupId))
        {
            var anchor = group.FirstOrDefault(r => r.Id == group.Key);
            if (anchor is null)
            {
                orphans.AddRange(group.Select(r => r.Id));
                continue;
            }

            var broken = false;
            if (group.Where(r => r.Id != group.Key)
                .Any(l => l.Kind != anchor.Kind || l.SaleScope is not null || l.OriginalAmount is not null))
            {
                d4++;
                broken = true;
            }
            var sum = group.Sum(r => r.Amount);
            if (sum < 0m)
            {
                var reversedLegs = group.Count(r => s.ReversedIds.Contains(r.Id));
                if (reversedLegs != 0 && reversedLegs != group.Count())
                {
                    d5++;
                    broken = true;
                }
                if (anchor.Kind == PersonBalance.KindPurchaseDeduction
                    && (anchor.OriginalAmount is null || -sum > anchor.OriginalAmount))
                {
                    d6++;
                    broken = true;
                }
            }
            if (broken) ruleGroups.Add(group.Key);
        }
        return (d4, d5, d6, ruleGroups, orphans);
    }

    private static (List<Guid> In, List<Guid> Out) Split(List<Guid> rows, Dictionary<Guid, BaselineRow> baselines) =>
        (rows.Where(baselines.ContainsKey).ToList(), rows.Where(r => !baselines.ContainsKey(r)).ToList());

    // ── Taban ────────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>--capture-baseline</c>'ın yazacağı satırlar (§7.3 "Taban çizgisi"): lisansın taban DIŞI eksi, D1 ve kopya
    /// satırları (satır başına bir kez, o anki Balance ve LedgerSum'la) ve işaret yoksa işaret. Var olan taban
    /// satırına dokunulmaz.
    /// </summary>
    public static IReadOnlyList<PersonBalanceBaseline> BaselineRowsToCapture(Snapshot s, LicenseReport r, DateTimeOffset now)
    {
        var balances = s.Balances.Where(b => b.LicenseId == r.LicenseId).ToDictionary(b => b.WpfCustomerId, b => b.Balance);
        var ledger = s.Ledger.Where(l => l.LicenseId == r.LicenseId).ToDictionary(l => l.WpfCustomerId, l => l.Sum);
        var rows = r.Details.NegativeOutside
            .Concat(r.Details.LedgerMismatchOutside)
            .Concat(r.Details.CopyOutside)
            .Distinct()
            .Select(id => new PersonBalanceBaseline
            {
                LicenseId = r.LicenseId, WpfCustomerId = id,
                Balance = balances.GetValueOrDefault(id), LedgerSum = ledger.GetValueOrDefault(id), CapturedAt = now,
            })
            .ToList();
        if (!r.HasMarker)
            rows.Add(new PersonBalanceBaseline { LicenseId = r.LicenseId, WpfCustomerId = LicenseMarker, CapturedAt = now });
        return rows;
    }
}
