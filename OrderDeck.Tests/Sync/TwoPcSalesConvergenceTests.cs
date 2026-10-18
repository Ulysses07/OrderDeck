using FluentAssertions;
using Microsoft.Data.Sqlite;
using OrderDeck.App.Services.Sales;
using OrderDeck.App.Services.Sync;
using OrderDeck.Core.Payments;
using OrderDeck.Core.Sales;
using OrderDeck.Core.Sessions;
using OrderDeck.Core.Storage.Repositories;
using Xunit;

namespace OrderDeck.Tests.Sync;

/// <summary>
/// İki bilgisayar, tek (sahte) sunucu: yayın ve etiket senkronunun uçtan uca yakınsaması (spec 2026-10-13 §10.3
/// <c>TwoPcSalesConvergenceTests</c>, §10.4 saha kontrollerinin otomatik karşılıkları; W2-12). Desen
/// <see cref="TwoPcConvergenceTests"/>: her bilgisayar gerçek yığınını koşturur (<see cref="SalesPc"/> — kendi SQLite'ı,
/// gerçek gönderim, akış, kip adımı, komut istemcisi, bekleyen komut göndericisi, düzenleme yönlendiricisi, kargo ve
/// geri doldurma servisleri); sunucu <see cref="FakeSalesServer"/>, kuralları tel biçiminde ve sunucudan bağımsız
/// yazılmış ikiz (K1–K7, açılış penceresi, komutlar, akış). Her turdan sonra sahte sunucunun işleyemediği istek
/// olmadığı doğrulanır.
///
/// <para><b>Permütasyonlar:</b> her senaryo dört kez koşar — iki bilgisayarın tur sırası (A önce, B önce) × akış sayfası
/// (sınırsız, sayfa başına bir öğe: yan liste ve çok sayfalı yetişme). Senaryonun nedensel adımları (A yayını bitirir,
/// B iptal eder…) sabittir; "tur" adımlarında iki bilgisayar permütasyonun sırasıyla döner.</para>
///
/// <para><b>Tur adımı</b> (<see cref="SalesPc.BackgroundRoundAsync"/>): bir bilgisayarın bütün arka plan sürücüleri
/// uygulamadaki gibi birer kez — satış akışı turu, oturum ve etiket gönderimi, kargo gönderimi, geri doldurma.</para>
///
/// <para><b>Yakınsama</b> (<see cref="AssertConvergedAsync"/>): sunucunun akıştaki her siparişi, onu taşıyan her
/// bilgisayarda birim birim aynı (fiyat, iptal, baskı, yedek, kargo bağı, ödenen fiyat, sürüm); iki bilgisayarın ortak
/// etiketleri ve kargo aynaları aynı; değişmezler DM3, DM5, DM7 ve DM8 tutuyor (<see cref="AssertInvariants"/>);
/// fazladan bir tur hiçbir yerel satırı ve hiçbir sunucu satırını değiştirmez (aynı akış öğesi iki kez → sıfır
/// fark).</para>
/// </summary>
public sealed class TwoPcSalesConvergenceTests : IDisposable
{
    private readonly FakeSalesServer _server = new() { DeriveLiveSessions = true, MinClientVersion = "0.9.11" };
    private readonly List<IDisposable> _owned = new();
    private readonly List<string> _files = new();
    private bool _bFirst;

    public void Dispose()
    {
        _server.Dispose();
        foreach (var d in _owned) d.Dispose();
        SqliteConnection.ClearAllPools();
        foreach (var p in _files)
            foreach (var f in new[] { p, p + ".bak", p + ".tmp" })
                try { File.Delete(f); } catch { /* en iyi çaba */ }
    }

    /// <summary>Tur sırası × akış sayfası.</summary>
    public static TheoryData<string> Orders => new() { "AB", "BA", "AB-sayfa1", "BA-sayfa1" };

    private void Use(string order)
    {
        _bFirst = order.StartsWith("BA", StringComparison.Ordinal);
        if (order.EndsWith("sayfa1", StringComparison.Ordinal)) _server.PageLimit = 1;
    }

    // ── kurulum ───────────────────────────────────────────────────────────

    private SalesPc NewPc(string name) => SalesPc.New(_server, name, _owned, _files);

    /// <summary>İki bilgisayar; ikisi de mod Off'ta bir tur döner (hello + state: kopya satırları mod açılmadan önce
    /// doğar — açılış penceresinin 1. koşulu). <paramref name="modeOn"/> ise sunucu modu açar ve iki bilgisayar açılış
    /// turunu bitirir.</summary>
    private async Task<(SalesPc A, SalesPc B)> PcsAsync(bool modeOn = true)
    {
        var a = NewPc("PC-A");
        var b = NewPc("PC-B");
        (await a.TourAsync()).Should().Be(SalesPullOutcome.ModeOff);
        (await b.TourAsync()).Should().Be(SalesPullOutcome.ModeOff);
        if (modeOn) await EnableAsync(a, b);
        return (a, b);
    }

    /// <summary><c>sales-sync enable --apply</c>: mod On, değişim anı, lisans geneli kargo ayarı; ardından verilen
    /// bilgisayarlar açılış turunu bitirir.</summary>
    private async Task EnableAsync(params SalesPc[] pcs)
    {
        _server.Mode = 1;
        _server.ModeChangedAt = DateTimeOffset.UtcNow;
        _server.Shipping = new FakeSalesServer.ShippingBody(Version: 2, FreeShippingThreshold: 750m, Fee: 60m);
        foreach (var pc in Ordered(pcs))
        {
            (await pc.SettleAsync()).Should().Be(SalesPullOutcome.CaughtUp, $"{pc.Name} mod On'u açtı");
            pc.State.GetMode().Should().Be(1);
            pc.State.Get().FirstCatchUpAt.Should().NotBeNull($"{pc.Name} ilk yetişmeyi bitirdi");
        }
    }

    private IEnumerable<SalesPc> Ordered(SalesPc[] pcs) => _bFirst ? pcs.Reverse() : pcs;

    /// <summary>Bir tur adımı: verilen bilgisayarlar permütasyonun sırasıyla, her biri bütün arka plan sürücülerinin
    /// birer turunu döner (<see cref="SalesPc.BackgroundRoundAsync"/>).</summary>
    private async Task RoundAsync(params SalesPc[] pcs)
    {
        foreach (var pc in Ordered(pcs)) await pc.BackgroundRoundAsync();
    }

    /// <summary>Bir nedensel adımın iki bilgisayara da yayılması: tur sırası ne olursa olsun iki tur adımı yeter
    /// (gönderen önce ya da sonra dönse de öbürü ikinci adımda görür).</summary>
    private async Task SyncAsync(params SalesPc[] pcs)
    {
        await RoundAsync(pcs);
        await RoundAsync(pcs);
    }

    private static Guid G(string id) => Guid.ParseExact(id, "N");

    private static string N(Guid id) => id.ToString("N");

    // ── yakınsama ─────────────────────────────────────────────────────────

    /// <summary>Sunucudaki siparişin birimleri, yerel satır biçiminde (karşılaştırma için).</summary>
    private SalesPc.LabelRow? ServerRow(string labelId)
    {
        if (_server.Order(G(labelId)) is not { } o) return null;
        var r = o.Row;
        return new SalesPc.LabelRow
        {
            Id = labelId, SessionId = r.SessionId is { } s ? N(s) : "", CustomerId = r.CustomerId, Code = r.Code,
            Price = r.Price, CancelledAt = r.CancelledAt?.ToUnixTimeSeconds(), CancelReason = r.CancelReason,
            PrintedAt = r.PrintedAt?.ToUnixTimeSeconds(), PrintBatchId = r.PrintBatchId, PrintSeq = r.PrintSeq,
            IsTentativeBackup = r.IsTentativeBackup ? 1 : 0, ShipmentId = r.ShipmentId is { } sh ? N(sh) : null,
            PaidPrice = r.PaidPrice, PriceCorrectedAt = r.PriceCorrectedAt?.ToUnixTimeSeconds(), ServerVersion = o.Version,
        };
    }

    /// <summary>
    /// Yakınsama: önce bir tur adımı daha; sonra her bilgisayarın akıştaki (devredilmiş yayınlardaki) her etiketi sunucuyla
    /// birim birim aynı, iki bilgisayarın ortak etiketleri ve kargo aynaları aynı, ve fazladan bir tur adımı hiçbir yerel
    /// satırı ve sunucu satırını değiştirmez.
    /// </summary>
    private async Task AssertConvergedAsync(params SalesPc[] pcs)
    {
        await RoundAsync(pcs);
        foreach (var pc in pcs)
        {
            pc.PendingCount.Should().Be(0, $"{pc.Name}: bekleyen komut kalmadı");
            foreach (var local in pc.Rows())
            {
                var session = pc.SessionOf(local.SessionId)!;
                if (session.ReleasedAt is null || _server.Session(G(local.SessionId))?.ReleasedAt is null) continue;
                local.SyncedAt.Should().NotBeNull($"{pc.Name}: {local.Id} gönderildi");
                (local with { SyncedAt = null }).Should().Be(ServerRow(local.Id),
                    $"{pc.Name}: devredilmiş yayının etiketi {local.Id} sunucuyla aynı");
            }
        }
        if (pcs.Length == 2)
        {
            var bRows = pcs[1].Rows().ToDictionary(r => r.Id);
            foreach (var row in pcs[0].Rows().Where(r => bRows.ContainsKey(r.Id)))
                (row with { SyncedAt = null }).Should().Be(bRows[row.Id] with { SyncedAt = null },
                    $"ortak etiket {row.Id} iki bilgisayarda aynı");
            if (pcs.All(pc => pc.State.GetMode() == 1))
                pcs[0].ShipmentRows().Should().Equal(pcs[1].ShipmentRows(), "kargo aynası iki bilgisayarda aynı");
        }

        AssertInvariants(pcs);

        var snapshots = pcs.Select(pc => pc.Snapshot()).ToList();
        var seq = _server.LastSeq;
        await RoundAsync(pcs);
        pcs.Select(pc => pc.Snapshot()).Should().Equal(snapshots, "fazladan tur: aynı öğe iki kez → sıfır fark");
        _server.LastSeq.Should().Be(seq, "fazladan tur sunucuda hiçbir satırı yazmadı");
    }

    /// <summary>
    /// Değişmezler (§3.3), yakınsamış durumda: DM3 devredilmiş yayının her siparişi akışta (<c>SessionReleasedAt</c>
    /// dolu); DM5 bir yayında bir kodun kargoya verilmemiş, iptal edilmemiş, kargo ücreti olmayan siparişlerinin tek
    /// fiyatı var; DM7 türetilmiş ve kargolanmamış dosyanın tutarı bağlı, iptal edilmemiş, geçici yedek olmayan
    /// siparişlerin toplamı; DM8 akış uygulaması gönderim kuyruğu doğurmaz — hiçbir bilgisayarda gönderilmemiş etiket
    /// yok; mod On'da gönderilmemiş yerel dosya oluşamaz (§8.4-3).
    /// </summary>
    private void AssertInvariants(SalesPc[] pcs)
    {
        var orders = _server.AllOrders();
        var shipments = _server.AllShipments().ToDictionary(s => s.Row.Id);
        foreach (var o in orders.Where(o => o.Row.SessionId is { } sid && _server.Session(sid)?.ReleasedAt is not null))
            o.SessionReleasedAt.Should().NotBeNull($"DM3: devredilmiş yayının siparişi {o.Row.Id} akışta");
        foreach (var group in orders
                     .Where(o => o.Row.CancelledAt is null && !o.Row.IsShippingFee && o.Row.Code is not null
                                 && !(o.Row.ShipmentId is { } sh && shipments.TryGetValue(sh, out var f)
                                      && f.Row.Status == "shipped"))
                     .GroupBy(o => (o.Row.SessionId, Code: o.Row.Code!.ToUpperInvariant())))
            group.Select(o => o.Row.Price).Distinct().Should().ContainSingle($"DM5: {group.Key.Code} kodunun tek fiyatı");
        foreach (var s in shipments.Values.Where(s => s.AmountDerivedAt is not null && s.Row.Status != "shipped"))
            s.Row.CumulativeAmount.Should().Be(orders
                    .Where(o => o.Row.ShipmentId == s.Row.Id && o.Row.CancelledAt is null && !o.Row.IsTentativeBackup)
                    .Sum(o => o.Row.Price),
                $"DM7: türetilmiş dosya {s.Row.Id} tutarı bağlardan");
        foreach (var pc in pcs)
        {
            pc.LabelRepo.HasUnsynced().Should().BeFalse($"{pc.Name}: gönderilmemiş etiket kalmadı (DM8)");
            if (pc.State.GetMode() == 1)
                pc.Scalar<int>("SELECT COUNT(*) FROM Shipment WHERE SyncedAt IS NULL").Should().Be(0,
                    $"{pc.Name}: mod On'da gönderilmemiş yerel dosya yok (§8.4-3)");
        }
    }

    // ── 1. A yayın yapar, bitirir; B görür (§10.4-1) ───────────────────────

    [Theory]
    [MemberData(nameof(Orders))]
    public async Task A_yayin_yapar_bitirir_B_gorur(string order)
    {
        Use(order);
        var (a, b) = await PcsAsync();
        var session = a.StartStream();
        var first = a.Sell(session, "alici_bir", "A1", 120m);
        var second = a.Sell(session, "alici_iki", "B2", 80m);
        await a.PrintAsync(first.Id, second.Id);

        await SyncAsync(a, b);

        b.Rows().Should().BeEmpty("canlı yayının etiketleri başka bilgisayara inmez (Y1)");
        b.SessionOf(session).Should().BeNull("uzak canlı yayın StreamSession'a hiç girmez (§3.2)");
        b.Sessions.GetActive().Should().BeNull();
        b.Scalar<int>("SELECT COUNT(*) FROM RemoteLiveSession WHERE Id = @session", new { session }).Should().Be(1,
            "B yalnız başlığı görür");

        a.EndStream(session);
        await SyncAsync(a, b);

        a.SessionOf(session)!.Authority.Should().Be(StreamSessionAuthority.Server, "devir sunucudan görüldü");
        var remote = b.SessionOf(session)!;
        remote.Origin.Should().Be(StreamSessionOrigin.Remote);
        remote.Authority.Should().Be(StreamSessionAuthority.Server);
        remote.ReleasedAt.Should().NotBeNull();
        b.Scalar<int>("SELECT COUNT(*) FROM RemoteLiveSession").Should().Be(0);
        b.Rows().Select(r => r.Id).Should().BeEquivalentTo([first.Id, second.Id]);
        await AssertConvergedAsync(a, b);
    }

    // ── 2. B iptal eder ve fiyat düzeltir; A görür, kâğıt çıkmaz (§10.4-2) ─────

    [Theory]
    [MemberData(nameof(Orders))]
    public async Task B_iptal_eder_ve_kod_fiyatini_duzeltir_A_gorur_iptal_edilen_kalinti_icin_kagit_cikmaz(string order)
    {
        Use(order);
        var (a, b) = await PcsAsync();
        var session = a.StartStream();
        var printed = a.Sell(session, "alici_bir", "A1", 120m);
        var residual = a.Sell(session, "alici_iki", "B2", 80m);
        var racing = a.Sell(session, "alici_uc", "C3", 60m);
        await a.PrintAsync(printed.Id);
        a.EndStream(session);
        await SyncAsync(a, b);
        a.ResidualOffer().Should().BeEquivalentTo([residual.Id, racing.Id], "ön koşul: A'nın kalıntı kuyruğu");

        var cancel = await b.Editor.CancelAsync([residual.Id, racing.Id], "musteri-vazgecti");
        cancel.HasProblems.Should().BeFalse();
        var price = await b.CorrectCodePriceAsync(session, "A1", 150m);
        price.Commanded.Should().BeTrue("devredilmiş yayının düzeltmesi komutla");
        price.Problem.Should().BeNull();

        // A iptali henüz görmeden kalıntıyı basar: kâğıt bir kez çıkar, sunucu "iptal edilmişti" der (§5.6-5).
        await a.PrintAsync(racing.Id);
        a.Papers[racing.Id].Should().Be(1);
        a.Discarded.Should().Contain(racing.Id, "\"kâğıdı imha et\"");

        await SyncAsync(a, b);

        a.Row(residual.Id)!.CancelledAt.Should().NotBeNull("B'nin iptali A'ya indi");
        a.Row(racing.Id)!.PrintedAt.Should().BeNull("iptal edilmiş siparişe baskı alınmaz");
        a.ResidualOffer().Should().BeEmpty("iptal edilen kalıntı kuyruğa teklif edilmez");
        await a.PrintAsync(residual.Id, racing.Id);
        a.Papers.Should().NotContainKey(residual.Id, "baskıdan önce yeniden okuma iptal edileni düşürür");
        a.Row(printed.Id)!.Price.Should().Be(150m);
        a.Row(printed.Id)!.PriceCorrectedAt.Should().NotBeNull("basılmış etiketin düzeltmesi işaretlenir");
        _server.Order(G(printed.Id))!.PriceSetAt.Should().NotBeNull();
        _server.Order(G(residual.Id))!.CancelSetAt.Should().NotBeNull();
        await AssertConvergedAsync(a, b);
    }

    // ── 3. B iptal eder, ardından A'nın geç push'u yok sayılır (§10.4-3) ──────

    /// <summary>
    /// A modu henüz görmemiş (açılış penceresi): mod Off kurallarıyla kendi devredilmiş yayınında kalıntıyı basar ve bir
    /// kodun fiyatını düzeltir; bu arada mod On'u görmüş B aynı kalıntıyı iptal etmiştir. A'nın geç push'unda damgalı
    /// iptal kazanır ve baskı <c>cancelled-elsewhere</c> ile yok sayılır (K2, K3, K5 ii); A <c>current</c>'ı uygular,
    /// kâğıdı imha et denir. Damgasız fiyat değişikliği alınır.
    /// </summary>
    [Theory]
    [MemberData(nameof(Orders))]
    public async Task B_iptal_eder_A_nin_gec_pushu_yok_sayilir_current_uygulanir_damgasiz_birim_alinir(string order)
    {
        Use(order);
        var (a, b) = await PcsAsync(modeOn: false);
        var session = a.StartStream();
        var residual = a.Sell(session, "alici_bir", "A1", 120m);
        var other = a.Sell(session, "alici_iki", "B2", 80m);
        await a.PrintAsync(other.Id);
        a.EndStream(session);
        await SyncAsync(a, b);
        a.SessionOf(session)!.ReleasedAt.Should().NotBeNull("mod Off'ta da kuyruk boşalınca devreder");
        a.SessionOf(session)!.Authority.Should().Be(StreamSessionAuthority.Local, "mod Off'ta Authority yerel kalır");

        _server.Mode = 1;
        _server.ModeChangedAt = DateTimeOffset.UtcNow;
        _server.Shipping = new FakeSalesServer.ShippingBody(2, 750m, 60m);
        (await b.SettleAsync()).Should().Be(SalesPullOutcome.CaughtUp, "B modu önce gördü");
        (await b.Editor.CancelAsync([residual.Id], "musteri-vazgecti")).HasProblems.Should().BeFalse();

        await a.PrintAsync(residual.Id);                                  // A modu görmeden: yerel yol
        (await a.CorrectCodePriceAsync(session, "B2", 95m)).Commanded.Should().BeFalse();
        _server.CommandCalls.Should().HaveCount(1, "A'nın yazımları komut değil (yalnız B'nin iptali)");

        (await a.SettleAsync()).Should().Be(SalesPullOutcome.CaughtUp, "açılış: pencere push'u, kip 1, ilk yetişme");

        var row = a.Row(residual.Id)!;
        row.CancelledAt.Should().NotBeNull("damgalı iptal kazanır; current uygulandı");
        row.PrintedAt.Should().BeNull("komutla iptal edilmiş siparişe baskı alınmaz (K3)");
        a.PushOutcomes.SelectMany(o => o.PrintDiscardLabelIds).Should().Contain(residual.Id, "\"kâğıdı imha et\"");
        a.ConflictCount(SalesConflictLog.PushIgnored).Should().BeGreaterThan(0);
        a.Row(other.Id)!.Price.Should().Be(95m);
        _server.Order(G(other.Id))!.Row.Price.Should().Be(95m, "damgasız fiyat açılış penceresinde push'tan alındı");
        a.SessionOf(session)!.Authority.Should().Be(StreamSessionAuthority.Server);
        await AssertConvergedAsync(a, b);
    }

    // ── 4. force-end, A aslında canlı ─────────────────────────────────────

    [Theory]
    [MemberData(nameof(Orders))]
    public async Task Force_end_A_canliyken_A_etiket_yazmaya_devam_eder_damgasizlar_alinir_damgalilar_kazanir_bitince_server(
        string order)
    {
        Use(order);
        var (a, b) = await PcsAsync();
        var session = a.StartStream();
        var l1 = a.Sell(session, "alici_bir", "A1", 100m);
        var l2 = a.Sell(session, "alici_iki", "B2", 50m);
        var l3 = a.Sell(session, "alici_uc", "C3", 70m);
        await a.PrintAsync(l1.Id, l2.Id, l3.Id);
        await SyncAsync(a, b);

        // A internetini kaybeder; 20 dk sessiz. B'nin çipinde "Yayını bitir…" çıkar, B bitirir.
        a.Offline = true;
        _server.SetReplicaLastSeen(a.Replica, DateTimeOffset.UtcNow.AddMinutes(-20));
        await b.SettleAsync();
        var chip = await b.ForceEndFromChipAsync(session);
        chip!.CanForceEnd.Should().BeTrue("sahip eşik kadar sessiz");
        _server.Session(G(session))!.ForceEndedBy.Should().Be(b.Replica.ToString());
        b.Rows().Select(r => r.Id).Should().BeEquivalentTo([l1.Id, l2.Id, l3.Id], "yayın devredildi, etiketler indi");
        (await b.Editor.CancelAsync([l1.Id], "musteri-vazgecti")).HasProblems.Should().BeFalse("B damgalı iptal");

        // A yayına devam eder (çevrimdışı): yeni etiketler, FİYAT kutusu, canlı kuyruktan iptal.
        var l4 = a.Sell(session, "alici_dort", "D4", 90m);
        var l5 = a.Sell(session, "alici_bes", "E5", 40m);
        await a.PrintAsync(l4.Id, l5.Id);
        a.Labels.CorrectPrices([l1.Id], 110m);
        a.Labels.CorrectPrices([l2.Id], 55m);
        (await a.Editor.CancelAsync([l3.Id], "stok-yok")).HasProblems.Should().BeFalse("canlı yayın: yerel yol");
        a.Sessions.GetActive()!.Id.Should().Be(session);

        a.Offline = false;
        await SyncAsync(a, b);

        a.Sessions.GetActive()!.Id.Should().Be(session, "force-end canlı yayını kesmez (§5.4)");
        a.SessionOf(session)!.Authority.Should().Be(StreamSessionAuthority.Local);
        a.SessionOf(session)!.ReleasedAt.Should().NotBeNull();
        a.RemoteSessions.Refresh();
        a.RemoteSessions.OwnSessionBand.Should().NotBeNull("kendi yayını uzaktan bitirildi bandı");
        var server1 = _server.Order(G(l1.Id))!;
        server1.Row.CancelledAt.Should().NotBeNull("B'nin damgalı iptali kazanır");
        server1.Row.Price.Should().Be(110m, "A'nın damgasız fiyatı alındı (K5 istisnası i)");
        _server.Order(G(l2.Id))!.Row.Price.Should().Be(55m);
        _server.Order(G(l3.Id))!.Row.CancelledAt.Should().NotBeNull("A'nın damgasız iptali alındı");
        _server.Order(G(l4.Id))!.SessionReleasedAt.Should().NotBeNull("devirden sonra gelen ekleme akışa girer (DM3)");
        a.Row(l1.Id)!.CancelledAt.Should().NotBeNull("ignored'ın current'ı uygulandı");

        a.EndStream(session);
        await SyncAsync(a, b);

        a.SessionOf(session)!.Authority.Should().Be(StreamSessionAuthority.Server, "A bitirince oturum server olur");
        b.Rows().Select(r => r.Id).Should().BeEquivalentTo([l1.Id, l2.Id, l3.Id, l4.Id, l5.Id]);
        await AssertConvergedAsync(a, b);
    }

    // ── 5. kalıntı kuyruk etiketinin çevrimdışı iptali ve ilk baskısı (§10.4-8) ──

    [Theory]
    [MemberData(nameof(Orders))]
    public async Task Kalinti_etiketin_cevrimdisi_iptali_ve_ilk_baskisi_bekleyen_komut_olur_tek_kagit_cikar(string order)
    {
        Use(order);
        var (a, b) = await PcsAsync();
        var session = a.StartStream();
        var dropped = a.Sell(session, "alici_bir", "A1", 120m);
        var paper = a.Sell(session, "alici_iki", "B2", 80m);
        var sold = a.Sell(session, "alici_uc", "C3", 60m);
        await a.PrintAsync(sold.Id);
        a.EndStream(session);
        await SyncAsync(a, b);
        a.ResidualOffer().Should().BeEquivalentTo([dropped.Id, paper.Id]);

        a.Offline = true;
        await a.SettleAsync();
        var cancel = await a.Editor.CancelQueueAsync([dropped.Id], "musteri-vazgecti");
        cancel.PendingLabelIds.Should().Equal(dropped.Id);
        var print = await a.PrintAsync(paper.Id);
        print.Pending.Should().Equal(paper.Id);
        a.PendingCount.Should().Be(2);
        a.ResidualOffer().Should().BeEmpty("bekleyen komutun örtüsü (§5.5)");
        await a.PrintAsync(paper.Id, dropped.Id);
        a.Papers[paper.Id].Should().Be(1, "aynı kâğıt ikinci kez teklif edilmez");
        a.Papers.Should().NotContainKey(dropped.Id, "bekleyen iptalin örtüsü baskıdan da düşürür");
        var batch = PendingBatchOf(a);

        a = a.Restart();                                                 // çevrimdışı yeniden açılış: örtü kalıcı
        a.ResidualOffer().Should().BeEmpty();
        await a.PrintAsync(paper.Id, dropped.Id);
        a.Papers.Should().BeEmpty("yeniden açılışta da teklif edilmez");

        a.Offline = false;
        await SyncAsync(a, b);

        a.PendingCount.Should().Be(0);
        _server.Order(G(dropped.Id))!.Row.CancelledAt.Should().NotBeNull("bağlantı gelince sunucuda iptal oldu");
        _server.Order(G(paper.Id))!.Row.PrintBatchId.Should().Be(batch, "tek kâğıdın basış kimliği");
        b.Row(paper.Id)!.PrintedAt.Should().NotBeNull();
        b.Row(dropped.Id)!.CancelledAt.Should().NotBeNull();
        await AssertConvergedAsync(a, b);
    }

    // ── 5b. başka bilgisayarın yayınındaki satırın kâğıdı (sahip kararı Y11) ──

    /// <summary>A yayını bitirir; B, A'nın devredilmiş yayınındaki etikete yedek ekler (<c>add-backup</c>). Yedek B'nin
    /// kuyruğundadır ve yayını A'nındır.</summary>
    private async Task<(SalesPc A, SalesPc B, string Parent, string Backup)> RemoteBackupAsync()
    {
        var (a, b) = await PcsAsync();
        var session = a.StartStream();
        var parent = a.Sell(session, "alici_bir", "A1", 120m);
        await a.PrintAsync(parent.Id);
        a.EndStream(session);
        await SyncAsync(a, b);
        var added = await b.Editor.AddBackupAsync(
            new SalesBackupRequest(parent.Id, "tiktok", "alici_yedek", "Takma Ad", "A1 bana da"));
        added.Problem.Should().BeNull();
        var backup = added.Backup!.Id;
        b.SessionOf(b.Row(backup)!.SessionId)!.Origin.Should().Be(StreamSessionOrigin.Remote, "ön koşul: A'nın yayını");
        return (a, b, parent.Id, backup);
    }

    private IReadOnlyList<Guid> MarkPrintedCommandIds() => _server.CommandCalls
        .Where(c => c.Command.Kind == "mark-printed").Select(c => c.Command.CommandId!.Value).ToList();

    /// <summary>Müşteri toplamı (W1: etiketlerden türetilir).</summary>
    private static (decimal Amount, int Labels) Totals(SalesPc pc, string labelId)
    {
        var customer = pc.Customers.GetById(pc.Row(labelId)!.CustomerId)!;
        return (customer.TotalAmount, customer.TotalLabelsPrinted);
    }

    [Theory]
    [MemberData(nameof(Orders))]
    public async Task B_A_nin_yayinindaki_yedegi_basar_baglanti_koparsa_onay_bekler_yeniden_acilista_kalir_yakinsar(
        string order)
    {
        Use(order);
        var (a, b, _, backup) = await RemoteBackupAsync();

        b.Offline = true;                                                // bağlantı kopar; B henüz bilmiyor
        b.Connectivity.IsOnline.Should().BeTrue("ön koşul: kopma fark edilmedi; bilinseydi yedek bekletilirdi (§5.8)");
        var print = await b.PrintAsync(backup);
        b.LastHeld.Should().BeEmpty();
        print.Pending.Should().Equal(backup);
        print.Notices.Should().BeEmpty("baskı onayı kaybolmaz: \"işlenemedi\" yok");
        b.Papers[backup].Should().Be(1);
        b.PendingCount.Should().Be(1);
        var sent = MarkPrintedCommandIds();

        b = b.Restart();                                                 // çevrimdışı yeniden açılış
        b.PendingCount.Should().Be(1, "bekleyen onay kalıcı");
        await b.PrintAsync(backup);
        b.Papers.Should().BeEmpty("örtü kalıcı: kâğıt ikinci kez çıkmaz");

        b.Offline = false;
        await SyncAsync(a, b);

        b.PendingCount.Should().Be(0);
        var commands = MarkPrintedCommandIds();
        commands.Distinct().Should().ContainSingle("tur aynı CommandId'yi yineler");
        _server.CommandLog(commands[0])!.Applications.Should().Be(1);
        sent.Should().BeEmpty("ön koşul: kopuk bağlantıda istek sunucuya varmadı");
        _server.Order(G(backup))!.Row.PrintedAt.Should().NotBeNull();
        a.Row(backup)!.PrintedAt.Should().NotBeNull("A'ya akışla indi");
        b.Row(backup)!.PrintedAt.Should().NotBeNull();

        // Yedek onaylanır: basılmış olduğu için müşteri toplamına girer (W2 M4: basılmamış görünseydi girmezdi).
        (await b.Editor.ConfirmBackupAsync(backup, null)).HasProblems.Should().BeFalse();
        await SyncAsync(a, b);

        Totals(a, backup).Should().Be(Totals(b, backup), "müşteri toplamı iki bilgisayarda eşit");
        Totals(b, backup).Amount.Should().Be(120m, "onaylı ve basılmış yedek toplama girer");
        await AssertConvergedAsync(a, b);
    }

    [Theory]
    [MemberData(nameof(Orders))]
    public async Task B_nin_uzak_baski_onayinin_yaniti_kaybolur_yeniden_acilistan_sonra_tur_ayni_kimlikle_yineler_bir_kez_uygulanir(
        string order)
    {
        Use(order);
        var (a, b, _, backup) = await RemoteBackupAsync();
        _server.LoseNextResponse(FakeSalesServer.Commands);           // sunucu uygular, yanıt kaybolur (zaman aşımı)

        (await b.PrintAsync(backup)).Pending.Should().Equal(backup);
        b = b.Restart();
        b.PendingCount.Should().Be(1, "sonucu bilinmeyen onay yeniden açılışta kalır");
        b.Row(backup)!.PrintedAt.Should().BeNull("yanıt kayboldu");

        await SyncAsync(a, b);

        var commands = MarkPrintedCommandIds();
        commands.Should().HaveCount(2).And.OnlyContain(id => id == commands[0], "aynı CommandId");
        _server.CommandLog(commands[0])!.Applications.Should().Be(1, "çift işaret yok");
        _server.CommandLog(commands[0])!.Replays.Should().Be(1);
        b.PendingCount.Should().Be(0);
        b.Row(backup)!.PrintedAt.Should().NotBeNull();
        a.Row(backup)!.PrintedAt.Should().NotBeNull();
        await AssertConvergedAsync(a, b);
    }

    [Theory]
    [MemberData(nameof(Orders))]
    public async Task Gonderilemeyen_uzak_baski_onayi_Off_turunda_sayili_uyariyla_duser_gonderilmez_ABORT_yok(string order)
    {
        Use(order);
        var (a, b, _, backup) = await RemoteBackupAsync();
        var settled = new List<PendingSalesCommandsSettled>();
        b.Pull.PendingCommandsSettled += (_, e) => { lock (settled) settled.Add(e); };
        b.Offline = true;                                                // bağlantı kopar; B henüz bilmiyor (§5.8)
        (await b.PrintAsync(backup)).Pending.Should().Equal(backup);

        _server.Disable(DateTimeOffset.UtcNow);
        b.Offline = false;
        await SyncAsync(a, b);

        MarkPrintedCommandIds().Should().BeEmpty("Off turu komut göndermez (sunucu mode-off döner, §4.6-1)");
        foreach (var pc in new[] { a, b })
        {
            pc.State.GetMode().Should().Be(0, $"{pc.Name} Off turunu bitirdi");
            pc.PendingCount.Should().Be(0);
            pc.Envelope.SeenMode.Should().Be(0);
        }
        settled.SelectMany(s => s.Warnings).Should().Equal(SalesCommandMessages.PrintUnrecordedModeOff(1));
        b.ConflictCount(SalesConflictLog.PendingCommandRejected).Should().Be(1, "sessiz kayıp yok: iz");
        b.Row(backup).Should().BeNull("uzak satır Off'ta silinir");
        a.Row(backup)!.PrintedAt.Should().BeNull("kayıt sunucuya ulaşmadı: yayının sahibinde basılmamış görünür");
        _server.Order(G(backup))!.Row.PrintedAt.Should().BeNull();
    }

    /// <summary>B, A'nın yayınındaki yedeği (A'ya akışla inmiş) çevrimdışı olduğunu bilirken basmaya çalışır: sahip kararı
    /// Y11 (a) — kâğıda gitmez, kuyrukta bekler, örtü de olmaz (§5.8).</summary>
    private async Task<(SalesPc A, SalesPc B, string Backup)> RemoteBackupHeldOfflineAsync()
    {
        var (a, b, _, backup) = await RemoteBackupAsync();
        await RoundAsync(a);                                             // yedek A'ya akışla iner
        a.Row(backup).Should().NotBeNull("ön koşul: A yedeği gördü");

        b.Offline = true;
        b.Connectivity.Report(SalesCommandStatus.Offline);               // B çevrimdışı olduğunu biliyor
        b.Connectivity.IsOnline.Should().BeFalse("ön koşul: bilinen çevrimdışılık");
        (await b.PrintAsync(backup)).Should().BeSameAs(SalesPrintMarkResult.Empty, "kâğıda giden satır yok");
        b.LastHeld.Should().BeEquivalentTo([backup], "başka bilgisayarın yayınının satırı bekletilir");
        b.Papers.Should().NotContainKey(backup, "kâğıt çıkmaz");
        b.PendingCount.Should().Be(0, "basılmadı: bekleyen onay ve örtü yok");
        b.Row(backup)!.PrintedAt.Should().BeNull();
        MarkPrintedCommandIds().Should().BeEmpty();
        return (a, b, backup);
    }

    /// <summary>
    /// Sahip kararı Y11 (a): B çevrimdışıyken bekletilen uzak yedek bağlantı gelince basılır; onay kalıcı bekleyen
    /// komutla gider ve hemen uygulanır. A'ya basılmış iner, A'nın kalıntı sorusunda teklif edilmez: tek kâğıt.
    /// </summary>
    [Theory]
    [MemberData(nameof(Orders))]
    public async Task B_cevrimdisiyken_A_nin_yayinindaki_yedegi_bekletir_baglaninca_basar_onay_bekleyen_komutla_gider_A_ya_teklif_edilmez(
        string order)
    {
        Use(order);
        var (a, b, backup) = await RemoteBackupHeldOfflineAsync();

        b.Offline = false;
        await SyncAsync(a, b);
        b.Connectivity.IsOnline.Should().BeTrue("ön koşul: B bağlandı");

        var print = await b.PrintAsync(backup);                          // kuyrukta bekleyen satır yeniden basılır
        b.LastHeld.Should().BeEmpty();
        print.Marked.Should().Equal(new[] { backup }, "onay bekleyen komutla gitti ve hemen uygulandı (Y11)");
        b.Papers[backup].Should().Be(1);
        b.PendingCount.Should().Be(0);
        var commands = MarkPrintedCommandIds();
        commands.Should().ContainSingle();
        _server.CommandLog(commands[0])!.Applications.Should().Be(1);

        await SyncAsync(a, b);

        a.Row(backup)!.PrintedAt.Should().NotBeNull("A'ya akışla basılmış indi");
        a.ResidualOffer().Should().NotContain(backup, "A'da kalıntı olarak teklif edilmez");
        a.Papers.Should().NotContainKey(backup, "tek kâğıt: B'ninki");
        a.Discarded.Should().BeEmpty();
        b.Discarded.Should().BeEmpty();
        await AssertConvergedAsync(a, b);
    }

    /// <summary>
    /// Risk 18'in kapanışı (sahip kararı Y11, a): B çevrimdışıyken A'nın yayınındaki yedeği bekletirken A onu yeni yayının
    /// kalıntı sorusundan basar. B hiç basmadığı için iki kâğıt çıkmaz: B bağlanınca satır basılmış iner, kuyruk düğmesi
    /// onu kâğıda göndermez, imha bildirimi de yoktur.
    /// </summary>
    [Theory]
    [MemberData(nameof(Orders))]
    public async Task B_cevrimdisiyken_yedegi_bekletirken_A_kalinti_teklifinden_basarsa_B_baglaninca_ikinci_kagit_cikmaz(
        string order)
    {
        Use(order);
        var (a, b, backup) = await RemoteBackupHeldOfflineAsync();
        var settled = new List<PendingSalesCommandsSettled>();
        b.Pull.PendingCommandsSettled += (_, e) => { lock (settled) settled.Add(e); };

        a.ResidualOffer().Should().Contain(backup, "A'da kendi yayınının basılmamış satırı");
        (await a.PrintAsync(backup)).Marked.Should().Equal(backup);
        var aBatch = a.Row(backup)!.PrintBatchId;
        aBatch.Should().NotBeNull();

        b.Offline = false;
        await SyncAsync(a, b);
        b.Row(backup)!.PrintedAt.Should().NotBeNull("A'nın basışı akışla indi");
        await b.PrintAsync(backup);                                      // B'nin kuyruğunda bekleyen satır yeniden

        b.LastHeld.Should().BeEmpty();
        b.Papers.Should().NotContainKey(backup, "basılmış satır kâğıda gitmez");
        a.Papers[backup].Should().Be(1, "tek kâğıt: A'nınki");
        MarkPrintedCommandIds().Should().ContainSingle("yalnız A'nın onayı");
        lock (settled)
        {
            settled.SelectMany(s => s.AlreadyPrintedLabelIds).Should().BeEmpty("B hiç basmadı: imha bildirimi yok");
            settled.SelectMany(s => s.Warnings).Should().BeEmpty();
        }
        b.Discarded.Should().BeEmpty();
        a.Discarded.Should().BeEmpty();
        _server.Order(G(backup))!.Row.PrintBatchId.Should().Be(aBatch);
        b.Row(backup)!.PrintBatchId.Should().Be(aBatch);
        await AssertConvergedAsync(a, b);
    }

    /// <summary>Bekleyen <c>mark-printed</c>'in basış kimliği (<see cref="PendingSalesCommandCodec"/> biçimi).</summary>
    private static string PendingBatchOf(SalesPc pc)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(pc.Scalar<string>(
            "SELECT PayloadJson FROM PendingSalesCommand WHERE Kind = 'mark-printed'"));
        return doc.RootElement.GetProperty("payload").GetProperty("batchId").GetString()!;
    }

    // ── 6. yedekten dönüş: bayat kuyruk iptali geri getirmez (§8.7) ───────────

    [Theory]
    [MemberData(nameof(Orders))]
    public async Task Yedekten_donus_mod_acildiktan_sonra_bayat_kuyruk_devredilmis_yayinda_iptali_geri_getirmez(string order)
    {
        Use(order);
        var (a, b) = await PcsAsync();
        var session = a.StartStream();
        var stale = a.Sell(session, "alici_bir", "A1", 120m);
        await a.PrintAsync(stale.Id);
        a.EndStream(session);
        var backup = a.Backup(_owned);                                   // kuyrukta: basılmış, iptalsiz, gönderilmemiş
        await SyncAsync(a, b);
        (await b.Editor.CancelAsync([stale.Id], "musteri-vazgecti")).HasProblems.Should().BeFalse();
        await SyncAsync(a, b);
        a.Row(stale.Id)!.CancelledAt.Should().NotBeNull("ön koşul");

        a = a.RestoreFrom(backup);
        a.Row(stale.Id)!.SyncedAt.Should().BeNull("ön koşul: yedeğin kuyruğunda bayat satır");
        await SyncAsync(a, b);

        a.Row(stale.Id)!.CancelledAt.Should().NotBeNull("K5: devredilmiş yayında bayat push reddedilir, current yazılır");
        _server.Order(G(stale.Id))!.Row.CancelledAt.Should().NotBeNull("sunucudaki iptal korunur");
        _server.ReplicaRow(_server.LicenseId, a.Replica)!.PreviousReplicaId.Should().NotBeNull();
        a.ConflictCount(SalesConflictLog.PushRejected).Should().BeGreaterThan(0);
        await AssertConvergedAsync(a, b);
    }

    [Theory]
    [MemberData(nameof(Orders))]
    public async Task Yedekten_donus_mod_acilmadan_alinmis_yedek_bayat_kuyrugu_On_kurallariyla_yargilanir(string order)
    {
        Use(order);
        var (a, b) = await PcsAsync(modeOn: false);
        var session = a.StartStream();
        var stale = a.Sell(session, "alici_bir", "A1", 120m);
        await a.PrintAsync(stale.Id);
        a.EndStream(session);
        var backup = a.Backup(_owned);                                   // mod Off döneminin yedeği (Mode = 0)
        await SyncAsync(a, b);
        await EnableAsync(a, b);
        (await b.Editor.CancelAsync([stale.Id], "musteri-vazgecti")).HasProblems.Should().BeFalse();
        await SyncAsync(a, b);

        a = a.RestoreFrom(backup);
        a.State.GetMode().Should().Be(0, "ön koşul: yedeğin kipi Off");
        await SyncAsync(a, b);

        a.State.GetMode().Should().Be(1, "açılış turu yeniden koştu");
        a.Row(stale.Id)!.CancelledAt.Should().NotBeNull(
            "geri yüklenmiş kopya açılış penceresi sayılmaz; K5 bayat satırı reddeder");
        _server.Order(G(stale.Id))!.Row.CancelledAt.Should().NotBeNull();
        await AssertConvergedAsync(a, b);
    }

    // ── 7. cursorReset ────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(Orders))]
    public async Task Sunucu_veritabani_kopyalaninca_cursorReset_akisi_bastan_uygular_fark_yok_sonraki_degisiklik_iner(
        string order)
    {
        Use(order);
        var (a, b) = await PcsAsync();
        var session = a.StartStream();
        var x = a.Sell(session, "alici_bir", "A1", 120m);
        var y = a.Sell(session, "alici_iki", "B2", 80m);
        await a.PrintAsync(x.Id, y.Id);
        a.EndStream(session);
        await SyncAsync(a, b);
        (await b.Editor.CancelAsync([x.Id], "musteri-vazgecti")).HasProblems.Should().BeFalse();
        await SyncAsync(a, b);
        var before = new[] { a.Snapshot(), b.Snapshot() };
        var cursorsBefore = new[] { Cursor(a), Cursor(b) };

        _server.RenumberChangeSeqs();
        cursorsBefore.Should().OnlyContain(c => c > _server.LastSeq, "ön koşul: imleçler yeni ufkun üstünde");
        await SyncAsync(a, b);

        new[] { a.Snapshot(), b.Snapshot() }.Should().Equal(before, "akış baştan uygulandı: aynı öğeler, sıfır fark");
        new[] { Cursor(a), Cursor(b) }.Should().OnlyContain(c => c <= _server.LastSeq, "imleç sıfırdan ilerledi");

        (await b.Editor.UncancelAsync([x.Id])).HasProblems.Should().BeFalse();
        await SyncAsync(a, b);
        a.Row(x.Id)!.CancelledAt.Should().BeNull("sıfırlamadan sonraki değişiklik iner");
        await AssertConvergedAsync(a, b);
    }

    // ── 8. On → Off → On ──────────────────────────────────────────────────

    /// <summary>
    /// Mod On döneminde: B, A'nın müşterisine ve kendi müşterisine dosya açar; A çevrimdışıyken kalıntısını basar
    /// (bekleyen komut), canlı yayını sessiz kalınca B bitirir ve bir etiketini damgalı iptal eder, A o etiketin fiyatını
    /// FİYAT kutusuyla değiştirir. Mod kapanır. Off'tan sonra: uzak satır yok, her bilgisayar yalnız kendi açık dosyalarını
    /// tutar (A, B'nin onun etiketleriyle açtığı dosyayı), bekleyen komut yerel yazıma çevrilmiş (kâğıt yeniden teklif
    /// edilmez), A'nın geç push'unun <c>ignored</c>'ı damgalı iptali uygulamış, kargo ayarı iki bilgisayarda aynı. Mod
    /// yeniden açılınca açılış baştan koşar ve iki bilgisayar yakınsar.
    /// </summary>
    [Theory]
    [MemberData(nameof(Orders))]
    public async Task On_Off_On_gecisleri_uzak_satir_yok_kendi_dosyalari_bekleyen_cevrilir_ignored_current_kargo_ayari_ayni(
        string order)
    {
        Use(order);
        var (a, b) = await PcsAsync();
        var sa = a.StartStream();
        var a1 = a.Sell(sa, "alici_bir", "A1", 300m);
        var a2 = a.Sell(sa, "alici_bir", "A2", 200m);
        var a3 = a.Sell(sa, "alici_iki", "A3", 70m);
        await a.PrintAsync(a1.Id, a2.Id);
        a.EndStream(sa);
        var sb = b.StartStream();
        var b1 = b.Sell(sb, "alici_dort", "B1", 40m);
        await b.PrintAsync(b1.Id);
        b.EndStream(sb);
        await SyncAsync(a, b);
        (await b.DekontAsync(a1.CustomerId)).Warning.Should().BeNull("B, A'nın müşterisine dosya açar (Y2)");
        (await b.DekontAsync(b1.CustomerId)).Warning.Should().BeNull();
        await SyncAsync(a, b);
        var fileForA = b.Row(a1.Id)!.ShipmentId!;
        var fileForB = b.Row(b1.Id)!.ShipmentId!;
        a.Row(a2.Id)!.ShipmentId.Should().Be(fileForA, "ön koşul: bağ A'ya indi");

        var s2 = a.StartStream();
        var l9 = a.Sell(s2, "alici_uc", "C9", 90m);
        await a.PrintAsync(l9.Id);
        await SyncAsync(a, b);
        a.Offline = true;
        await a.PrintAsync(a3.Id);                                       // kalıntının ilk baskısı: bekleyen komut
        a.PendingCount.Should().Be(1);
        _server.SetReplicaLastSeen(a.Replica, DateTimeOffset.UtcNow.AddMinutes(-20));
        await b.SettleAsync();
        (await b.ForceEndFromChipAsync(s2))!.CanForceEnd.Should().BeTrue();
        (await b.Editor.CancelAsync([l9.Id], "musteri-vazgecti")).HasProblems.Should().BeFalse();
        a.Labels.CorrectPrices([l9.Id], 95m);                            // A'nın canlı yolu (çevrimdışı)

        _server.Disable(DateTimeOffset.UtcNow);
        a.Offline = false;
        await SyncAsync(a, b);

        foreach (var pc in new[] { a, b })
        {
            pc.State.GetMode().Should().Be(0, $"{pc.Name} Off turunu bitirdi");
            pc.Envelope.SeenMode.Should().Be(0);
            pc.RemoteRows.Should().Be(0, "Off'ta uzak satır kalmaz (§6.5-1)");
            pc.Scalar<int>("SELECT COUNT(*) FROM RemoteLiveSession").Should().Be(0);
            pc.Scalar<int>("SELECT COUNT(*) FROM StreamSession WHERE Authority = 'server'").Should().Be(0);
            pc.PendingCount.Should().Be(0);
            pc.Settings.Shipping.FreeShippingThreshold.Should().Be(750m, "lisans geneli kargo ayarı yerele yazıldı (§6.2)");
            pc.Settings.Shipping.ShippingFee.Should().Be(60m);
        }
        OriginOf(a, fileForA).Should().Be(N(a.Replica), "B'nin A'nın etiketleriyle açtığı dosya A'ya geçti (M19)");
        a.ShipmentRepo.GetById(fileForB).Should().BeNull("A başkasının açık dosyasını tutmaz");
        b.ShipmentRepo.GetById(fileForA).Should().BeNull("B, A'nın dosyasını tutmaz");
        b.ShipmentRepo.GetById(fileForB).Should().NotBeNull("B kendi dosyasını tutar");
        a.Row(a3.Id)!.PrintedAt.Should().NotBeNull("bekleyen baskı onayı yerel yazıma çevrildi");
        a.Papers[a3.Id].Should().Be(1);
        a.ResidualOffer().Should().NotContain(a3.Id, "basılmış kâğıt yeniden teklif edilmez");
        _server.Order(G(a3.Id))!.Row.PrintedAt.Should().NotBeNull("yerel yazım push'la gitti");
        a.Row(l9.Id)!.CancelledAt.Should().NotBeNull("ignored'daki current (B'nin damgalı iptali) uygulandı");
        _server.Order(G(l9.Id))!.Row.Price.Should().Be(95m, "damgasız fiyat alındı");
        a.Sessions.GetActive()!.Id.Should().Be(s2, "uzaktan bitirilen canlı yayın A'da sürer");

        a.EndStream(s2);
        await SyncAsync(a, b);
        await EnableAsync(a, b);
        await SyncAsync(a, b);

        b.Rows().Select(r => r.Id).Should().Contain([a1.Id, a2.Id, a3.Id, l9.Id], "mod yeniden açılınca akış yeniden iner");
        a.Rows().Select(r => r.Id).Should().Contain(b1.Id);
        await AssertConvergedAsync(a, b);
    }

    // ── 9. açılış penceresi ───────────────────────────────────────────────

    /// <summary>
    /// Modu henüz görmemiş A, mod Off kurallarıyla dekont kaydeder (dosya bağlanır, "Beklet"); açılışın 1. adımı ile 2.
    /// adımı arasında bir dekont daha kaydedilir: tur kipi 0'da bırakır, sonraki tur açar ve ilk yetişme takılmaz. A'nın
    /// kargo kararları sunucuda kalır (pencere, §7.1, K4, K7). İlk yetişme sürerken A'nın yerelde devri bilinen kendi
    /// yayını <c>server</c>'dır: yerel yol seçilmez (§8.4-2).
    /// </summary>
    [Theory]
    [MemberData(nameof(Orders))]
    public async Task Acilis_penceresi_modu_gormemis_PCnin_kargo_karari_kalir_adimlar_arasi_dekont_kipi_0da_tutar_yerel_yol_secilmez(
        string order)
    {
        Use(order);
        var (a, b) = await PcsAsync(modeOn: false);
        var session = a.StartStream();
        var a1 = a.Sell(session, "alici_bir", "A1", 300m);
        var a2 = a.Sell(session, "alici_bir", "A2", 250m);
        var a3 = a.Sell(session, "alici_iki", "A3", 120m);
        await a.PrintAsync(a1.Id, a2.Id, a3.Id);
        a.EndStream(session);
        await SyncAsync(a, b);
        a.SessionOf(session)!.ReleasedAt.Should().NotBeNull("ön koşul: devir yerelde biliniyor (mod Off)");

        _server.Mode = 1;
        _server.ModeChangedAt = DateTimeOffset.UtcNow;
        _server.Shipping = new FakeSalesServer.ShippingBody(2, 750m, 60m);
        (await b.SettleAsync()).Should().Be(SalesPullOutcome.CaughtUp, "B modu önce gördü");

        (await a.DekontAsync(a1.CustomerId, ShipmentDirective.Hold)).Warning.Should().BeNull();
        var file = a.Row(a1.Id)!.ShipmentId!;
        a.ShipmentRepo.GetById(file)!.Status.Should().Be(ShipmentStatus.Held, "ön koşul: Off kuralıyla karar");

        string? lateFile = null;
        _server.DuringPush = endpoint =>
        {
            if (endpoint != FakeSalesServer.ShipmentsSync || lateFile is not null) return;
            // Kuyruk boşaltılırken kaydedilen dekont: yerel kip hâlâ 0, kargo adımı yerel yoldan, beklemeden biter.
            a.DekontAsync(a3.CustomerId, ShipmentDirective.Hold).IsCompletedSuccessfully.Should().BeTrue();
            lateFile = a.Row(a3.Id)!.ShipmentId;
        };
        (await a.TourAsync()).Should().Be(SalesPullOutcome.Opening, "adımlar arasında kaydedilen dekont kipi 0'da tutar");
        _server.DuringPush = null;
        lateFile.Should().NotBeNull();
        a.State.GetMode().Should().Be(0);

        SalesEditRoute? routeDuringCatchUp = null;
        _server.OnChanges = _ => routeDuringCatchUp ??= a.Editor.Route([a1.Id]).Single().Route;
        (await a.SettleAsync()).Should().Be(SalesPullOutcome.CaughtUp, "ilk yetişme takılmaz");
        _server.OnChanges = null;

        routeDuringCatchUp.Should().Be(SalesEditRoute.Command,
            "devri yerelde bilinen kendi yayını 2. adımda server oldu; ilk yetişme sürerken yerel yol seçilmez");
        a.State.Get().FirstCatchUpAt.Should().NotBeNull();
        var serverFile = _server.Shipment(G(file))!;
        serverFile.Row.Status.Should().Be("held", "modu görmemiş PC'nin kararı kaybolmaz");
        serverFile.Row.CumulativeAmount.Should().Be(550m, "tutar bağlardan türetildi (K4 pencere bağı + K7)");
        serverFile.AmountDerivedAt.Should().NotBeNull();
        serverFile.CommandedAt.Should().BeNull();
        serverFile.OriginReplicaId.Should().Be(a.Replica);
        var serverLate = _server.Shipment(G(lateFile!))!;
        serverLate.Row.Status.Should().Be("held");
        serverLate.Row.CumulativeAmount.Should().Be(120m);
        await SyncAsync(a, b);
        b.ShipmentRepo.GetById(file)!.Status.Should().Be(ShipmentStatus.Held);
        b.ShipmentRepo.GetById(file)!.CumulativeAmount.Should().Be(550m);
        await AssertConvergedAsync(a, b);
    }

    // ── 10. On → Off, PC-B'nin açtığı PC-A etiketli dosya ─────────────────

    [Theory]
    [MemberData(nameof(Orders))]
    public async Task On_Off_B_nin_actigi_A_etiketli_dosyayi_A_tutar_sonraki_dekontta_esik_dogru_havuzdan(string order)
    {
        Use(order);
        var (a, b) = await PcsAsync();
        var sa = a.StartStream();
        var a1 = a.Sell(sa, "alici_bir", "A1", 300m);
        var a2 = a.Sell(sa, "alici_bir", "A2", 250m);
        await a.PrintAsync(a1.Id, a2.Id);
        a.EndStream(sa);
        await SyncAsync(a, b);
        (await b.DekontAsync(a1.CustomerId)).Warning.Should().BeNull();
        await SyncAsync(a, b);
        var file = b.Row(a1.Id)!.ShipmentId!;
        _server.Shipment(G(file))!.OriginReplicaId.Should().Be(b.Replica, "ön koşul: dosyayı B açtı");

        _server.Disable(DateTimeOffset.UtcNow);
        await SyncAsync(a, b);

        OriginOf(a, file).Should().Be(N(a.Replica), "Off'ta dosyayı A tutar");
        b.ShipmentRepo.GetById(file).Should().BeNull("B dosyayı tutmaz");
        var s2 = a.StartStream();
        var a3 = a.Sell(s2, "alici_bir", "A3", 300m);
        await a.PrintAsync(a3.Id);
        a.EndStream(s2);
        var step = await a.DekontAsync(a1.CustomerId);

        step.Warning.Should().BeNull();
        a.Row(a3.Id)!.ShipmentId.Should().Be(file, "sonraki dekont dosyayı bulur");
        step.Threshold.Should().NotBeNull("havuz 850 ≥ lisans geneli eşik 750");
        step.Threshold!.PooledAmount.Should().Be(850m, "eşik doğru havuzdan");
        step.Threshold.Shipment!.Id.Should().Be(file);
        await SyncAsync(a, b);
        _server.Shipment(G(file))!.Row.CumulativeAmount.Should().Be(850m);
        b.ShipmentRepo.GetById(file).Should().BeNull();
        await AssertConvergedAsync(a, b);
    }

    // ── 11. On → Off, A kapalıyken B dosyayı kargolamış ────────────────────

    [Theory]
    [MemberData(nameof(Orders))]
    public async Task On_Off_A_kapaliyken_B_nin_kargoladigi_dosya_Shipped_iner_yeni_etiket_ona_baglanmaz(string order)
    {
        Use(order);
        var (a, b) = await PcsAsync();
        var sa = a.StartStream();
        var a1 = a.Sell(sa, "alici_bir", "A1", 300m);
        await a.PrintAsync(a1.Id);
        a.EndStream(sa);
        await SyncAsync(a, b);
        (await b.DekontAsync(a1.CustomerId)).Warning.Should().BeNull();
        await SyncAsync(a, b);
        var file = b.Row(a1.Id)!.ShipmentId!;
        a.ShipmentRepo.GetById(file)!.Status.Should().Be(ShipmentStatus.Pending);

        a.Offline = true;
        (await b.Editor.DecideShipmentAsync(file, ShipmentDecision.ShipNow)).Problem.Should().BeNull();
        _server.Disable(DateTimeOffset.UtcNow);
        a.Offline = false;
        await SyncAsync(a, b);

        a.State.GetMode().Should().Be(0);
        a.ShipmentRepo.GetById(file)!.Status.Should().Be(ShipmentStatus.Shipped, "son akış dosya öğelerini de uygular (§8.5-1)");
        var s2 = a.StartStream();
        var a2 = a.Sell(s2, "alici_bir", "A2", 100m);
        await a.PrintAsync(a2.Id);
        a.EndStream(s2);
        (await a.DekontAsync(a1.CustomerId)).Warning.Should().BeNull();
        a.Row(a2.Id)!.ShipmentId.Should().NotBe(file, "kargolanmış dosyaya yeni etiket bağlanmaz");
        await SyncAsync(a, b);
        _server.Shipment(G(file))!.Row.Status.Should().Be("shipped", "Shipped tek yönlü (§7.1)");
        await AssertConvergedAsync(a, b);
    }

    // ── 12. Off turu 2. ile 3. adım arasında kesilir ──────────────────────

    [Theory]
    [MemberData(nameof(Orders))]
    public async Task Off_turu_ikinci_ile_ucuncu_adim_arasinda_kesilirse_acilista_ucuncu_adim_kosar_o_ana_kadar_gorulen_kip_1(
        string order)
    {
        Use(order);
        var (a, b) = await PcsAsync();
        var sa = a.StartStream();
        var residual = a.Sell(sa, "alici_bir", "A1", 120m);
        var sold = a.Sell(sa, "alici_iki", "B2", 80m);
        await a.PrintAsync(sold.Id);
        a.EndStream(sa);
        await SyncAsync(a, b);
        a.Offline = true;
        (await a.Editor.CancelQueueAsync([residual.Id], "musteri-vazgecti")).PendingLabelIds.Should().Equal(residual.Id);
        a.Exec("CREATE TRIGGER TestFailCancel BEFORE UPDATE OF CancelledAt ON Label BEGIN SELECT RAISE(ABORT, 'test'); END");
        _server.Disable(DateTimeOffset.UtcNow);
        a.Offline = false;

        (await a.TourAsync()).Should().Be(SalesPullOutcome.Disabled);
        a.State.GetMode().Should().Be(0, "2. adım işlendi");
        a.PendingCount.Should().Be(1, "3. adım yarıda kaldı");
        a.Envelope.SeenMode.Should().Be(1);
        await RoundAsync(a, b);
        _server.ReplicaRow(_server.LicenseId, a.Replica)!.SeenMode.Should().Be(1, "tur bitene kadar görülen kip 1");

        a.Exec("DROP TRIGGER TestFailCancel");
        a = a.Restart();                                                 // açılış: 3. adım gönderimden önce koşar
        a.PendingCount.Should().Be(0);
        a.Envelope.SeenMode.Should().Be(0);
        await SyncAsync(a, b);

        _server.ReplicaRow(_server.LicenseId, a.Replica)!.SeenMode.Should().Be(0, "sunucu turun bittiğini görür (§8.6)");
        _server.Order(G(residual.Id))!.Row.CancelledAt.Should().NotBeNull("çevrilen iptal push'la gitti");
        await AssertConvergedAsync(a, b);
    }

    // ── 13. geri doldurma ayrışması, ardından mod On (§8.2-4, §8.7) ────────

    [Theory]
    [MemberData(nameof(Orders))]
    public async Task Geri_doldurma_ayrismasi_kayip_iptal_sunucuya_gecer_mod_Onun_ilk_turu_geri_almaz(string order)
    {
        Use(order);
        var (a, b) = await HistoryWithLostCancelAsync();
        var (session, lost) = _history;

        await SyncAsync(a, b);                                           // Ws'nin ilk açılışı: geri doldurma, onarım

        _server.Order(G(lost))!.Row.CancelledAt.Should().NotBeNull("yerel kazandı: kayıp iptal sunucuya geçti");
        a.ConflictCount(SalesConflictLog.BackfillRequeued).Should().Be(1);
        _server.ReplicaRow(_server.LicenseId, a.Replica)!.BackfillCompleted.Should().BeTrue();
        await EnableAsync(a, b);

        a.FirstCatchUps.Should().ContainSingle().Which.CatchUpOverwrites.Should().Be(0, "ilk tur onarılan değeri geri almaz");
        a.Row(lost)!.CancelledAt.Should().NotBeNull();
        await SyncAsync(a, b);
        b.Row(lost)!.CancelledAt.Should().NotBeNull();
        b.SessionOf(session)!.Origin.Should().Be(StreamSessionOrigin.Remote);
        await AssertConvergedAsync(a, b);
    }

    [Theory]
    [MemberData(nameof(Orders))]
    public async Task Geri_doldurma_ayrismasi_geri_yuklenmis_veritabaninda_sunucu_kazanir(string order)
    {
        Use(order);
        var (a, b) = await HistoryWithLostCancelAsync();
        var (_, lost) = _history;
        a = a.RestoreFrom(a.Backup(_owned));                             // aynı içerik, geri yüklenmiş kopya

        await SyncAsync(a, b);

        _server.Order(G(lost))!.Row.CancelledAt.Should().BeNull("geri yüklenmiş kopyada yerel kazanmaz (§4.3)");
        a.ConflictCount(SalesConflictLog.BackfillDiverged).Should().Be(1);
        await EnableAsync(a, b);

        a.FirstCatchUps.Should().ContainSingle().Which.CatchUpOverwrites.Should().BeGreaterThan(0,
            "ilk yetişme sunucunun değerini aldı ve uyardı (§5.4)");
        a.Row(lost)!.CancelledAt.Should().BeNull("sunucu kazanır");
        await AssertConvergedAsync(a, b);
    }

    private (string Session, string Lost) _history;

    /// <summary>
    /// Ws öncesi geçmiş: A'nın yayını mod Off'ta gönderildi ve devredildi; ardından F05 öncesinin kayıp güncellemesi —
    /// yerelde iptal edilmiş ama satır "gönderilmiş" görünüyor, sunucu iptali hiç görmedi. Geri doldurma henüz koşmadı.
    /// </summary>
    private async Task<(SalesPc A, SalesPc B)> HistoryWithLostCancelAsync()
    {
        var (a, b) = await PcsAsync(modeOn: false);
        var session = a.StartStream();
        var lost = a.Sell(session, "alici_bir", "A1", 120m);
        var kept = a.Sell(session, "alici_iki", "B2", 80m);
        await a.PrintAsync(lost.Id, kept.Id);
        a.EndStream(session);
        await a.PushAsync();
        await a.PushAsync();
        _server.Session(G(session))!.ReleasedAt.Should().NotBeNull("ön koşul: geçmiş sunucuda");
        using (var scope = OrderDeck.Core.Storage.SyncApplyScope.Begin(a.Db))
        {
            scope.Execute("UPDATE Label SET CancelledAt = @at, CancelReason = 'musteri' WHERE Id = @id",
                new { at = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), id = lost.Id });
            scope.Commit();
        }
        a.Row(lost.Id)!.SyncedAt.Should().NotBeNull("ön koşul: kayıp güncelleme kuyrukta değil");
        _history = (session, lost.Id);
        return (a, b);
    }

    // ── 14. iptal → geri al: tutar, havuz ve "Ödendi" (komut yolu, §10.4-4) ─────

    [Theory]
    [MemberData(nameof(Orders))]
    public async Task Iptal_ve_geri_alma_komutla_dosya_tutarini_havuzu_ve_Odendi_bagini_eski_haline_dondurur(string order)
    {
        Use(order);
        var (a, b) = await PcsAsync();
        var sa = a.StartStream();
        var k1 = a.Sell(sa, "alici_bir", "K1", 300m);
        var k5 = a.Sell(sa, "alici_bir", "K5", 250m);
        await a.PrintAsync(k1.Id, k5.Id);
        a.EndStream(sa);
        await SyncAsync(a, b);
        (await b.DekontAsync(k1.CustomerId)).Warning.Should().BeNull();
        await SyncAsync(a, b);
        var file = b.Row(k1.Id)!.ShipmentId!;
        var customer = k1.CustomerId;

        void Expect(decimal amount, bool cancelled)
        {
            _server.Shipment(G(file))!.Row.CumulativeAmount.Should().Be(amount);
            foreach (var pc in new[] { a, b })
            {
                pc.ShipmentRepo.GetById(file)!.CumulativeAmount.Should().Be(amount, $"{pc.Name}: dosya tutarı");
                Pool(pc, customer).Should().Be(amount, $"{pc.Name}: havuz");
                var row = pc.Row(k5.Id)!;
                (row.CancelledAt is not null).Should().Be(cancelled);
                row.ShipmentId.Should().Be(file, $"{pc.Name}: bağ kalır — \"Ödendi\" rozeti (§7.2)");
            }
        }

        Expect(550m, cancelled: false);
        (await b.Editor.CancelAsync([k5.Id], "musteri-vazgecti")).HasProblems.Should().BeFalse();
        await SyncAsync(a, b);
        Expect(300m, cancelled: true);
        (await b.Editor.UncancelAsync([k5.Id])).HasProblems.Should().BeFalse();
        await SyncAsync(a, b);
        Expect(550m, cancelled: false);
        await AssertConvergedAsync(a, b);
    }

    /// <summary>Yerel kargo aynasındaki dosyanın kaynak kopyası.</summary>
    private static string? OriginOf(SalesPc pc, string shipmentId) =>
        pc.Scalar<string?>("SELECT OriginReplicaId FROM Shipment WHERE Id = @shipmentId", new { shipmentId });

    private static decimal Pool(SalesPc pc, string customerId) => pc.Scalar<decimal>(
        "SELECT COALESCE(SUM(CumulativeAmount), 0) FROM Shipment WHERE CustomerId = @customerId AND Status IN ('Pending', 'Held')",
        new { customerId });

    // ── 15. mod Off'ta devredilmiş kendi yayını: yerel yol ─────────────────

    [Theory]
    [MemberData(nameof(Orders))]
    public async Task Mod_Offta_devredilmis_kendi_yayininda_iptal_fiyat_ve_kalinti_baski_yerel_yoldan_calisir(string order)
    {
        Use(order);
        var (a, b) = await PcsAsync(modeOn: false);
        var sa = a.StartStream();
        var x = a.Sell(sa, "alici_bir", "A1", 120m);
        var y = a.Sell(sa, "alici_iki", "B2", 80m);
        var residual = a.Sell(sa, "alici_uc", "C3", 60m);
        await a.PrintAsync(x.Id, y.Id);
        a.EndStream(sa);
        await SyncAsync(a, b);
        a.SessionOf(sa)!.ReleasedAt.Should().NotBeNull("ön koşul: devredildi (mod Off)");

        a.Editor.Route([x.Id, y.Id, residual.Id]).Should().OnlyContain(d => d.Route == SalesEditRoute.Local);
        a.Offline = true;                                                // ağ yokken de: hiçbir yazım ağı beklemez
        var cancel = a.Editor.CancelAsync([x.Id], "musteri-vazgecti");
        var price = a.CorrectCodePriceAsync(sa, "B2", 95m);
        var print = a.Editor.MarkPrintedAsync([residual.Id]);
        foreach (var task in new Task[] { cancel, price, print })
            task.IsCompletedSuccessfully.Should().BeTrue("mod Off: bugünkü yerel çağrı, çağıranın iş parçacığında, beklemeden");
        (await cancel).Applied.Should().Equal(x.Id);
        (await price).Commanded.Should().BeFalse();
        (await price).Local.Should().NotBeNull();
        (await print).Pending.Should().BeEmpty();
        a.Row(residual.Id)!.PrintedAt.Should().NotBeNull("yerel yazım hemen; ağ beklenmez");
        a.Row(x.Id)!.CancelledAt.Should().NotBeNull();
        a.Row(y.Id)!.Price.Should().Be(95m);
        _server.CommandCalls.Should().BeEmpty("mod Off'ta komut yok");
        a.PendingCount.Should().Be(0);
        a.Offline = false;

        await SyncAsync(a, b);

        _server.Order(G(x.Id))!.Row.CancelledAt.Should().NotBeNull("yerel yazım push'la gitti");
        _server.Order(G(y.Id))!.Row.Price.Should().Be(95m);
        _server.Order(G(residual.Id))!.Row.PrintedAt.Should().NotBeNull();
        b.Rows().Should().BeEmpty("mod Off'ta akış uygulanmaz");
        await AssertConvergedAsync(a, b);
    }

    // ── 16. yayın sırasında internet kesintisi (§10.4-10, -11) ─────────────

    [Theory]
    [MemberData(nameof(Orders))]
    public async Task Yayin_sirasinda_2_dk_ve_20_dk_kesinti_B_esikten_sonra_bitirir_baglanti_gelince_kayip_ve_cift_yok(
        string order)
    {
        Use(order);
        var (a, b) = await PcsAsync();
        var session = a.StartStream();
        var n = 0;
        List<string> Sell(int count) =>
            Enumerable.Range(0, count).Select(_ => a.Sell(session, $"alici_{++n}", $"K{n}", 40m + n).Id).ToList();
        await a.PrintAsync(Sell(3).ToArray());
        await SyncAsync(a, b);
        AssertSessionOnServer(a, session, 3);

        // 2 dk kesinti: etiket yazma, baskı, kuyruktan çıkarma ve FİYAT kutusu çalışır; B bitiremez.
        a.Offline = true;
        _server.SetReplicaLastSeen(a.Replica, DateTimeOffset.UtcNow.AddMinutes(-2));
        var shortOutage = Sell(4);
        await a.PrintAsync(shortOutage[0], shortOutage[1]);
        (await a.Editor.CancelQueueAsync([shortOutage[2]], "musteri-vazgecti")).PendingLabelIds.Should().BeEmpty(
            "canlı yayın: yerel yol");
        a.Labels.CorrectPrices([shortOutage[3]], 75m);
        await RoundAsync(a, b);
        (await b.ForceEndFromChipAsync(session))!.CanForceEnd.Should().BeFalse("sahip eşikten kısa sessiz (Y7)");
        _server.Session(G(session))!.ReleasedAt.Should().BeNull();
        a.Offline = false;
        await SyncAsync(a, b);
        AssertSessionOnServer(a, session, 7);
        b.Rows().Should().BeEmpty("yayın hâlâ canlı");

        // 20 dk kesinti: B eşikten sonra bitirir, A yayına devam eder.
        a.Offline = true;
        _server.SetReplicaLastSeen(a.Replica, DateTimeOffset.UtcNow.AddMinutes(-20));
        var longOutage = Sell(3);
        await a.PrintAsync(longOutage[0], longOutage[1]);
        (await a.Editor.CancelQueueAsync([longOutage[2]], "musteri-vazgecti")).HasProblems.Should().BeFalse();
        await b.SettleAsync();
        (await b.ForceEndFromChipAsync(session))!.CanForceEnd.Should().BeTrue();
        b.Rows().Should().HaveCount(7, "sunucunun bildiği etiketler devirle indi");
        var afterForceEnd = Sell(2);
        await a.PrintAsync(afterForceEnd.ToArray());
        a.Offline = false;
        await SyncAsync(a, b);

        AssertSessionOnServer(a, session, 12);
        a.EndStream(session);
        await SyncAsync(a, b);
        a.SessionOf(session)!.Authority.Should().Be(StreamSessionAuthority.Server);
        b.Rows().Should().HaveCount(12);
        await AssertConvergedAsync(a, b);
    }

    /// <summary>Yayının sunucudaki siparişleri yereldekilerle birebir: aynı küme (kayıp yok, çift yok), aynı birimler.</summary>
    private void AssertSessionOnServer(SalesPc pc, string sessionId, int expected)
    {
        var local = pc.Rows().Where(r => r.SessionId == sessionId).ToList();
        var server = _server.OrdersOfSession(G(sessionId));
        local.Should().HaveCount(expected);
        server.Should().HaveCount(expected, "kesintide yazılan etiketler bağlantı gelince gitti, çift yok");
        server.Select(o => N(o.Row.Id)).Should().BeEquivalentTo(local.Select(r => r.Id));
        foreach (var row in local)
        {
            row.SyncedAt.Should().NotBeNull($"{row.Id} gönderildi");
            (row with { SyncedAt = null }).Should().Be(ServerRow(row.Id), $"{row.Id} sunucuyla aynı");
        }
    }

    // ── 17. kesintide bitirilen yayın: devrediliyor (Y8) ──────────────────

    /// <summary>
    /// A yayını çevrimdışıyken bitirir; bağlantı gelince kapanış önce <c>Release = false</c> ve gönderilmemiş etiket
    /// sayısıyla gider, etiketler gönderilirken B yayını "devrediliyor, N etiket" görür, bitiremez ve düzenleyemez (Y8);
    /// kuyruk boşalınca devir olur.
    /// </summary>
    [Theory]
    [MemberData(nameof(Orders))]
    public async Task Kesintide_bitirilen_yayin_baglanti_gelince_devredilir_arada_B_devrediliyor_gorur_bitiremez(string order)
    {
        Use(order);
        var (a, b) = await PcsAsync();
        var session = a.StartStream();
        var first = a.Sell(session, "alici_bir", "A1", 100m);
        await a.PrintAsync(first.Id);
        await SyncAsync(a, b);
        a.Offline = true;
        var second = a.Sell(session, "alici_iki", "B2", 60m);
        var third = a.Sell(session, "alici_uc", "C3", 70m);
        await a.PrintAsync(second.Id, third.Id);
        a.EndStream(session);
        _server.SetReplicaLastSeen(a.Replica, DateTimeOffset.UtcNow.AddMinutes(-20));

        var hold = _server.Hold(FakeSalesServer.OrdersSync);
        a.Offline = false;
        var tour = a.TourAsync();
        await hold.Arrived.WaitAsync(TimeSpan.FromSeconds(30));
        _server.Session(G(session))!.EndedAt.Should().NotBeNull("kapanış önce gitti");
        _server.Session(G(session))!.UnsentLabelCount.Should().Be(2);
        await b.SettleAsync();
        var chip = await b.ForceEndFromChipAsync(session);
        chip!.IsHandingOver.Should().BeTrue();
        chip.CanForceEnd.Should().BeFalse("devrediliyor yayın bitirilemez (Y8)");
        chip.Text.Should().Contain("2 etiket");
        b.Rows().Should().BeEmpty("devredilmemiş yayının etiketleri inmez: düzenlenemez");
        hold.Release();
        await tour;

        await SyncAsync(a, b);
        _server.Session(G(session))!.ReleasedAt.Should().NotBeNull("kuyruk boşalınca devredildi");
        a.SessionOf(session)!.Authority.Should().Be(StreamSessionAuthority.Server);
        b.Rows().Select(r => r.Id).Should().BeEquivalentTo([first.Id, second.Id, third.Id]);
        await AssertConvergedAsync(a, b);
    }

    private long Cursor(SalesPc pc) => pc.Scalar<long>(
        "SELECT COALESCE((SELECT Seq FROM SyncCursor WHERE Name = @name AND LicenseKey = @key), 0)",
        new { name = SalesChangesPullService.CursorName, key = _server.Lisans });
}
