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
/// eksi satır ya da tabandaki bir satırın (kopya dahil) tabandakinin altında eksi olması, taban dışı D1 ya da
/// tabandaki satırın <c>Balance − LedgerSum</c> farkının değişmesi, taban dışı kopya satır. Havuz kipindeki D3
/// uyarısı S2'de (S1 kip bayrağını okumaz); 6. madde yalnız sayı. 4. maddenin "grubunda canlı asıl üye kalmamış"
/// kalemi de yalnız sayı: uyarıya çevrilmesi spec §10 açık kalanlarda (numara izlemesinin hakemi). <b>Tabanı
/// var</b> ⇔ lisansın işaret satırı (<see cref="LicenseMarker"/>) var ya da lisans ilk yakalamadan SONRA verilmiş
/// (S1 öncesi verisi olamaz).</para>
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

    /// <param name="licenseId">null = BÜTÜN lisanslar, verisi olmayanlar da (raporları boş). Taban yakalaması her
    /// lisansa işaret yazar (plan fark 7): yakalama anında verisi olmayan eski bir lisans atlansaydı işaretsiz kalır,
    /// sonradan başlayan sapmasında hiç uyarmazdı.</param>
    public async Task<Snapshot> LoadAsync(Guid? licenseId, DateTimeOffset now, CancellationToken ct)
    {
        // Lisans süzgeci her sorguda parametre: null'da düşer (lisans Id listesi taşınmaz).
        var licenses = await _db.Licenses.AsNoTracking()
            .Where(l => licenseId == null || l.Id == licenseId)
            .Select(l => new LicenseRow(l.Id, l.IssuedAt)).ToListAsync(ct);

        var projections = await _db.WpfCustomerProjections.IgnoreQueryFilters().AsNoTracking()
            .Where(p => licenseId == null || p.LicenseId == licenseId)
            .Select(p => new ProjectionRow(p.Id, p.LicenseId, p.GroupId, p.MergedIntoId, p.PurgedAt != null))
            .ToListAsync(ct);
        var balances = await _db.CustomerBalances.AsNoTracking()
            .Where(b => licenseId == null || b.LicenseId == licenseId)
            .Select(b => new BalanceRow(b.LicenseId, b.WpfCustomerId, b.Balance))
            .ToListAsync(ct);
        var ledger = await _db.CustomerBalanceTransactions.AsNoTracking()
            .Where(t => licenseId == null || t.LicenseId == licenseId)
            .GroupBy(t => new { t.LicenseId, t.WpfCustomerId })
            .Select(g => new LedgerRow(g.Key.LicenseId, g.Key.WpfCustomerId, g.Sum(t => t.Amount), g.Count()))
            .ToListAsync(ct);
        var drawRows = await _db.CustomerBalanceTransactions.AsNoTracking()
            .Where(t => (licenseId == null || t.LicenseId == licenseId) && t.DrawGroupId != null)
            .Select(t => new DrawRow(t.Id, t.LicenseId, t.WpfCustomerId, t.Amount, t.Kind, t.DrawGroupId!.Value,
                t.OriginalAmount, t.SaleScope))
            .ToListAsync(ct);
        var reversed = await _db.CustomerBalanceTransactions.AsNoTracking()
            .Where(r => r.ReversesTransactionId != null
                        && _db.CustomerBalanceTransactions.Any(l => l.Id == r.ReversesTransactionId
                                                                    && l.DrawGroupId != null
                                                                    && (licenseId == null || l.LicenseId == licenseId)))
            .Select(r => r.ReversesTransactionId!.Value)
            .ToListAsync(ct);
        var reversedIds = reversed.ToHashSet();
        (drawRows, reversedIds) = await RereadDrawSuspectsAsync(licenseId, drawRows, reversedIds, ct);
        var baselines = await _db.PersonBalanceBaselines.AsNoTracking()
            .Select(b => new BaselineRow(b.LicenseId, b.WpfCustomerId, b.Balance, b.LedgerSum, b.CapturedAt, b.ReviewedAt))
            .ToListAsync(ct);
        (balances, ledger) = await RereadLedgerSuspectsAsync(licenseId, balances, ledger, baselines, ct);
        var since = now - ActivePcWindow;
        var pcs = await _db.Activations.AsNoTracking()
            .Where(a => (licenseId == null || a.LicenseId == licenseId) && a.DeactivatedAt == null && a.LastSeenAt >= since)
            .Select(a => new ActivationRow(a.LicenseId, a.AppVersion))
            .ToListAsync(ct);
        var projectionCount = await _db.WpfCustomerProjections.IgnoreQueryFilters().LongCountAsync(ct);
        var transactionCount = await _db.CustomerBalanceTransactions.LongCountAsync(ct);

        return new Snapshot(licenses, projections, balances, ledger, drawRows, reversedIds, baselines, pcs,
            projectionCount, transactionCount);
    }

    /// <summary>Yeniden okumanın parti boyu (<c>Contains</c> parametre sınırı).</summary>
    private const int RereadBatchSize = 500;

    /// <summary>
    /// Kural bozan çekim gruplarını ve yetim bacakların gruplarını yeniden okur: grubun satırları ve geri alınıp
    /// alınmadıkları AYNI ifadede (<see cref="RereadLedgerSuspectsAsync"/>'un kalıbı). Çekim satırları ve geri alma
    /// hedefleri ayrı taramalardır. Kilitli READ COMMITTED'da bir tarama, tek işlemde eklenmiş bir grubun bir kısmını
    /// görüp taramanın geçtiği konumlara eklenenleri kaçırabilir. O zaman çapası görünmeyen bacak yetim, geri
    /// almasının bir kısmı görünen grup D5 sayılır ve 'kural' uyarısı tabandan bağımsız çıkar (sahte e-posta).
    /// Görülen kısım commit edilmişse grubun tamamı da edilmiştir; yeniden okuma onu bütün görür. İki okumada da tutan
    /// ihlal kalır. S1'de dolu <c>DrawGroupId</c> yok (havuz kipi açılmadan dolmaz, spec §3.2); S2'nin verisiyle aynı
    /// kod koşar.
    /// </summary>
    private async Task<(List<DrawRow> Draws, HashSet<Guid> Reversed)> RereadDrawSuspectsAsync(
        Guid? licenseId, List<DrawRow> draws, HashSet<Guid> reversed, CancellationToken ct)
    {
        var suspects = new HashSet<Guid>();
        foreach (var license in draws.GroupBy(d => d.LicenseId))
        {
            var (_, _, _, ruleGroups, orphans) = CheckDraws(license, reversed);
            suspects.UnionWith(ruleGroups.Select(g => g.Key));
            suspects.UnionWith(orphans.Select(o => o.DrawGroupId));
        }
        if (suspects.Count == 0) return (draws, reversed);

        var fresh = new List<DrawRow>();
        var freshReversed = new HashSet<Guid>();
        foreach (var chunk in suspects.Chunk(RereadBatchSize))
        {
            var groups = chunk.Select(g => (Guid?)g).ToList();
            var rows = await _db.CustomerBalanceTransactions.AsNoTracking()
                .Where(t => (licenseId == null || t.LicenseId == licenseId) && groups.Contains(t.DrawGroupId))
                .Select(t => new
                {
                    t.Id, t.LicenseId, t.WpfCustomerId, t.Amount, t.Kind, DrawGroupId = t.DrawGroupId!.Value,
                    t.OriginalAmount, t.SaleScope,
                    Reversed = _db.CustomerBalanceTransactions.Any(r => r.ReversesTransactionId == t.Id),
                })
                .ToListAsync(ct);
            fresh.AddRange(rows.Select(t => new DrawRow(
                t.Id, t.LicenseId, t.WpfCustomerId, t.Amount, t.Kind, t.DrawGroupId, t.OriginalAmount, t.SaleScope)));
            freshReversed.UnionWith(rows.Where(t => t.Reversed).Select(t => t.Id));
        }

        // Şüpheli grupların satırları ve geri alınmışlıkları yalnız yeniden okumadan gelir.
        reversed.ExceptWith(draws.Where(d => suspects.Contains(d.DrawGroupId)).Select(d => d.Id));
        reversed.UnionWith(freshReversed);
        return (draws.Where(d => !suspects.Contains(d.DrawGroupId)).Concat(fresh).ToList(), reversed);
    }

    /// <summary>
    /// D1 adaylarını — ilk okumada <c>Balance ≠ Σ</c> olan satırları ve lisansın bütün taban satırlarını — yeniden
    /// okur; satır başına bakiye ve defter AYNI ifadede. Bakiyeler ve defter yukarıda ayrı sorgular; prod'da RCSI ya da
    /// snapshot yalıtımı yok ve aralarında commit eden bir yazım (apply, iade, kimlik birleştirici)
    /// <c>Balance(eski) ≠ Σ(yeni)</c> üretir: günlük işte sahte "D1" uyarısı, taban yakalamasında yanlış farklı,
    /// her gün "farkı değişen" diye uyaran kalıcı taban satırı. Tek ifade de anlık görüntü değildir (kilitli READ
    /// COMMITTED), ama pencere bir satırın iki okuması arasına daralır. Önce bakiye satırı OLMAYANLAR, sonra olanlar
    /// okunur ve ikincisi önceliklidir: arada açılan bakiye satırı ikincide tutarlı görünür. İkisinde de yoksa satırın
    /// ne bakiyesi ne hareketi kalmıştır.
    /// </summary>
    private async Task<(List<BalanceRow> Balances, List<LedgerRow> Ledger)> RereadLedgerSuspectsAsync(
        Guid? licenseId, List<BalanceRow> balances, List<LedgerRow> ledger, List<BaselineRow> baselines,
        CancellationToken ct)
    {
        var bal = balances.ToDictionary(b => (b.LicenseId, b.WpfCustomerId));
        var led = ledger.ToDictionary(l => (l.LicenseId, l.WpfCustomerId));
        var suspects = bal.Keys.Union(led.Keys)
            .Where(k => (bal.TryGetValue(k, out var b) ? b.Balance : 0m) != (led.TryGetValue(k, out var l) ? l.Sum : 0m))
            .Concat(baselines
                .Where(b => b.WpfCustomerId != LicenseMarker && (licenseId is null || b.LicenseId == licenseId))
                .Select(b => (b.LicenseId, b.WpfCustomerId)))
            .ToHashSet();
        if (suspects.Count == 0) return (balances, ledger);

        foreach (var key in suspects)
        {
            bal.Remove(key);
            led.Remove(key);
        }
        foreach (var chunk in suspects.Select(k => k.WpfCustomerId).Distinct().Chunk(RereadBatchSize))
        {
            var rows = chunk.ToList();
            var withoutBalanceRow = await _db.CustomerBalanceTransactions.AsNoTracking()
                .Where(t => (licenseId == null || t.LicenseId == licenseId) && rows.Contains(t.WpfCustomerId)
                            && !_db.CustomerBalances.Any(b => b.LicenseId == t.LicenseId
                                                              && b.WpfCustomerId == t.WpfCustomerId))
                .GroupBy(t => new { t.LicenseId, t.WpfCustomerId })
                .Select(g => new LedgerRow(g.Key.LicenseId, g.Key.WpfCustomerId, g.Sum(t => t.Amount), g.Count()))
                .ToListAsync(ct);
            var withBalanceRow = await _db.CustomerBalances.AsNoTracking()
                .Where(b => (licenseId == null || b.LicenseId == licenseId) && rows.Contains(b.WpfCustomerId))
                .Select(b => new
                {
                    b.LicenseId, b.WpfCustomerId, b.Balance,
                    Sum = _db.CustomerBalanceTransactions
                        .Where(t => t.LicenseId == b.LicenseId && t.WpfCustomerId == b.WpfCustomerId)
                        .Sum(t => t.Amount),
                    Count = _db.CustomerBalanceTransactions
                        .Count(t => t.LicenseId == b.LicenseId && t.WpfCustomerId == b.WpfCustomerId),
                })
                .ToListAsync(ct);

            foreach (var row in withoutBalanceRow.Where(r => suspects.Contains((r.LicenseId, r.WpfCustomerId))))
                led[(row.LicenseId, row.WpfCustomerId)] = row;
            foreach (var row in withBalanceRow.Where(r => suspects.Contains((r.LicenseId, r.WpfCustomerId))))
            {
                var key = (row.LicenseId, row.WpfCustomerId);
                bal[key] = new BalanceRow(row.LicenseId, row.WpfCustomerId, row.Balance);
                if (row.Count > 0) led[key] = new LedgerRow(row.LicenseId, row.WpfCustomerId, row.Sum, row.Count);
                else led.Remove(key);
            }
        }
        return (bal.Values.ToList(), led.Values.ToList());
    }

    // ── Rapor ────────────────────────────────────────────────────────────────

    /// <summary>Taban içi / dışı sayı ve tutar toplamı.</summary>
    public readonly record struct Tally(int InBaseline, int Outside, decimal Total);

    /// <summary><c>--details</c>'in GUID listeleri (ad/telefon yok). <c>*Rows</c> ve satır listeleri
    /// <c>WpfCustomerId</c>'dir (§7.3); <see cref="RuleBreakingGroups"/> (çapa) ve <see cref="OrphanLegs"/> (bacak)
    /// ise HAREKET Id'leri — grubu defterde bulmak için, satırları yanlarında.</summary>
    public sealed record Details(
        IReadOnlyList<Guid> NegativeOutside, IReadOnlyList<Guid> NegativeInBaseline,
        IReadOnlyList<Guid> LedgerMismatchOutside, IReadOnlyList<Guid> LedgerMismatchInBaseline,
        IReadOnlyList<Guid> CopyOutside, IReadOnlyList<Guid> StrandedPurged,
        IReadOnlyList<Guid> RuleBreakingGroups, IReadOnlyList<Guid> RuleBreakingRows,
        IReadOnlyList<Guid> OrphanLegs, IReadOnlyList<Guid> OrphanLegRows,
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
        Tally CopyRows, int CopyRowsWithTransactions, int CopyTransactionCount, int CopyRowsWithBalance,
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
        var data = ByLicense.Of(s);
        var reports = s.Licenses.OrderBy(l => l.Id)
            .Select(l => AnalyzeLicense(data, s.ReversedIds, l, firstCapture))
            .ToList();
        return new Report(reports, s.ProjectionCount, s.TransactionCount);
    }

    /// <summary>Anlık görüntünün listeleri lisansa göre BİR kez gruplanmış. Lisans başına süzmek O(L·N) olurdu:
    /// lisanssız yük verisizler dahil bütün lisansları getirir (plan fark 16).</summary>
    private sealed record ByLicense(
        ILookup<Guid, ProjectionRow> Projections, ILookup<Guid, BalanceRow> Balances, ILookup<Guid, LedgerRow> Ledger,
        ILookup<Guid, DrawRow> Draws, ILookup<Guid, BaselineRow> Baselines, ILookup<Guid, ActivationRow> ActivePcs)
    {
        public static ByLicense Of(Snapshot s) => new(
            Projections: s.Projections.ToLookup(p => p.LicenseId),
            Balances: s.Balances.ToLookup(b => b.LicenseId),
            Ledger: s.Ledger.ToLookup(l => l.LicenseId),
            Draws: s.DrawRows.ToLookup(r => r.LicenseId),
            Baselines: s.Baselines.ToLookup(b => b.LicenseId),
            ActivePcs: s.ActivePcs.ToLookup(a => a.LicenseId));
    }

    private static LicenseReport AnalyzeLicense(
        ByLicense data, IReadOnlySet<Guid> reversedIds, LicenseRow license, DateTimeOffset? firstCapture)
    {
        var id = license.Id;
        var projections = data.Projections[id].ToDictionary(p => p.Id);
        var balances = data.Balances[id].ToDictionary(b => b.WpfCustomerId, b => b.Balance);
        var ledger = data.Ledger[id].ToDictionary(l => l.WpfCustomerId);
        var baselines = data.Baselines[id].Where(b => b.WpfCustomerId != LicenseMarker).ToDictionary(b => b.WpfCustomerId);
        var hasMarker = data.Baselines[id].Any(b => b.WpfCustomerId == LicenseMarker);
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
        // 5. Ham ↔ kırpılmış (gruplama işlerinin KeyOf'u .Trim() ile, WPF ham karşılaştırır). Tanım spec §7.3-5'in
        // SQL'inden (DATALENGTH ↔ LTRIM/RTRIM, bütün satırlar) BİLEREK farklı, KeyOf ile birebir: yalnız asıl kayıtlar,
        // .NET Trim (bütün boşluk karakterleri), yalnız boşluk numara grupsuz. Sekme/satır sonu sayısı da aynı kuralla:
        // yalnız sekme ve satır sonundan oluşan numara iki yanda da grupsuzdur, ayrışma riski taşımaz. S2'nin enable'ı
        // bu iki sayıyı kullanır (plan fark 15).
        var trim = canonical.Count(p => PersonMembership.IsGrouped(p.GroupId) && p.GroupId != p.GroupId!.Trim());
        var control = canonical.Count(p => PersonMembership.IsGrouped(p.GroupId) && p.GroupId!.IndexOfAny(['\t', '\n', '\r']) >= 0);
        // 6.
        var negativePersons = persons.Where(m => m.Sum(p => Bal(p.Id)) < 0m).ToList();
        var explained = negativePersons.Count(m => m.Where(p => Bal(p.Id) < 0m)
            .All(p => baselines.ContainsKey(p.Id) && !Worsened(p.Id)));
        // 7.
        var (d4, d5, d6, ruleGroups, orphans) = CheckDraws(data.Draws[id], reversedIds);
        // 9.
        var versions = data.ActivePcs[id]
            .GroupBy(a => a.AppVersion ?? "yok", StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        // §8.1: yalnız yeni sapma. "Tabandaki satırın Balance'ı tabandakinden düşük" (D2'nin izi) yalnız şu an eksi
        // satırlarda: artı satırın düşmesi olağan çekim (eksiye inen artı taban satırı burada yakalanır). Kopyalar da
        // dahil — 2. madde onları saymaz, ama tabana alınmış kopyaya sonradan yazım §4.12'nin dar penceresidir.
        var worsened = rows.Where(r => Bal(r) < 0m && Worsened(r)).ToList();
        // Spec'in lafzı: farkın her değişimi, düzeltilip 0'a inmesi de. Susturma yolu sahip kararı (plan fark 22).
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

        // Adlandırılmış argümanlar: kayıtların çoğu alanı aynı türde (int, Tally, List<Guid>); yer değiştirme sessizce
        // derlenirdi.
        return new LicenseReport(
            LicenseId: id, HasMarker: hasMarker, HasBaseline: hasBaseline,
            SpreadPersons: spread.Count, SpreadPoolTotal: spread.Sum(m => m.Sum(p => Bal(p.Id))),
            NegativeRows: new Tally(InBaseline: negIn.Count, Outside: negOut.Count, Total: negative.Sum(Bal)),
            LedgerMismatches: new Tally(
                InBaseline: misIn.Count, Outside: misOut.Count, Total: mismatch.Sum(r => Bal(r) - Sum(r))),
            PurgedWithBalance: purgedWithBalance.Count, PurgedBalanceTotal: purgedWithBalance.Sum(p => Bal(p.Id)),
            StrandedPurged: stranded.Count, StrandedPurgedTotal: stranded.Sum(p => Bal(p.Id)),
            TrimMismatches: trim, ControlCharGroups: control,
            NegativePersons: negativePersons.Count, NegativePersonsExplained: explained,
            D4: d4, D5: d5, D6: d6, OrphanLegs: orphans.Count,
            CopyRows: new Tally(InBaseline: copyIn.Count, Outside: copyOut.Count, Total: copies.Sum(Bal)),
            CopyRowsWithTransactions: copyTx.Count,
            CopyTransactionCount: copyTx.Sum(r => ledger[r].Count),
            CopyRowsWithBalance: copyBal.Count,
            ActivePcVersions: versions,
            Alerts: alerts,
            Details: new Details(
                NegativeOutside: negOut, NegativeInBaseline: negIn,
                LedgerMismatchOutside: misOut, LedgerMismatchInBaseline: misIn,
                CopyOutside: copyOut, StrandedPurged: stranded.Select(p => p.Id).ToList(),
                RuleBreakingGroups: ruleGroups.Select(g => g.Key).ToList(),
                RuleBreakingRows: RowsOf(ruleGroups.SelectMany(g => g)),
                OrphanLegs: orphans.Select(r => r.Id).ToList(), OrphanLegRows: RowsOf(orphans),
                WorsenedBaselineRows: worsened, ChangedMismatchRows: changedMismatch));

        static List<Guid> RowsOf(IEnumerable<DrawRow> draws) => draws.Select(r => r.WpfCustomerId).Distinct().ToList();
    }

    /// <summary>D4 (bacak türü ve boş alanları), D5 (ya hepsi ya hiçbiri geri alınmış), D6 (−Σ ≤ OriginalAmount),
    /// yetim bacak — bir lisansın <c>DrawGroupId</c> dolu satırları. Çekim grubu ⇔ toplamı &lt; 0 (grup geri almaları
    /// &gt; 0).</summary>
    private static (int D4, int D5, int D6, List<IGrouping<Guid, DrawRow>> RuleGroups, List<DrawRow> OrphanLegs) CheckDraws(
        IEnumerable<DrawRow> licenseDraws, IReadOnlySet<Guid> reversedIds)
    {
        int d4 = 0, d5 = 0, d6 = 0;
        var ruleGroups = new List<IGrouping<Guid, DrawRow>>();
        var orphans = new List<DrawRow>();
        foreach (var group in licenseDraws.GroupBy(r => r.DrawGroupId))
        {
            var anchor = group.FirstOrDefault(r => r.Id == group.Key);
            if (anchor is null)
            {
                orphans.AddRange(group);
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
                var reversedLegs = group.Count(r => reversedIds.Contains(r.Id));
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
            if (broken) ruleGroups.Add(group);
        }
        return (d4, d5, d6, ruleGroups, orphans);
    }

    private static (List<Guid> In, List<Guid> Out) Split(List<Guid> rows, Dictionary<Guid, BaselineRow> baselines) =>
        (rows.Where(baselines.ContainsKey).ToList(), rows.Where(r => !baselines.ContainsKey(r)).ToList());

    // ── Taban ────────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>--capture-baseline</c>'ın yazacağı satırlar (§7.3 "Taban çizgisi"). Tabanı OLMAYAN lisansta taban dışı eksi,
    /// D1 ve kopya satırları (satır başına bir kez, o anki Balance ve LedgerSum'la) ve işaret; tabanı olup işareti
    /// olmayan lisansta (ilk yakalamadan sonra verilmiş) yalnız işaret; işaretli lisansta hiçbir şey.
    ///
    /// <para><b>Yeniden yakalama yeni sapmayı tabana almaz.</b> Taban S1 öncesinden kalanı tutar ("yeni kod onları
    /// üretmez", §7.3); tabanı olan lisansın taban dışı satırı yakalamadan sonra doğmuştur ve §8.1'in uyardığı yeni
    /// sapmadır. Yakalama yeniden koşunca (pilot için <c>--license</c>, prosedürün tekrarı) onu tabana yazsaydı uyarı
    /// kalıcı olarak susardı. Taban dışı bir satırı bilinçli kabul etmek ayrı, hedefli bir iştir (sahip kararı). Var olan
    /// taban satırına dokunulmaz.</para>
    /// </summary>
    public static IReadOnlyList<PersonBalanceBaseline> BaselineRowsToCapture(Snapshot s, LicenseReport r, DateTimeOffset now)
    {
        if (r.HasMarker) return [];
        var marker = new PersonBalanceBaseline { LicenseId = r.LicenseId, WpfCustomerId = LicenseMarker, CapturedAt = now };
        if (r.HasBaseline) return [marker];

        var ids = r.Details.NegativeOutside
            .Concat(r.Details.LedgerMismatchOutside)
            .Concat(r.Details.CopyOutside)
            .ToHashSet();
        var balances = s.Balances.Where(b => b.LicenseId == r.LicenseId && ids.Contains(b.WpfCustomerId))
            .ToDictionary(b => b.WpfCustomerId, b => b.Balance);
        var ledger = s.Ledger.Where(l => l.LicenseId == r.LicenseId && ids.Contains(l.WpfCustomerId))
            .ToDictionary(l => l.WpfCustomerId, l => l.Sum);
        var rows = ids
            .Select(id => new PersonBalanceBaseline
            {
                LicenseId = r.LicenseId, WpfCustomerId = id,
                Balance = balances.GetValueOrDefault(id), LedgerSum = ledger.GetValueOrDefault(id), CapturedAt = now,
            })
            .ToList();
        rows.Add(marker);
        return rows;
    }
}
