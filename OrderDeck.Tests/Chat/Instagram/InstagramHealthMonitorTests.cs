using FluentAssertions;
using OrderDeck.Chat.Facebook;
using OrderDeck.Chat.Ingestors.Instagram;
using OrderDeck.Tests.TestHelpers;
using Kind = OrderDeck.Chat.Ingestors.Instagram.InstagramPollKind;
using State = OrderDeck.Chat.Ingestors.Instagram.InstagramAccountState;

namespace OrderDeck.Tests.Chat.Instagram;

/// <summary>
/// Hesap başına sağlık izleyicisinin geçişleri (çoklu hesap tasarımı §8.5).
///
/// NEDEN: izleyici yalnız olguları tutar; "yanıt yok", "yayın kesildi" gibi yorumları saf sunucu bu
/// alanlardan okuma anında türetir. Bir alan yanlış anda yazılırsa (ör. <c>Since</c> her bildirimde
/// yenilenirse ya da oturum içi duruş "bu oturumda canlıydı" bilgisini silerse) susan hesap operatöre
/// sağlam görünür. Saat elle ilerletilir; ağ ve bekleme yoktur. Kimlik, token ve ad literal yazılmaz,
/// üretilir (depo herkese açık).
/// </summary>
public sealed class InstagramHealthMonitorTests
{
    private readonly ManualTime _time = new();
    private readonly InstagramHealthMonitor _monitor;

    public InstagramHealthMonitorTests() => _monitor = new InstagramHealthMonitor(_time);

    private DateTimeOffset Now => _time.GetUtcNow();

    private void Tick(double seconds) => _time.Advance(TimeSpan.FromSeconds(seconds));

    private static string New(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    private static FacebookLinkedPage NewPage() => new(New("p"), New("t"), New("Sayfa"));

    /// <summary>n Sayfa üretip izleyiciyi yapılandırır; ilki birincildir.</summary>
    private FacebookLinkedPage[] Configure(int n)
    {
        var pages = Enumerable.Range(0, n).Select(_ => NewPage()).ToArray();
        _monitor.Configure(pages);
        return pages;
    }

    /// <summary>Tek Sayfa yapılandırır ve hesap döngüsünün başladığını bildirir; PageId döner.</summary>
    private string Started()
    {
        var id = Configure(1)[0].PageId;
        _monitor.Report(id, State.Starting);
        return id;
    }

    private InstagramAccountHealth Entry(string pageId) => _monitor.Snapshot().Single(h => h.PageId == pageId);

    /// <summary>Yeni eklenmiş girdi: Stopped, Since = şimdi, öbür alanlar boş.</summary>
    private InstagramAccountHealth Fresh(FacebookLinkedPage page, bool primary)
        => new(page.PageId, page.PageName, primary, null, State.Stopped, null, Now, null, null, false);

    /// <summary>Poller'ın tur sonucu bildirimi (kota başlığı yok).</summary>
    private void Round(string pageId, Kind kind, string? message = null, TimeSpan retryAfter = default)
        => _monitor.ObserverFor(pageId).RoundCompleted(kind, message, retryAfter, null);

    [Fact]
    public void Configure_eksigi_Stopped_ekler_fazlayi_siler_kalanin_durumuna_dokunmaz_ilk_girdi_birincildir()
    {
        var pages = Configure(2);
        _monitor.Snapshot().Should().Equal(Fresh(pages[0], primary: true), Fresh(pages[1], primary: false));

        _monitor.Report(pages[1].PageId, State.Starting);
        Round(pages[1].PageId, Kind.Ok);
        var kept = Entry(pages[1].PageId);
        Tick(7);
        var added = NewPage();

        _monitor.Configure(new[] { pages[1], added }); // pages[0] çıktı, sıra değişti, yeni Sayfa girdi

        _monitor.Snapshot().Should().Equal(kept with { IsPrimary = true }, Fresh(added, primary: false));
    }

    [Fact]
    public void Configure_kalan_girdilerin_sirasini_birincil_isaretini_ve_Sayfa_adini_listeden_yeniden_yazar()
    {
        var pages = Configure(2);
        var second = Entry(pages[1].PageId);
        _monitor.Report(pages[0].PageId, State.Starting);
        Round(pages[0].PageId, Kind.Ok);
        var first = Entry(pages[0].PageId);
        Tick(7);
        var renamed = pages[1] with { PageName = New("Sayfa") };

        _monitor.Configure(new[] { renamed, pages[0] }); // ikisi de kaldı: yer değiştirdiler, birinin adı değişti

        _monitor.Snapshot().Should().Equal(
            second with { PageName = renamed.PageName, IsPrimary = true },
            first with { IsPrimary = false });
    }

    [Fact]
    public void Report_Starting_yalniz_Stopped_girdide_etkilidir()
    {
        var id = Configure(1)[0].PageId;
        Tick(3);

        _monitor.Report(id, State.Starting);
        Entry(id).Should().BeEquivalentTo(new { State = State.Starting, Since = Now });

        Round(id, Kind.Ok);
        var live = Entry(id);
        Tick(3);

        _monitor.Report(id, State.Starting); // poller yeniden kurulurken
        Entry(id).Should().Be(live);
    }

    [Fact]
    public void Yeni_turun_Starting_bildirimi_ve_SetAccount_bekleyen_hatanin_Detail_ve_RetryAt_ini_silmez()
    {
        var id = Started();
        _monitor.Report(id, State.Faulted, "sebep", Now.AddMinutes(5));
        var faulted = Entry(id);
        Tick(300);
        var username = New("u");

        _monitor.Report(id, State.Starting); // bekleme bitti, hesap döngüsü yeni tura başlıyor
        _monitor.SetAccount(id, username); // hesap yeniden çözüldü

        Entry(id).Should().Be(faulted with { Username = username });
    }

    [Fact]
    public void Tur_Ok_Live_yapar_NoBroadcast_canliliga_gore_ayrilir_Since_yalniz_durum_degisince_guncellenir()
    {
        var id = Started();

        Round(id, Kind.NoBroadcast); // henüz canlı olmadı
        Entry(id).Should().BeEquivalentTo(
            new { State = State.WaitingForBroadcast, LastContactAt = Now, WasLiveThisSession = false });

        Tick(10);
        Round(id, Kind.Ok);
        var liveAt = Now;
        Tick(30);
        Round(id, Kind.Ok); // aynı durum: Since kalır, temas yenilenir
        Entry(id).Should().BeEquivalentTo(
            new { State = State.Live, Since = liveAt, LastContactAt = Now, WasLiveThisSession = true });

        var before = Entry(id);
        Tick(5);
        _monitor.ObserverFor(id).RoundStarting();
        Round(id, Kind.Transient); // hiçbir alan değişmez
        Entry(id).Should().Be(before);

        Round(id, Kind.NoBroadcast); // BroadcastEnded yalnız canlıdan sonra
        Entry(id).Should().BeEquivalentTo(
            new { State = State.BroadcastEnded, Since = Now, LastContactAt = Now, WasLiveThisSession = true });
    }

    [Fact]
    public void Tur_RateLimited_RetryAt_Fatal_Detail_yazar_ayni_durumu_yineleyen_Report_Since_i_degistirmez()
    {
        var id = Started();
        const string reason = "Instagram bağlantını yenilemen gerekiyor.";

        Round(id, Kind.RateLimited, retryAfter: TimeSpan.FromMinutes(7));
        Entry(id).Should().BeEquivalentTo(
            new { State = State.RateLimited, RetryAt = Now.AddMinutes(7), LastContactAt = Now });

        Tick(1);
        Round(id, Kind.Fatal, reason); // tur olayı vermediği RetryAt'i temizler
        var faultedAt = Now;
        Entry(id).Should().BeEquivalentTo(
            new { State = State.Faulted, Since = faultedAt, Detail = reason, RetryAt = (DateTimeOffset?)null });

        Tick(1);
        _monitor.Report(id, State.Faulted, reason, Now.AddMinutes(5)); // çağıran RetryAt'i yazar
        Entry(id).Should().BeEquivalentTo(new { Since = faultedAt, Detail = reason, RetryAt = Now.AddMinutes(5) });
    }

    [Theory, InlineData(Kind.Ok), InlineData(Kind.NoBroadcast), InlineData(Kind.RateLimited)]
    public void Sonuc_bildiren_tur_onceki_durumun_Detail_ve_RetryAt_ini_tasimaz(Kind kind)
    {
        var id = Started();
        _monitor.Report(id, State.Faulted, "sebep", Now.AddMinutes(5)); // önceki durumun sebebi ve saati
        Tick(1);

        Round(id, kind);

        // Yeniden deneme saatini yalnız kota turu verir (süre verilmedi: şimdi); öbürleri temizler.
        DateTimeOffset? retryAt = kind == Kind.RateLimited ? Now : null;
        Entry(id).Should().BeEquivalentTo(new { Detail = (string?)null, RetryAt = retryAt });
    }

    [Fact]
    public void Tur_Fatal_son_temasi_degistirmez()
    {
        var id = Started();
        Round(id, Kind.Ok);
        var contactAt = Now;
        Tick(4);

        Round(id, Kind.Fatal, "sebep");

        Entry(id).Should().BeEquivalentTo(new { State = State.Faulted, LastContactAt = contactAt });
    }

    [Fact]
    public void Iki_Sayfanin_gozlemcileri_ayni_anda_tutulur_her_biri_yalniz_kendi_girdisini_yazar()
    {
        var pages = Configure(2);
        var first = _monitor.ObserverFor(pages[0].PageId);
        var second = _monitor.ObserverFor(pages[1].PageId);
        _monitor.Report(pages[0].PageId, State.Starting);
        _monitor.Report(pages[1].PageId, State.Starting);

        first.RoundCompleted(Kind.Ok, null, TimeSpan.Zero, null);
        second.RoundCompleted(Kind.NoBroadcast, null, TimeSpan.Zero, null);

        _monitor.Snapshot().Select(h => h.State).Should().Equal(State.Live, State.WaitingForBroadcast);
    }

    [Theory, InlineData(State.NotLinked), InlineData(State.ResolveFailed), InlineData(State.Skipped)]
    public void Cozumleme_sonucu_Report_ile_yazilir_Found_gelince_SetAccount_Starting_e_alir(State state)
    {
        var id = Started();
        var username = New("u");

        _monitor.Report(id, state, detail: "sebep", retryAt: Now.AddMinutes(5));
        Entry(id).Should().BeEquivalentTo(new { State = state, Detail = "sebep", RetryAt = Now.AddMinutes(5) });

        Tick(300);
        _monitor.SetAccount(id, username);
        Entry(id).Should().BeEquivalentTo(new
        {
            State = State.Starting,
            Since = Now,
            Username = username,
            Detail = (string?)null,
            RetryAt = (DateTimeOffset?)null,
        });
    }

    [Fact]
    public void SetAccount_oteki_durumlarda_yalniz_kullanici_adini_yazar()
    {
        var id = Started();
        Round(id, Kind.Ok);
        var before = Entry(id);
        var username = New("u");

        _monitor.SetAccount(id, username);
        Entry(id).Should().Be(before with { Username = username });

        _monitor.SetAccount(id, null);
        Entry(id).Should().Be(before);
    }

    [Fact]
    public void StopAll_silmeden_Stopped_yapar_oteki_alanlara_dokunmaz()
    {
        var id = Configure(2)[0].PageId;
        _monitor.Report(id, State.Starting);
        Round(id, Kind.Ok);
        _monitor.Report(id, State.Faulted, "sebep", Now.AddMinutes(5));
        var before = Entry(id);
        Tick(20);

        _monitor.StopAll();

        _monitor.Snapshot().Should().HaveCount(2).And.OnlyContain(h => h.State == State.Stopped);
        // Canlıydı bilgisi, son temas, Detail, RetryAt aynen.
        Entry(id).Should().Be(before with { State = State.Stopped, Since = Now });

        _monitor.Report(id, State.Starting); // liste oturum içinde yeniden doldu; Report vermediği alanı temizler
        Entry(id).Should().Be(before with { State = State.Starting, Since = Now, Detail = null, RetryAt = null });
    }

    [Fact]
    public void ResetSession_girdileri_Stopped_yapar_ve_dort_oturum_alanini_temizler()
    {
        var id = Started();
        var username = New("u");
        _monitor.SetAccount(id, username);
        Round(id, Kind.Ok);
        _monitor.Report(id, State.Faulted, "sebep", Now.AddMinutes(5));
        var before = Entry(id);
        Tick(60);

        _monitor.ResetSession();

        Entry(id).Should().Be(before with
        {
            State = State.Stopped,
            Since = Now,
            WasLiveThisSession = false,
            LastContactAt = null,
            RetryAt = null,
            Detail = null,
        });
    }

    [Fact]
    public void Zaten_durmus_girdide_StopAll_Since_e_dokunmaz_ResetSession_yeniler()
    {
        var id = Configure(1)[0].PageId;
        var configuredAt = Now;
        Tick(20);

        _monitor.StopAll(); // durum değişmedi: genel kural
        Entry(id).Since.Should().Be(configuredAt);

        _monitor.ResetSession(); // tek istisna: hep "şimdi"
        Entry(id).Since.Should().Be(Now);
    }

    [Fact]
    public void Snapshot_kopyadir_Clear_siler_bilinmeyen_Sayfa_yok_sayilir()
    {
        var id = Configure(2)[0].PageId;
        var first = _monitor.Snapshot();

        _monitor.Report(id, State.Starting);
        first.Should().OnlyContain(h => h.State == State.Stopped); // eski anlık görüntü değişmedi

        _monitor.Clear();
        _monitor.Report(id, State.Starting);
        _monitor.SetAccount(id, New("u"));
        Round(id, Kind.Ok);
        _monitor.Snapshot().Should().BeEmpty();
    }

    [Fact]
    public void Eszamanli_yazimda_girdi_kaybolmaz_istisna_cikmaz()
    {
        var pages = Configure(4);

        Parallel.For(0, 4000, i =>
        {
            var id = pages[i % 4].PageId;
            _monitor.Report(id, State.Starting);
            Round(id, Kind.Ok);
            _monitor.SetAccount(id, null);
            _monitor.Snapshot().Should().HaveCount(4);
            if (i % 50 == 0) _monitor.Configure(pages);
        });

        _monitor.Snapshot().Should().OnlyContain(h => h.WasLiveThisSession);
    }

    [Fact]
    public void Saat_verilmezse_sistem_saati_okunur()
    {
        var monitor = new InstagramHealthMonitor();
        var before = DateTimeOffset.UtcNow;

        monitor.Configure(new[] { NewPage() });

        monitor.Snapshot().Single().Since.Should().BeOnOrAfter(before).And.BeOnOrBefore(DateTimeOffset.UtcNow);
    }
}
