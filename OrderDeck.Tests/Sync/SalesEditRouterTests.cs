using FluentAssertions;
using OrderDeck.App.Services.Sales;
using OrderDeck.App.Services.Sync;
using OrderDeck.Core.Sales;
using OrderDeck.Core.Sessions;
using Xunit;
using static OrderDeck.Tests.Sync.SalesEditTestBed;

namespace OrderDeck.Tests.Sync;

/// <summary>
/// Düzenleme yönlendiricisi (spec 2026-10-13 §5.3, §5.2 devir kapısı, §5.6 yeniden baskı, §5.7, §5.8, §8.4-2; W2-05).
/// Mod Off'ta her kombinasyon bugünkü yerel yol: kapı yok, ağ yok. Mod On'da etiket başına: canlı kendi yayını yerel,
/// ilk yetişmeden önce devri bilinmeyen kendi bitmiş yayını kapalı, sonra kapılı yerel, devredilmiş yayın komut,
/// çevrimdışı ya da uç yoksa kullanılamaz. Karışık seçim bölünür. Sunucu <see cref="FakeSalesServer"/>.
/// </summary>
public sealed class SalesEditRouterTests : IDisposable
{
    private readonly SalesEditTestBed _bed = new();

    public void Dispose() => _bed.Dispose();

    private string Customer() => Guid.NewGuid().ToString("N");

    // ── mod Off: her kombinasyon yerel, kapı ve ağ yok ────────────────────

    public static IEnumerable<object[]> AllCombinations()
    {
        foreach (var authority in new[] { StreamSessionAuthority.Local, StreamSessionAuthority.Server })
        foreach (var ended in new[] { false, true })
        foreach (var released in new[] { false, true })
        foreach (var caughtUp in new[] { false, true })
        foreach (var online in new[] { false, true })
            yield return [authority, ended, released, caughtUp, online];
    }

    [Theory]
    [MemberData(nameof(AllCombinations))]
    public async Task Mod_Offta_her_kombinasyon_yerel_kapiya_ve_aga_dokunmaz(
        string authority, bool ended, bool released, bool caughtUp, bool online)
    {
        _bed.ModeOff(caughtUp);
        _bed.Online = online;
        var session = _bed.LocalSession(ended, released ? 950 : null, authority);
        var label = _bed.LocalLabel(session, Customer());
        using var held = await _bed.Gate.EnterAsync(session);

        var decisions = _bed.Router.Route([label]);

        decisions.Should().ContainSingle().Which.Route.Should().Be(SalesEditRoute.Local);
        _bed.Router.EditBlockedReason([label]).Should().BeNull();
        _bed.Gate.EntryCount.Should().Be(1, "yalnız testin tuttuğu kilit");
        _bed.HttpCount.Should().Be(0);
    }

    [Fact]
    public async Task Mod_Offta_islemler_bugunku_yerel_cagri_kapi_tutulsa_da_yazar_ve_hic_istek_gitmez()
    {
        // En kötü kombinasyon: kendi bitmiş yayını, devri bilinmiyor, ilk yetişme yok, kapı başkasında.
        _bed.ModeOff();
        var session = _bed.LocalSession(ended: true);
        var customer = Customer();
        var label = _bed.LocalLabel(session, customer);
        var backup = _bed.LocalLabel(session, customer, 80m, parentLabelId: label, tentative: true);
        using var held = await _bed.Gate.EnterAsync(session);

        var cancel = await _bed.Router.CancelAsync([label], CancelReasonCodes.Customer);
        cancel.Applied.Should().Equal(label);
        cancel.HasProblems.Should().BeFalse();
        _bed.Local(label).CancelledAt.Should().NotBeNull();
        _bed.Local(label).SyncedAt.Should().BeNull("bugünkü gibi push kuyruğuna girer");

        var uncancel = await _bed.Router.UncancelAsync([label]);
        uncancel.Applied.Should().Equal(label);
        _bed.Local(label).CancelledAt.Should().BeNull();

        var confirm = await _bed.Router.ConfirmBackupAsync(backup, 90m);
        confirm.Applied.Should().Equal(backup);
        _bed.Local(backup).IsTentativeBackup.Should().Be(0);
        _bed.Local(backup).Price.Should().Be(90m);

        var reprint = await _bed.Router.PrepareReprintAsync([label, backup]);
        reprint.Labels.Select(l => l.Id).Should().Equal(label, backup);
        reprint.FromCache.Should().BeFalse();
        reprint.CacheNote.Should().BeNull();

        _bed.HttpCount.Should().Be(0);
        _bed.Gate.EntryCount.Should().Be(1);
    }

    [Fact]
    public void Mod_Offta_islemler_eszamanli_tamamlanir()
    {
        // Mod Off'ta yerel yol bugünkü gibi çağıranın iş parçacığında, beklemeden biter (canlı yol ağı beklemez).
        _bed.ModeOff();
        var label = _bed.LocalLabel(_bed.LocalSession(ended: false), Customer());

        var task = _bed.Router.CancelAsync([label], CancelReasonCodes.Customer);

        task.IsCompletedSuccessfully.Should().BeTrue();
        _bed.Local(label).CancelledAt.Should().NotBeNull();
    }

    // ── mod On: yayın hâline göre yol ─────────────────────────────────────

    [Fact]
    public async Task Mod_Onda_canli_kendi_yayini_kapisiz_yerel()
    {
        _bed.ModeOn(caughtUp: false);
        var session = _bed.LocalSession(ended: false);
        var label = _bed.LocalLabel(session, Customer());
        using var held = await _bed.Gate.EnterAsync(session);

        _bed.Router.Route([label]).Single().Route.Should().Be(SalesEditRoute.Local);
        var result = await _bed.Router.CancelAsync([label], CancelReasonCodes.Customer);

        result.HasProblems.Should().BeFalse("canlı yayın devir kapısına hiç uğramaz");
        _bed.Local(label).CancelledAt.Should().NotBeNull();
        _bed.HttpCount.Should().Be(0);
    }

    [Fact]
    public async Task Mod_Onda_ilk_yetismeden_once_devri_bilinmeyen_kendi_bitmis_yayini_kapali()
    {
        _bed.ModeOn(caughtUp: false);
        var label = _bed.LocalLabel(_bed.LocalSession(ended: true), Customer());

        var decision = _bed.Router.Route([label]).Single();
        decision.Route.Should().Be(SalesEditRoute.Blocked);
        decision.Reason.Should().Be("Senkron açılıyor; birkaç dakika sonra tekrar dene.");

        var result = await _bed.Router.CancelAsync([label], CancelReasonCodes.Customer);
        result.Applied.Should().BeEmpty();
        result.ProblemText.Should().Be("Senkron açılıyor; birkaç dakika sonra tekrar dene.");
        _bed.Local(label).CancelledAt.Should().BeNull("yerel yazım push'ta K5 ile reddedilip kaybolurdu");
        _bed.HttpCount.Should().Be(0);
    }

    [Fact]
    public async Task Mod_Onda_ilk_yetismeden_sonra_kapanis_gonderimi_kilidi_tutarken_devir_metni_doner()
    {
        _bed.ModeOn();
        var session = _bed.LocalSession(ended: true);
        var label = _bed.LocalLabel(session, Customer());
        _bed.Router.Route([label]).Single().Route.Should().Be(SalesEditRoute.LocalGated);

        var push = await _bed.Gate.EnterAsync(session);   // kapanış gönderimi "boş mu" okumasıyla yanıt arasında
        var refused = await _bed.Router.CancelAsync([label], CancelReasonCodes.Customer);
        push.Dispose();

        refused.ProblemText.Should().Be("Yayın sunucuya devrediliyor; birkaç saniye sonra tekrar dene.");
        refused.Problems.Single().LabelIds.Should().Equal(label);
        _bed.Local(label).CancelledAt.Should().BeNull();

        var accepted = await _bed.Router.CancelAsync([label], CancelReasonCodes.Customer);
        accepted.HasProblems.Should().BeFalse();
        _bed.Local(label).CancelledAt.Should().NotBeNull();
        _bed.Gate.EntryCount.Should().Be(0, "kilit yazımdan sonra bırakılır");
        _bed.HttpCount.Should().Be(0);
    }

    [Fact]
    public void Mod_Onda_force_end_ile_devri_gorulmus_ama_yerelde_yeni_bitmis_yayin_kapili_yerel()
    {
        // §5.4: aktifken force-end gördü (ReleasedAt saklandı, Authority yerel); operatör yayını bitirdi, kapanış
        // gönderimi henüz dönmedi. Sahibin push'u K2 ile yargılanır (K5 istisnası): yerel düzenleme kapıyla.
        _bed.ModeOn(caughtUp: false);
        var label = _bed.LocalLabel(_bed.LocalSession(ended: true, releasedAt: 950), Customer());

        _bed.Router.Route([label]).Single().Route.Should().Be(SalesEditRoute.LocalGated);
    }

    [Fact]
    public async Task Mod_Onda_devredilmis_yayin_tek_iptal_komutu_gonderir_beklenen_surum_sunucunun_ve_yerel_satir_yanittan()
    {
        _bed.ModeOn();
        var (_, _, order) = await _bed.RemoteLabelAsync();
        var before = _bed.Local(order);
        before.ServerVersion.Should().Be(1);

        _bed.Router.Route([N(order)]).Single().Route.Should().Be(SalesEditRoute.Command);
        var result = await _bed.Router.CancelAsync([N(order)], CancelReasonCodes.Customer);

        result.HasProblems.Should().BeFalse();
        result.Applied.Should().Equal(N(order));
        result.ChangedLabelIds.Should().Contain(N(order));
        var call = _bed.Server.CommandCalls.Should().ContainSingle().Subject.Command;
        call.Kind.Should().Be("cancel");
        call.Expected!.Single()!.Version.Should().Be(1, "beklenen sürüm yerel ServerVersion");
        var after = _bed.Local(order);
        after.CancelledAt.Should().NotBeNull("yerel ayna yanıtın satırından");
        after.ServerVersion.Should().Be(2);
        after.SyncedAt.Should().Be(before.SyncedAt, "yanıt gönderim kuyruğuna girmez (DM8)");
        after.Revision.Should().Be(before.Revision);
    }

    [Fact]
    public async Task Mod_Onda_cevrimdisiyken_devredilmis_yayin_kullanilamaz_arac_ipucuyla_ve_istek_gitmez()
    {
        _bed.ModeOn();
        var (_, _, order) = await _bed.RemoteLabelAsync();
        _bed.Online = false;
        var http = _bed.HttpCount;

        var decision = _bed.Router.Route([N(order)]).Single();
        decision.Route.Should().Be(SalesEditRoute.Unavailable);
        decision.Reason.Should().Be("Bu değişiklik için sunucu bağlantısı gerekli");
        _bed.Router.EditBlockedReason([N(order)]).Should().Be("Bu değişiklik için sunucu bağlantısı gerekli");
        _bed.Router.ServerUnavailableReason.Should().Be("Bu değişiklik için sunucu bağlantısı gerekli");

        var result = await _bed.Router.CancelAsync([N(order)], CancelReasonCodes.Customer);

        result.ProblemText.Should().Be("Bu değişiklik için sunucu bağlantısı gerekli.");
        _bed.Local(order).CancelledAt.Should().BeNull();
        _bed.HttpCount.Should().Be(http);
    }

    [Fact]
    public async Task Komutun_ag_hatasi_baglantiyi_cevrimdisi_yapar()
    {
        _bed.ModeOn();
        var (_, _, order) = await _bed.RemoteLabelAsync();
        _bed.Server.Offline = true;

        var result = await _bed.Router.CancelAsync([N(order)], CancelReasonCodes.Customer);

        result.ProblemText.Should().Be("Bu değişiklik için sunucu bağlantısı gerekli.");
        _bed.Router.Route([N(order)]).Single().Route.Should().Be(SalesEditRoute.Unavailable,
            "son state taze olsa da komut ağa takıldı (sonraki başarılı state'e kadar)");
    }

    [Fact]
    public async Task Komut_ucu_yoksa_bir_sure_kullanilamaz_metniyle()
    {
        _bed.ModeOn();
        var (_, _, order) = await _bed.RemoteLabelAsync();
        _bed.Server.EnqueueStatus(FakeSalesServer.Commands, 404);

        var result = await _bed.Router.CancelAsync([N(order)], CancelReasonCodes.Customer);

        result.ProblemText.Should().Be("Sunucu güncelleniyor; biraz sonra tekrar dene.");
        var decision = _bed.Router.Route([N(order)]).Single();
        decision.Route.Should().Be(SalesEditRoute.Unavailable);
        decision.Reason.Should().Be("Sunucu güncelleniyor; biraz sonra tekrar dene.");
    }

    [Fact]
    public async Task Session_live_reddi_satir_ici_metne_doner()
    {
        _bed.ModeOn();
        var (_, _, order) = await _bed.RemoteLabelAsync();
        var lastSeen = DateTimeOffset.UtcNow.AddMinutes(-2);
        _bed.Server.EnqueueProblem(FakeSalesServer.Commands, 409, "session-live",
            new Dictionary<string, object?> { ["machine"] = "PC-A", ["ownerLastSeenAt"] = lastSeen });

        var result = await _bed.Router.CancelAsync([N(order)], CancelReasonCodes.Customer);

        result.ProblemText.Should().Be($"Bu yayın PC-A'da canlı (son sinyal {SalesCommandMessages.Clock(lastSeen)}).");
        _bed.Local(order).CancelledAt.Should().BeNull();
    }

    [Fact]
    public async Task Karisik_secim_bolunur_bir_yerel_yazim_ve_bir_komut()
    {
        _bed.ModeOn();
        var (_, customer, order) = await _bed.RemoteLabelAsync();
        var local = _bed.LocalLabel(_bed.LocalSession(ended: true), N(customer));
        var localBefore = _bed.Local(local);

        var result = await _bed.Router.CancelAsync([local, N(order)], CancelReasonCodes.Customer);

        result.HasProblems.Should().BeFalse();
        result.Applied.Should().BeEquivalentTo([local, N(order)]);
        var call = _bed.Server.CommandCalls.Should().ContainSingle().Subject.Command;
        call.Payload!.Value.GetProperty("items").EnumerateArray().Select(e => e.GetGuid()).Should().Equal(order);
        _bed.Local(local).CancelledAt.Should().NotBeNull();
        _bed.Local(local).Revision.Should().Be(localBefore.Revision + 1, "yerel yazım bir kez, push kuyruğuna");
        _bed.Local(N(order)).CancelledAt.Should().NotBeNull();
        _bed.Router.EditBlockedReason([local, N(order)]).Should().BeNull();
    }

    [Fact]
    public async Task Karisik_secimde_cevrimdisi_kisim_sayisiyla_birlesik_metinde()
    {
        _bed.ModeOn();
        var (_, customer, order) = await _bed.RemoteLabelAsync();
        var local = _bed.LocalLabel(_bed.LocalSession(ended: false), N(customer));
        _bed.Online = false;

        var result = await _bed.Router.CancelAsync([local, N(order)], CancelReasonCodes.Customer);

        result.Applied.Should().Equal(local);
        result.ProblemText.Should().Be("1 etiket değiştirilemedi: Bu değişiklik için sunucu bağlantısı gerekli.");
        _bed.Router.EditBlockedReason([local, N(order)]).Should().BeNull("karışık seçim etkin kalır, bölünür");
        _bed.Server.CommandCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task Iptali_geri_alma_ve_yedek_onayi_komutla()
    {
        _bed.ModeOn();
        var session = _bed.RemoteSession();
        var customer = _bed.RemoteCustomer();
        var cancelled = _bed.RemoteOrder(session, customer, cancelled: true);
        var backup = _bed.RemoteOrder(session, customer, 120m, tentative: true, parent: cancelled);
        await _bed.PullAsync();

        var uncancel = await _bed.Router.UncancelAsync([N(cancelled)]);
        var confirm = await _bed.Router.ConfirmBackupAsync(N(backup), 110m);

        uncancel.HasProblems.Should().BeFalse();
        confirm.HasProblems.Should().BeFalse();
        _bed.Server.CommandCalls.Select(c => c.Command.Kind).Should().Equal("uncancel", "confirm-backup");
        _bed.Local(cancelled).CancelledAt.Should().BeNull();
        _bed.Local(backup).IsTentativeBackup.Should().Be(0);
        _bed.Local(backup).Price.Should().Be(110m);
    }

    // ── bağlantı ölçüsü ───────────────────────────────────────────────────

    [Fact]
    public void Baglanti_son_state_iki_tur_icinde_ve_bu_lisansinsa_cevrimici()
    {
        var now = DateTimeOffset.UtcNow;
        SalesStateSnapshot? state = new("lisans-a", 1, null, null, [], 15, [], now);
        var key = "lisans-a";
        var connectivity = new SalesConnectivity(() => state, () => key, () => now);

        connectivity.IsOnline.Should().BeTrue();
        connectivity.UnavailableReason.Should().BeNull();

        now += TimeSpan.FromSeconds(76);
        connectivity.IsOnline.Should().BeFalse("iki turdan eski state");
        connectivity.UnavailableReason.Should().Be("Bu değişiklik için sunucu bağlantısı gerekli");

        state = state with { ReceivedAt = now };
        connectivity.IsOnline.Should().BeTrue();
        key = "lisans-b";
        connectivity.IsOnline.Should().BeFalse("başka lisansın state'i sayılmaz");
    }

    [Fact]
    public void Baglanti_komutun_ag_hatasi_yeni_state_ya_da_sunucu_yanitina_kadar_cevrimdisi_404_bir_sure()
    {
        var now = DateTimeOffset.UtcNow;
        SalesStateSnapshot? state = new("lisans-a", 1, null, null, [], 15, [], now);
        var connectivity = new SalesConnectivity(() => state, () => "lisans-a", () => now);

        now += TimeSpan.FromSeconds(5);
        connectivity.Report(SalesCommandStatus.Offline);
        connectivity.IsOnline.Should().BeFalse();
        connectivity.Report(SalesCommandStatus.NotReady);
        connectivity.IsOnline.Should().BeFalse("istek gitmedi: ölçü değişmez");

        now += TimeSpan.FromSeconds(5);
        state = state with { ReceivedAt = now };
        connectivity.IsOnline.Should().BeTrue("hatadan sonra gelen state");

        connectivity.Report(SalesCommandStatus.Offline);
        connectivity.Report(SalesCommandStatus.Stale);
        connectivity.IsOnline.Should().BeTrue("sunucu yanıt verdi");

        connectivity.Report(SalesCommandStatus.EndpointMissing);
        connectivity.UnavailableReason.Should().Be("Sunucu güncelleniyor; biraz sonra tekrar dene.");
        now += TimeSpan.FromSeconds(61);
        state = state with { ReceivedAt = now };
        connectivity.UnavailableReason.Should().BeNull("uç bir süre sonra yeniden denenir");
    }

    // ── yeniden baskı (§5.6) ──────────────────────────────────────────────

    [Fact]
    public async Task Yeniden_baski_cevrimiciyken_sunucunun_guncel_satirlari_basilir()
    {
        _bed.ModeOn();
        var session = _bed.RemoteSession();
        var customer = _bed.RemoteCustomer();
        var kept = _bed.RemoteOrder(session, customer, 150m);
        var dropped = _bed.RemoteOrder(session, customer, 160m);
        await _bed.PullAsync();
        // Başka bilgisayar bu arada iptal etti ve fiyatı değiştirdi; akış henüz inmedi.
        _bed.Server.MutateOrder(dropped, o => o with { CancelledAt = DateTimeOffset.UtcNow, CancelReason = "musteri" });
        _bed.Server.MutateOrder(kept, o => o with { Price = 140m, PriceCorrectedAt = DateTimeOffset.UtcNow });

        var plan = await _bed.Router.PrepareReprintAsync([N(kept), N(dropped)]);

        plan.Error.Should().BeNull();
        plan.FromCache.Should().BeFalse();
        plan.Labels.Select(l => l.Id).Should().Equal(N(kept));
        plan.Labels.Single().Price.Should().Be(140m, "güncel fiyat");
        plan.CorrectionIds.Should().Contain(N(kept));
        _bed.Server.CommandCalls.Single().Command.Kind.Should().Be("reprint");
    }

    [Fact]
    public async Task Yeniden_baski_cevrimdisiyken_onbellekten_ve_son_esitleme_notuyla()
    {
        _bed.ModeOn();
        var (_, customer, order) = await _bed.RemoteLabelAsync();
        var local = _bed.LocalLabel(_bed.LocalSession(ended: true), N(customer));
        _bed.Online = false;
        var synced = DateTimeOffset.UtcNow.AddMinutes(-7);
        _bed.LastFeedSyncAt = synced;
        var http = _bed.HttpCount;

        var plan = await _bed.Router.PrepareReprintAsync([N(order), local]);

        plan.Labels.Select(l => l.Id).Should().Equal(N(order), local);
        plan.FromCache.Should().BeTrue();
        plan.CacheNote.Should().Be($"son eşitleme {SalesCommandMessages.Clock(synced)}");
        _bed.HttpCount.Should().Be(http);
    }

    [Fact]
    public async Task Yeniden_baskida_komut_aga_takilirsa_onbellekten_basilir()
    {
        _bed.ModeOn();
        var (_, _, order) = await _bed.RemoteLabelAsync();
        _bed.Server.Offline = true;

        var plan = await _bed.Router.PrepareReprintAsync([N(order)]);

        plan.Labels.Select(l => l.Id).Should().Equal(N(order));
        plan.FromCache.Should().BeTrue();
        plan.CacheNote.Should().Be("son eşitleme bilinmiyor");
    }

    [Fact]
    public async Task Yeniden_baski_yalniz_yerel_satirlarda_komut_gondermez()
    {
        _bed.ModeOn();
        var label = _bed.LocalLabel(_bed.LocalSession(ended: true), Customer());

        var plan = await _bed.Router.PrepareReprintAsync([label]);

        plan.Labels.Select(l => l.Id).Should().Equal(label);
        plan.FromCache.Should().BeFalse();
        _bed.Server.CommandCalls.Should().BeEmpty();
    }
}
