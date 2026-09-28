using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain.Bank;

namespace OrderDeck.LicenseServer.Services.Bank;

/// <summary>Katman isabeti: bu katmanın önerisi insan kararıyla doğrulandı / çelişti.</summary>
public sealed record LayerStat(PaymentMatchLayer Layer, int Confirmed, int Contradicted);

/// <summary>Gerçek müşterinin platformuna göre insan kararları. Katman 1 (açıklamada kullanıcı adı) pratikte yalnız
/// Instagram/TikTok ve kayıt formu kullanıcı adlarında işler: YouTube satırlarında <c>Username</c> kanal kimliği, Facebook'ta
/// sayısal kimliktir; havale açıklamasında geçmez. Bilinmeyen, başka lisansın ya da KVKK'yla silinmiş müşteri
/// <see cref="PaymentMatchMetrics.UnknownPlatform"/> altında sayılır.</summary>
public sealed record PlatformStat(string Platform, int Confirmed, int Contradicted, int ManualOnly)
{
    public int Linked => Confirmed + Contradicted + ManualOnly;
    public decimal? ContradictionRate => PaymentMatchMetrics.Rate(Contradicted, Confirmed + Contradicted);
}

/// <summary>Spec §9 sayımları (kayan pencere).
/// <para><see cref="Contradicted"/> öneriden farklı müşteriye verilmiş elle eşlemeyi de sayar: satırın durumu ManualOnly'dir
/// (plan), ama öneri yanlıştı; sayılmasaydı çelişki oranı olduğundan iyi görünürdü. <see cref="ManualOnly"/> yalnız önerisi
/// olmayan hareketin insan bağıdır.</para>
/// <para><b>Karar kaynağı.</b> Öneri kararları (<see cref="Confirmed"/> + <see cref="Contradicted"/>) kaynağına göre ayrılır:
/// dekont onaylı (satır bir dekonta bağlı, <see cref="PaymentMatch.PaymentId"/> dolu: <see cref="ReceiptConfirmed"/>,
/// <see cref="ReceiptContradicted"/>) ve yalnız sayfa (<see cref="PageConfirmed"/>, <see cref="PageContradicted"/>). Panelde
/// dekont onayı öneri görülmeden verilir; dekontun müşterisi satırın gerçek müşterisidir, aynı müşteriye elle verilmiş karara
/// sonradan iliştirilen dekont da öyle: bağımsız kanıttır. Sayfadaki elle eşleme ise öneriye bakarak verilir, öneriye eşit
/// elle eşleme doğrulandı sayılır (çapalı). Faz 2 (otomatik onay) kararı bağımsız kanıta dayanmalı: eşik yalnız dekont onaylı
/// kararlardan hesaplanır (<see cref="MeetsPhase2Threshold"/>), sayfa kararları ayrı gösterilir, ne örnekleme ne orana
/// girer.</para></summary>
public sealed record PaymentMatchSummary(
    int Incoming, int Excluded, int Proposed, int Confirmed, int Contradicted, int ManualOnly,
    int ReceiptConfirmed, int ReceiptContradicted, int PendingProposals, int NoProposal, int OpenGaps, decimal? ContradictionRate,
    TimeSpan? LagMedian, TimeSpan? LagMax, IReadOnlyList<LayerStat> Layers, IReadOnlyList<PlatformStat> Platforms)
{
    /// <summary>Faz 2 eşiğinin örneklemi: en az bu kadar dekont onaylı öneri kararı.</summary>
    public const int Phase2MinReceiptDecisions = 200;

    /// <summary>Faz 2 eşiğinde dekont onaylı öneri kararlarının en çok yüzde kaçı çelişki olabilir.</summary>
    public const int Phase2MaxContradictionPercent = 2;

    public int Linked => Confirmed + Contradicted + ManualOnly;

    /// <summary>Dekont onaylı öneri kararları: Faz 2 eşiğinin örneklemi.</summary>
    public int ReceiptDecisions => ReceiptConfirmed + ReceiptContradicted;

    /// <summary>Yalnız sayfada verilmiş (dekontsuz) öneri kararları; gösterilir, eşiğe girmez.</summary>
    public int PageConfirmed => Confirmed - ReceiptConfirmed;
    public int PageContradicted => Contradicted - ReceiptContradicted;

    /// <summary>Dekont onaylı kararların çelişki oranı; yalnız gösterim (<see cref="PaymentMatchMetrics.Rate"/>).</summary>
    public decimal? ReceiptContradictionRate => PaymentMatchMetrics.Rate(ReceiptContradicted, ReceiptDecisions);

    /// <summary>Faz 2 geçiş kararı: ≥ <see cref="Phase2MinReceiptDecisions"/> dekont onaylı öneri kararı VE onların çelişkisi
    /// ≤ %<see cref="Phase2MaxContradictionPercent"/>, tam sayılardan (çelişki × 100 ≤ karar × 2). Yuvarlı oran yalnız
    /// gösterim içindir; onunla karar verilseydi %2,00–%2,05 arası gerçek oran 0,020'ye yuvarlanıp eşiği geçerdi. Sayfa kararları
    /// hesaba girmez (sınıf özeti).</summary>
    public bool MeetsPhase2Threshold => ReceiptDecisions >= Phase2MinReceiptDecisions
        && ReceiptContradicted * 100 <= ReceiptDecisions * Phase2MaxContradictionPercent;
}

/// <summary>Gölge eşleştirmenin ölçümü (spec §9). Salt okur.
/// <para><b>Nüfus</b>: penceredeki GELEN hareketler, hareketin anına (<see cref="BankTransaction.OccurredAt"/>) göre; her sayım
/// bu hareketlerin eşleşme satırlarından. Satırın yaratılma anı pencere değildir: ilk çekim 90 günü geri doldurur, eski
/// hareketin önerisi bugün yazılır. Dışlanan hareket (<see cref="PaymentMatcher.IsExcluded"/>: POS tahsilatı, sıfır tutar)
/// yalnız <see cref="PaymentMatchSummary.Excluded"/>'da sayılır, eşleştirme sayımlarına girmez: müşteri havalesi değildir.
/// Dışlama hareketin kendi alanlarından okunur; "excluded:*" kanıtı 180 günde boşaltılır. Gecikme ise tüm gelenlerden.
/// Tek istisna açık gap'tir: dekontun hareketi yoktur, gap'in yaratılma anına (<see cref="PaymentMatchGap.CreatedAt"/>) göre
/// pencerelenir.</para></summary>
public sealed class PaymentMatchMetrics
{
    /// <summary>Gecikme ölçümünde 7 günü aşan farklar (ilk geri doldurma) sayım dışı.</summary>
    public const int LagCapDays = 7;

    /// <summary>Platform kırılımında gerçek müşterisi bilinmeyen (silinmiş, başka lisansın, projeksiyonu olmayan) kararlar.</summary>
    public const string UnknownPlatform = "?";

    private readonly LicenseDbContext _db;
    private readonly PaymentMatcher _matcher;

    public PaymentMatchMetrics(LicenseDbContext db, PaymentMatcher matcher)
    { _db = db; _matcher = matcher; }

    public async Task<PaymentMatchSummary> ComputeAsync(Guid licenseId, int days, CancellationToken ct)
    {
        var since = DateTimeOffset.UtcNow.AddDays(-days);
        // Yalnız dışlama ve gecikme için gereken alanlar: ham JSON ve açıklama okunmaz.
        var incoming = await _db.BankTransactions.AsNoTracking()
            .Where(t => t.LicenseId == licenseId && t.Direction == BankTransactionDirection.Incoming && t.OccurredAt >= since)
            .Select(t => new BankTransaction
            {
                Id = t.Id, Direction = t.Direction, Amount = t.Amount, TransactionCode = t.TransactionCode,
                OccurredAt = t.OccurredAt, FetchedAt = t.FetchedAt,
            })
            .ToListAsync(ct);
        var excluded = incoming.Where(_matcher.IsExcluded).Select(t => t.Id).ToHashSet();
        var rows = await (from m in _db.PaymentMatches.AsNoTracking()
                          join t in _db.BankTransactions on m.BankTransactionId equals t.Id
                          where m.LicenseId == licenseId && t.Direction == BankTransactionDirection.Incoming && t.OccurredAt >= since
                          select new
                          {
                              m.BankTransactionId, m.Status, m.Layer, HasProposal = m.ProposedWpfCustomerId != null,
                              HasReceipt = m.PaymentId != null,
                              ActualPlatform = _db.WpfCustomerProjections
                                  .Where(c => c.Id == m.ActualWpfCustomerId && c.LicenseId == licenseId && c.PurgedAt == null)
                                  .Select(c => c.Platform).FirstOrDefault(),
                          })
            .ToListAsync(ct);
        var decisions = rows.Where(r => !excluded.Contains(r.BankTransactionId))
            .Select(r => new Decision(Classify(r.Status, r.HasProposal), r.Layer,
                string.IsNullOrWhiteSpace(r.ActualPlatform) ? UnknownPlatform : r.ActualPlatform, r.HasReceipt))
            .ToList();
        var openGaps = await _db.PaymentMatchGaps.CountAsync(g => g.LicenseId == licenseId && g.CreatedAt >= since && g.ResolvedAt == null, ct);

        int Of(Outcome o) => decisions.Count(d => d.Outcome == o);
        int ReceiptOf(Outcome o) => decisions.Count(d => d.Outcome == o && d.Receipt);
        var confirmed = Of(Outcome.Confirmed); var contradicted = Of(Outcome.Contradicted); var pending = Of(Outcome.Pending);

        var lagTicks = incoming.Select(x => (x.FetchedAt - x.OccurredAt).Ticks)
            .Where(l => l >= 0 && l <= TimeSpan.FromDays(LagCapDays).Ticks).OrderBy(l => l).ToList();
        TimeSpan? lagMedian = lagTicks.Count == 0 ? null
            : TimeSpan.FromTicks(lagTicks.Count % 2 == 1 ? lagTicks[lagTicks.Count / 2] : (lagTicks[lagTicks.Count / 2 - 1] + lagTicks[lagTicks.Count / 2]) / 2);
        TimeSpan? lagMax = lagTicks.Count == 0 ? null : TimeSpan.FromTicks(lagTicks[^1]);

        var layers = decisions
            .Where(d => d.Outcome is Outcome.Confirmed or Outcome.Contradicted)
            .GroupBy(d => d.Layer)
            .Select(g => new LayerStat(g.Key, g.Count(d => d.Outcome == Outcome.Confirmed), g.Count(d => d.Outcome == Outcome.Contradicted)))
            .OrderBy(l => l.Layer).ToList();
        var platforms = decisions
            .Where(d => d.Outcome is Outcome.Confirmed or Outcome.Contradicted or Outcome.ManualOnly)
            .GroupBy(d => d.Platform)
            .Select(g => new PlatformStat(g.Key, g.Count(d => d.Outcome == Outcome.Confirmed),
                g.Count(d => d.Outcome == Outcome.Contradicted), g.Count(d => d.Outcome == Outcome.ManualOnly)))
            .OrderByDescending(p => p.Linked).ThenBy(p => p.Platform, StringComparer.Ordinal).ToList();

        return new PaymentMatchSummary(
            Incoming: incoming.Count, Excluded: excluded.Count,
            Proposed: confirmed + contradicted + pending, Confirmed: confirmed, Contradicted: contradicted,
            ManualOnly: Of(Outcome.ManualOnly),
            ReceiptConfirmed: ReceiptOf(Outcome.Confirmed), ReceiptContradicted: ReceiptOf(Outcome.Contradicted), PendingProposals: pending,
            NoProposal: Of(Outcome.NoProposal), OpenGaps: openGaps,
            ContradictionRate: Rate(contradicted, confirmed + contradicted),
            LagMedian: lagMedian, LagMax: lagMax, Layers: layers, Platforms: platforms);
    }

    /// <summary>Gösterim oranı, üç basamağa yuvarlı; payda sıfırsa null (karar yok, oran yok). Eşik kararında kullanılmaz.</summary>
    public static decimal? Rate(int part, int whole) => whole == 0 ? null : Math.Round((decimal)part / whole, 3);

    /// <summary>Satırın ölçümdeki anlamı. Durumdan farkı: öneriden farklı müşteriye elle eşleme (ManualOnly + öneri) çelişkidir.</summary>
    private enum Outcome { Pending, NoProposal, Confirmed, Contradicted, ManualOnly }

    /// <summary><paramref name="Receipt"/>: satır bir dekonta bağlı (dekont onaylı karar).</summary>
    private readonly record struct Decision(Outcome Outcome, PaymentMatchLayer Layer, string Platform, bool Receipt);

    private static Outcome Classify(PaymentMatchStatus status, bool hasProposal) => status switch
    {
        PaymentMatchStatus.Proposed => Outcome.Pending,
        PaymentMatchStatus.NoProposal => Outcome.NoProposal,
        PaymentMatchStatus.ConfirmedByHuman => Outcome.Confirmed,
        PaymentMatchStatus.Contradicted => Outcome.Contradicted,
        // Elle eşleme öneriden farklı müşteriye ManualOnly yazar (plan): öneri yanlıştı.
        PaymentMatchStatus.ManualOnly => hasProposal ? Outcome.Contradicted : Outcome.ManualOnly,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Ölçümde karşılığı olmayan eşleşme durumu."),
    };
}
