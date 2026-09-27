using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Domain.Bank;
using OrderDeck.LicenseServer.Services.Bank;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Bank;

public sealed class PaymentMatchMetricsTests
{
    private static LicenseDbContext NewDb()
        => new(new DbContextOptionsBuilder<LicenseDbContext>().UseInMemoryDatabase($"metrics-{Guid.NewGuid():N}").Options);

    /// <summary>Dışlama kararı eşleştiricinin varsayılan ayarıyla (CCP) verilir.</summary>
    private static PaymentMatchMetrics Metrics(LicenseDbContext db)
        => new(db, new PaymentMatcher(db, Options.Create(new BankOptions()), NullLogger<PaymentMatcher>.Instance));

    private static BankTransaction T(Guid lic, DateTimeOffset now, int daysAgo, BankTransactionDirection dir = BankTransactionDirection.Incoming,
        string? code = null, decimal amount = 10m) => new()
    { Id = Guid.NewGuid(), LicenseId = lic, ObifinId = Random.Shared.NextInt64(1, 1_000_000_000), ObifinAccountId = 1, BankaKodu = "qnb", Direction = dir,
      Amount = amount, Currency = "TL", OccurredAt = now.AddDays(-daysAgo), FetchedAt = now, TransactionCode = code };

    private static PaymentMatch M(BankTransaction t, PaymentMatchStatus s, PaymentMatchLayer layer = PaymentMatchLayer.None, string? evidence = null,
        Guid? proposed = null, Guid? actual = null, Guid? payment = null) => new()
    { Id = Guid.NewGuid(), LicenseId = t.LicenseId, BankTransactionId = t.Id, Status = s, Layer = layer, Evidence = evidence,
      ProposedWpfCustomerId = proposed, ActualWpfCustomerId = actual, PaymentId = payment, CreatedAt = t.OccurredAt, UpdatedAt = t.OccurredAt };

    [Fact]
    public async Task Son_30_gun_sayimlari_celiski_orani_gecikme_ve_katman()
    {
        using var db = NewDb();
        var lic = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        var t1 = T(lic, now, 1); var t2 = T(lic, now, 2); var t3 = T(lic, now, 3); var t4 = T(lic, now, 4); var t5 = T(lic, now, 40);
        var t6 = T(lic, now, 5, BankTransactionDirection.Outgoing); var t7 = T(lic, now, 6, code: "CCP");
        db.BankTransactions.AddRange(t1, t2, t3, t4, t5, t6, t7);
        db.PaymentMatches.AddRange(
            M(t1, PaymentMatchStatus.ConfirmedByHuman, PaymentMatchLayer.UsernameInDescription),
            M(t2, PaymentMatchStatus.Contradicted, PaymentMatchLayer.IbanMemory),
            M(t3, PaymentMatchStatus.ManualOnly),
            M(t4, PaymentMatchStatus.Proposed, PaymentMatchLayer.UsernameInDescription),
            M(t5, PaymentMatchStatus.ConfirmedByHuman, PaymentMatchLayer.UsernameInDescription),
            M(t7, PaymentMatchStatus.NoProposal, evidence: "excluded:CCP"));
        db.PaymentMatchGaps.Add(new PaymentMatchGap { Id = Guid.NewGuid(), LicenseId = lic, PaymentId = Guid.NewGuid(), Reason = PaymentMatchGapReason.NoCandidate, CreatedAt = now.AddDays(-1) });
        db.PaymentMatchGaps.Add(new PaymentMatchGap { Id = Guid.NewGuid(), LicenseId = lic, PaymentId = Guid.NewGuid(), Reason = PaymentMatchGapReason.NoCandidate, CreatedAt = now.AddDays(-2), ResolvedAt = now });
        await db.SaveChangesAsync();

        var m = await Metrics(db).ComputeAsync(lic, days: 30, CancellationToken.None);

        m.Incoming.Should().Be(5, "40 gün önceki ve giden sayılmaz");
        m.Excluded.Should().Be(1);
        m.Proposed.Should().Be(3, "Confirmed + Contradicted + bekleyen Proposed");
        m.Confirmed.Should().Be(1); m.Contradicted.Should().Be(1); m.ManualOnly.Should().Be(1); m.PendingProposals.Should().Be(1);
        m.NoProposal.Should().Be(0, "dışlanan NoProposal'dan düşülür");
        m.OpenGaps.Should().Be(1, "çözülen gap sayılmaz");
        m.ContradictionRate.Should().Be(0.5m, "1 / (1 + 1)");
        m.LagMedian.Should().Be(TimeSpan.FromDays(3), "1,2,3,4,6 gün → medyan 3");
        m.LagMax.Should().Be(TimeSpan.FromDays(6));
        m.Layers.Single(l => l.Layer == PaymentMatchLayer.UsernameInDescription).Confirmed.Should().Be(1);
        m.Layers.Single(l => l.Layer == PaymentMatchLayer.IbanMemory).Contradicted.Should().Be(1);
    }

    [Fact]
    public async Task Dislama_kanittan_degil_hareketin_kendisinden_okunur()
    {
        // Kanıt 180 günde boşaltılır: "excluded:*" kanıtı olmayan POS tahsilatı ve sıfır tutarlı hareket yine dışlanır, eşleştirme
        // sayımlarına girmez. Öneri yok satırı ancak dışlanmamış harekette NoProposal sayılır.
        using var db = NewDb();
        var lic = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        var pos = T(lic, now, 1, code: " ccp "); var zero = T(lic, now, 1, amount: 0m); var plain = T(lic, now, 1);
        db.BankTransactions.AddRange(pos, zero, plain);
        db.PaymentMatches.AddRange(M(pos, PaymentMatchStatus.NoProposal), M(zero, PaymentMatchStatus.NoProposal),
            M(plain, PaymentMatchStatus.NoProposal, evidence: "no-signal"));
        await db.SaveChangesAsync();

        var m = await Metrics(db).ComputeAsync(lic, days: 30, CancellationToken.None);

        m.Incoming.Should().Be(3);
        m.Excluded.Should().Be(2);
        m.NoProposal.Should().Be(1);
    }

    [Fact]
    public async Task Oneriden_farkli_musteriye_elle_esleme_celiski_sayilir()
    {
        // Elle eşleme öneriden farklı müşteriye ManualOnly yazar (plan); ölçümde öneri yanlıştı: çelişkidir, katmanına da
        // çelişki olarak yazılır. ManualOnly yalnız önerisi olmayan bağları sayar.
        using var db = NewDb();
        var lic = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        var overridden = T(lic, now, 1); var noProposal = T(lic, now, 2); var confirmed = T(lic, now, 3);
        db.BankTransactions.AddRange(overridden, noProposal, confirmed);
        db.PaymentMatches.AddRange(
            M(overridden, PaymentMatchStatus.ManualOnly, PaymentMatchLayer.NameAmount, proposed: Guid.NewGuid(), actual: Guid.NewGuid()),
            M(noProposal, PaymentMatchStatus.ManualOnly, actual: Guid.NewGuid()),
            M(confirmed, PaymentMatchStatus.ConfirmedByHuman, PaymentMatchLayer.NameAmount));
        await db.SaveChangesAsync();

        var m = await Metrics(db).ComputeAsync(lic, days: 30, CancellationToken.None);

        m.Contradicted.Should().Be(1); m.ManualOnly.Should().Be(1); m.Confirmed.Should().Be(1);
        m.Proposed.Should().Be(2, "iki harekete öneri üretilmişti");
        m.Linked.Should().Be(3);
        m.ContradictionRate.Should().Be(0.5m);
        var layer = m.Layers.Single(l => l.Layer == PaymentMatchLayer.NameAmount);
        layer.Confirmed.Should().Be(1); layer.Contradicted.Should().Be(1);
    }

    [Fact]
    public async Task Pencere_hareketin_anina_gore_cizilir_satirin_yaratilma_anina_gore_degil()
    {
        // İlk çekim 90 günü geri doldurur: eski hareketin önerisi bugün yazılır. O satır 30 günlük pencereye girmez; penceredeki
        // hareketin eski tarihli satırı da dışarıda kalmaz.
        using var db = NewDb();
        var lic = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        var old = T(lic, now, 60); var recent = T(lic, now, 2);
        db.BankTransactions.AddRange(old, recent);
        var backfilled = M(old, PaymentMatchStatus.ConfirmedByHuman, PaymentMatchLayer.IbanMemory); backfilled.CreatedAt = now;
        var early = M(recent, PaymentMatchStatus.Contradicted, PaymentMatchLayer.IbanMemory); early.CreatedAt = now.AddDays(-45);
        db.PaymentMatches.AddRange(backfilled, early);
        await db.SaveChangesAsync();

        var m = await Metrics(db).ComputeAsync(lic, days: 30, CancellationToken.None);

        m.Incoming.Should().Be(1);
        m.Confirmed.Should().Be(0); m.Contradicted.Should().Be(1);
    }

    [Fact]
    public async Task Gecikme_medyani_cift_sayida_ortalama_yedi_gunu_asan_ve_negatif_fark_sayim_disi()
    {
        // İlk çekim 90 günü geri doldurur: o hareketlerin çekim farkı günlerce sürer ve maksimumu şişirirdi; saat kayması da
        // negatif fark üretebilir. İkisi de gecikme ölçümünden düşer, gelen sayımında kalır.
        using var db = NewDb();
        var lic = Guid.NewGuid(); var allCapped = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        BankTransaction Lag(Guid license, TimeSpan occurredAgo, TimeSpan lag)
        {
            var t = T(license, now, 0);
            t.OccurredAt = now - occurredAgo; t.FetchedAt = t.OccurredAt + lag;
            return t;
        }
        db.BankTransactions.AddRange(
            Lag(lic, TimeSpan.FromHours(1), TimeSpan.FromHours(1)),
            Lag(lic, TimeSpan.FromHours(3), TimeSpan.FromHours(3)),
            Lag(lic, TimeSpan.FromDays(10), TimeSpan.FromDays(10)),
            Lag(lic, TimeSpan.FromHours(2), TimeSpan.FromHours(-1)),
            Lag(allCapped, TimeSpan.FromDays(10), TimeSpan.FromDays(10)),
            Lag(allCapped, TimeSpan.FromHours(2), TimeSpan.FromHours(-1)));
        await db.SaveChangesAsync();

        var m = await Metrics(db).ComputeAsync(lic, days: 30, CancellationToken.None);

        m.Incoming.Should().Be(4, "sayım dışı gecikmeli hareket yine gelendir");
        m.LagMedian.Should().Be(TimeSpan.FromHours(2), "çift sayı: 1 sa ile 3 sa ortalaması");
        m.LagMax.Should().Be(TimeSpan.FromHours(3), "10 günlük geri doldurma farkı maksimuma girmez");

        var capped = await Metrics(db).ComputeAsync(allCapped, days: 30, CancellationToken.None);

        capped.Incoming.Should().Be(2);
        capped.LagMedian.Should().BeNull("sayılabilecek gecikme yok");
        capped.LagMax.Should().BeNull();
    }

    [Theory]
    [InlineData(199, 0, 0, 0, false)]  // 199 dekont onaylı karar: örneklem eksik
    [InlineData(196, 4, 0, 0, true)]   // 4 / 200 = tam %2
    [InlineData(195, 5, 0, 0, false)]  // 5 / 200 = %2,5
    [InlineData(240, 5, 0, 0, false)]  // 5 / 245 = %2,04: yuvarlı oran 0,020 olsa da eşik sağlanmaz
    [InlineData(0, 0, 1000, 0, false)] // yalnız sayfa kararı: öneriye bakılarak verildi, eşiği hiç sağlamaz
    [InlineData(150, 0, 60, 0, false)] // sayfa kararları dekont onaylı örneklemi tamamlamaz
    [InlineData(196, 4, 0, 30, true)]  // sayfa çelişkisi dekont onaylı orana girmez (ayrı gösterilir)
    public void Faz2_esigi_yalniz_dekont_onayli_kararlarla_tam_sayilardan_karar_verir(int receiptConfirmed, int receiptContradicted,
        int pageConfirmed, int pageContradicted, bool expected)
    {
        var confirmed = receiptConfirmed + pageConfirmed; var contradicted = receiptContradicted + pageContradicted;
        var s = new PaymentMatchSummary(
            Incoming: confirmed + contradicted, Excluded: 0, Proposed: confirmed + contradicted,
            Confirmed: confirmed, Contradicted: contradicted, ManualOnly: 0,
            ReceiptConfirmed: receiptConfirmed, ReceiptContradicted: receiptContradicted, PendingProposals: 0, NoProposal: 0, OpenGaps: 0,
            ContradictionRate: PaymentMatchMetrics.Rate(contradicted, confirmed + contradicted),
            LagMedian: null, LagMax: null, Layers: Array.Empty<LayerStat>(), Platforms: Array.Empty<PlatformStat>());

        s.MeetsPhase2Threshold.Should().Be(expected);
        s.ReceiptDecisions.Should().Be(receiptConfirmed + receiptContradicted);
        (s.PageConfirmed, s.PageContradicted).Should().Be((pageConfirmed, pageContradicted));
    }

    [Fact]
    public async Task Oneri_kararlari_dekont_onayli_ve_yalniz_sayfa_olarak_ayrilir()
    {
        // Panelde dekont onayı öneri görülmeden verilir: bağımsız kanıttır. Sayfadaki elle eşleme öneriye bakarak verilir.
        // Satır bir dekonta bağlıysa (PaymentId) karar dekont onaylıdır; aynı müşteriye elle verilmiş karara sonradan
        // iliştirilen dekont da: dekontun müşterisi satırın gerçek müşterisidir. Önerisiz bağ ve dışlanan hareket öneri
        // kararı değildir.
        using var db = NewDb();
        var lic = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        var txs = Enumerable.Range(1, 6).Select(i => T(lic, now, i)).ToArray();
        var pos = T(lic, now, 1, code: "CCP");
        db.BankTransactions.AddRange(txs); db.BankTransactions.Add(pos);
        db.PaymentMatches.AddRange(
            M(txs[0], PaymentMatchStatus.ConfirmedByHuman, PaymentMatchLayer.UsernameInDescription, proposed: a, actual: a, payment: Guid.NewGuid()),
            M(txs[1], PaymentMatchStatus.Contradicted, PaymentMatchLayer.IbanMemory, proposed: a, actual: b, payment: Guid.NewGuid()),
            M(txs[2], PaymentMatchStatus.ManualOnly, PaymentMatchLayer.NameAmount, proposed: a, actual: b, payment: Guid.NewGuid()),
            M(txs[3], PaymentMatchStatus.ConfirmedByHuman, PaymentMatchLayer.UsernameInDescription, proposed: a, actual: a),
            M(txs[4], PaymentMatchStatus.ManualOnly, PaymentMatchLayer.UsernameInDescription, proposed: a, actual: b),
            M(txs[5], PaymentMatchStatus.ManualOnly, actual: b, payment: Guid.NewGuid()),
            M(pos, PaymentMatchStatus.ConfirmedByHuman, PaymentMatchLayer.UsernameInDescription, proposed: a, actual: a, payment: Guid.NewGuid()));
        await db.SaveChangesAsync();

        var m = await Metrics(db).ComputeAsync(lic, days: 30, CancellationToken.None);

        (m.Confirmed, m.Contradicted, m.ManualOnly).Should().Be((2, 3, 1));
        (m.ReceiptConfirmed, m.ReceiptContradicted).Should().Be((1, 2), "öneriden farklı elle eşlemeye iliştirilen dekont çelişkidir");
        (m.PageConfirmed, m.PageContradicted).Should().Be((1, 1));
        m.ReceiptDecisions.Should().Be(3, "önerisiz bağ ve dışlanan hareket öneri kararı değildir");
    }

    [Fact]
    public async Task Platform_kirilimi_gercek_musterinin_platformuna_gore_bilinmeyen_ve_silinen_soru_isareti()
    {
        using var db = NewDb();
        var lic = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        WpfCustomerProjection C(string platform, bool purged = false, Guid? license = null)
        {
            var c = new WpfCustomerProjection { Id = Guid.NewGuid(), LicenseId = license ?? lic, Platform = platform,
                Username = $"u-{Guid.NewGuid():N}", UpdatedAt = now, PurgedAt = purged ? now : null };
            db.WpfCustomerProjections.Add(c);
            return c;
        }
        var yt = C("youtube"); var ig = C("instagram"); var tt = C("tiktok"); var gone = C("instagram", purged: true);
        var foreign = C("facebook", license: Guid.NewGuid());
        var txs = Enumerable.Range(1, 9).Select(i => T(lic, now, i % 5 + 1)).ToArray();
        db.BankTransactions.AddRange(txs);
        db.PaymentMatches.AddRange(
            M(txs[0], PaymentMatchStatus.ConfirmedByHuman, PaymentMatchLayer.IbanMemory, proposed: yt.Id, actual: yt.Id),
            M(txs[1], PaymentMatchStatus.ConfirmedByHuman, PaymentMatchLayer.UsernameInDescription, proposed: ig.Id, actual: ig.Id),
            M(txs[2], PaymentMatchStatus.Contradicted, PaymentMatchLayer.UsernameInDescription, proposed: tt.Id, actual: ig.Id),
            M(txs[3], PaymentMatchStatus.ManualOnly, actual: tt.Id),
            M(txs[4], PaymentMatchStatus.ManualOnly, PaymentMatchLayer.UsernameInDescription, proposed: ig.Id, actual: tt.Id),
            M(txs[5], PaymentMatchStatus.ConfirmedByHuman, PaymentMatchLayer.UsernameInDescription, proposed: gone.Id, actual: gone.Id),
            M(txs[6], PaymentMatchStatus.ConfirmedByHuman, PaymentMatchLayer.UsernameInDescription, proposed: foreign.Id, actual: foreign.Id),
            M(txs[7], PaymentMatchStatus.ManualOnly, actual: Guid.NewGuid()),
            M(txs[8], PaymentMatchStatus.Proposed, PaymentMatchLayer.UsernameInDescription, proposed: yt.Id));
        await db.SaveChangesAsync();

        var m = await Metrics(db).ComputeAsync(lic, days: 30, CancellationToken.None);

        m.Platforms.Select(p => p.Platform).Should().BeEquivalentTo(new[] { "youtube", "instagram", "tiktok", PaymentMatchMetrics.UnknownPlatform },
            "bekleyen öneri karar değildir; silinen, başka lisansın ve bilinmeyen müşteri '?' altında");
        var youtube = m.Platforms.Single(p => p.Platform == "youtube");
        (youtube.Confirmed, youtube.Contradicted, youtube.ManualOnly).Should().Be((1, 0, 0));
        youtube.ContradictionRate.Should().Be(0m);
        var instagram = m.Platforms.Single(p => p.Platform == "instagram");
        (instagram.Confirmed, instagram.Contradicted, instagram.ManualOnly).Should().Be((1, 1, 0));
        instagram.ContradictionRate.Should().Be(0.5m);
        var tiktok = m.Platforms.Single(p => p.Platform == "tiktok");
        (tiktok.Confirmed, tiktok.Contradicted, tiktok.ManualOnly).Should().Be((0, 1, 1), "öneriden farklı elle eşleme çelişkidir");
        tiktok.ContradictionRate.Should().Be(1m);
        var unknown = m.Platforms.Single(p => p.Platform == PaymentMatchMetrics.UnknownPlatform);
        (unknown.Confirmed, unknown.Contradicted, unknown.ManualOnly).Should().Be((2, 0, 1));
        unknown.ContradictionRate.Should().Be(0m);
        m.Platforms.Sum(p => p.Linked).Should().Be(m.Linked);
    }
}
