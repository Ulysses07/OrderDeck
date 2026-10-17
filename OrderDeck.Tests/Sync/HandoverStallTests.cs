using FluentAssertions;
using OrderDeck.App.Services.Sync;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.Tests.Sync;

/// <summary>
/// Kendi yayınının devir durumu (W2-09; spec 2026-10-13 §5.7 "Kendi yayını devrediliyor", "Akış … Yayın X devredilemedi:
/// N etiket gönderilemiyor", dikkat sayacı; §12 risk 4). Kural: devri bekletilen yayın, sunucuya ulaşan
/// <see cref="HandoverStallTracker.StuckAfterTours"/> ardışık turda gönderilmemiş etiket taşıyor ve sayısı azalmıyorsa
/// devredilemiyor sayılır; çevrimdışı tur sayılmaz, azalan tur seriyi yeniden başlatır.
/// </summary>
public sealed class HandoverStallTests : IDisposable
{
    private readonly SalesEditTestBed _bed = new();

    public void Dispose() => _bed.Dispose();

    private static PendingHandover Held(string id, int unsent, long startedAt = 1_760_000_000L) => new(id, startedAt, unsent);

    // ── kural ───────────────────────────────────────────────────────────

    [Fact]
    public void Azalmayan_kuyruk_besinci_sunucu_turunda_devredilemiyor_sayilir()
    {
        var tracker = new HandoverStallTracker();
        for (var tour = 1; tour < HandoverStallTracker.StuckAfterTours; tour++)
            tracker.Observe([Held("a", 3)]).Should().BeEmpty($"{tour}. tur: seri henüz eşiğe varmadı");

        tracker.Observe([Held("a", 3)]).Should().Equal(new StuckHandover("a", 1_760_000_000L, 3));
        tracker.Observe([Held("a", 4)]).Should().ContainSingle("artan sayı ilerleme değildir");
    }

    [Fact]
    public void Azalan_kuyruk_seriyi_yeniden_baslatir_bosalan_ve_devredilen_seriden_cikar()
    {
        var tracker = new HandoverStallTracker();
        for (var i = 0; i < 4; i++) tracker.Observe([Held("a", 300), Held("b", 2)]);

        // Büyük kuyruk turlar boyunca azalarak boşalıyor (Y9): uyarı yok.
        tracker.Observe([Held("a", 200), Held("b", 2)]).Should().Equal(new StuckHandover("b", 1_760_000_000L, 2));
        // a'nın serisi azalan turda 1'den yeniden başladı; b listeden düştü (devredildi).
        for (var streak = 2; streak < HandoverStallTracker.StuckAfterTours; streak++)
            tracker.Observe([Held("a", 200)]).Should().BeEmpty($"a'nın serisi {streak}");
        tracker.Observe([Held("a", 200)]).Should().ContainSingle(s => s.SessionId == "a");

        tracker.Observe([Held("a", 0)]).Should().BeEmpty("kuyruk boşaldı: devir bu turda");
        tracker.Observe([Held("a", 200)]).Should().BeEmpty("seri sıfırdan");
    }

    [Fact]
    public void Sifirlama_seriyi_siler()
    {
        var tracker = new HandoverStallTracker();
        for (var i = 0; i < HandoverStallTracker.StuckAfterTours; i++) tracker.Observe([Held("a", 1)]);

        tracker.Reset();

        tracker.Observe([Held("a", 1)]).Should().BeEmpty();
    }

    // ── tur ─────────────────────────────────────────────────────────────

    /// <summary>Bu bilgisayarın bitmiş, devri bekleyen yayını ve gönderilmemiş iki etiketi (yerelde; sunucuda yok).</summary>
    private string HeldSession()
    {
        var session = _bed.LocalSession(ended: true);
        var customer = Guid.NewGuid().ToString("N");
        foreach (var _ in Enumerable.Range(0, 2))
        {
            var label = _bed.LocalLabel(session, customer, printed: false);
            _bed.Exec("UPDATE Label SET SyncedAt = NULL WHERE Id = @label", new { label });
        }
        _bed.Exec("UPDATE StreamSession SET SyncedAt = NULL WHERE Id = @session", new { session });
        return session;
    }

    [Fact]
    public async Task Etiketi_kalici_gonderilemeyen_yayin_bes_sunucu_turunda_devredilemiyor_cevrimdisi_tur_sayilmaz()
    {
        _bed.ModeOn();
        var session = HeldSession();
        var pull = _bed.CreatePull();
        new SessionRepository(_bed.Db).HasPendingHandover().Should().BeTrue("ön koşul");

        for (var tour = 1; tour <= HandoverStallTracker.StuckAfterTours; tour++)
        {
            if (tour == 3)
            {
                // Çevrimdışı tur: seri ne ilerler ne sıfırlanır.
                _bed.Server.Offline = true;
                (await pull.TourOnceAsync(CancellationToken.None)).Should().Be(SalesPullOutcome.Failed);
                pull.StuckHandovers.Should().BeEmpty();
                _bed.Server.Offline = false;
            }
            _bed.Server.EnqueueStatus(FakeSalesServer.OrdersSync, 400);      // parti kalıcı olarak düşüyor
            await pull.TourOnceAsync(CancellationToken.None);
            if (tour < HandoverStallTracker.StuckAfterTours)
                pull.StuckHandovers.Should().BeEmpty($"{tour}. sunucu turu");
        }

        pull.StuckHandovers.Should().ContainSingle(s => s.SessionId == session && s.UnsentLabels == 2);
        _bed.Server.Session(Guid.ParseExact(session, "N"))!.UnsentLabelCount.Should().Be(2,
            "devrediliyor çipinin sayısı sahibin kapanışından");

        // Etiketler sonunda gidince yayın devredilir; uyarı kalkar.
        await pull.TourOnceAsync(CancellationToken.None);
        pull.StuckHandovers.Should().BeEmpty();
        new SessionRepository(_bed.Db).HasPendingHandover().Should().BeFalse("devir görüldü");
    }

    [Fact]
    public async Task Mod_Offta_seri_ve_uyari_silinir()
    {
        _bed.ModeOn();
        HeldSession();
        var pull = _bed.CreatePull();
        for (var i = 0; i < HandoverStallTracker.StuckAfterTours; i++)
        {
            _bed.Server.EnqueueStatus(FakeSalesServer.OrdersSync, 400);
            await pull.TourOnceAsync(CancellationToken.None);
        }
        pull.StuckHandovers.Should().ContainSingle();

        _bed.ModeOff();
        (await pull.TourOnceAsync(CancellationToken.None)).Should().Be(SalesPullOutcome.ModeOff);

        pull.StuckHandovers.Should().BeEmpty("mod Off'ta satış satırı ve devir uyarısı yok");
    }

    // ── satır ve dikkat sayacı ───────────────────────────────────────────

    private static readonly SalesFeedStatus CaughtUp = new(true, new DateTimeOffset(2026, 10, 17, 18, 14, 0, TimeSpan.Zero),
        false, 0);

    [Fact]
    public void Devri_bekleyen_yayin_varken_satir_onde_devrediliyor_der()
    {
        var feed = SalesFeedStatusFormatter.Format(CaughtUp, 0)!.Value;

        var s = SalesFeedStatusFormatter.Format(CaughtUp, 0, handingOver: true, [])!.Value;

        s.Text.Should().Be($"Yayın sunucuya devrediliyor… · {feed.Text}");
        s.Healthy.Should().BeTrue("geçici durum");
        SalesFeedStatusFormatter.Format(CaughtUp, 0, handingOver: false, [])!.Value.Should().Be(feed);
        SalesFeedStatusFormatter.Format(SalesFeedStatus.Off, 0, handingOver: true, []).Should().BeNull("mod Off'ta satır yok");
    }

    [Fact]
    public void Devredilemeyen_yayin_baslangic_saatiyle_anilir_satir_sari_ipucu_destek()
    {
        var feed = SalesFeedStatusFormatter.Format(CaughtUp, 0)!.Value;
        var started = new DateTimeOffset(2026, 10, 13, 21, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();

        var s = SalesFeedStatusFormatter.Format(CaughtUp, 0, handingOver: true,
            [new StuckHandover("a", started, 3), new StuckHandover("b", started + 60, 1)], TimeZoneInfo.Utc)!.Value;

        s.Text.Should().Be($"Yayın 13 Eki 21:00 devredilemedi: 3 etiket gönderilemiyor (+1 yayın) · {feed.Text}");
        s.Healthy.Should().BeFalse();
        SalesFeedStatusFormatter.Tooltip(s, 0).Split('\n').Should().Equal(s.Text, SalesCommandMessages.HandoverStuckAdvice);
    }

    [Fact]
    public void Devredilemeyen_yayin_dikkat_sayacina_girer()
    {
        var counter = new SyncPendingCounter(new SyncOutboxRepository(_bed.Db), new SyncCursorRepository(_bed.Db),
            new PaymentRequestServiceTestHelpers.NullLicenseProvider(), () => 1);

        var attention = counter.Attention();

        attention.StuckHandovers.Should().Be(1);
        attention.Any.Should().BeTrue();
        var status = SyncStatusFormatter.Format(0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, attention);
        status.Text.Should().EndWith(" · 1 yayın devredilemedi");
        status.Healthy.Should().BeFalse();
        SyncStatusFormatter.Tooltip(status, attention).Should().EndWith(SalesCommandMessages.HandoverStuckAdvice);
        new SyncPendingCounter(new SyncOutboxRepository(_bed.Db), new SyncCursorRepository(_bed.Db),
            new PaymentRequestServiceTestHelpers.NullLicenseProvider()).Attention().Any.Should().BeFalse();
    }
}
