using FluentAssertions;
using OrderDeck.App.Services.Drawers;
using OrderDeck.App.Services.PriceCorrection;
using OrderDeck.App.Services.Sync;
using OrderDeck.Core.Sales;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Tests.Sync;
using Xunit;
using static OrderDeck.Tests.Sync.SalesEditTestBed;

namespace OrderDeck.Tests.App;

/// <summary>
/// "Basılanlar"ın toplu iptali, yedek çekmecesi ve yeniden baskısı düzenleme yönlendiricisinden geçer (spec 2026-10-13
/// §5.3 "Basılanlar toplu iptal", "Yeniden bas"; §5.6; §5.7; W2-05). Mod Off'ta bugünkü sonuç ve sıfır istek; mod On'da
/// başka bilgisayarın devredilmiş yayını (rapordan açılmış) komutla iptal edilir, yedekleri yine tek çekmecede listelenir;
/// yeniden baskı çevrimiçiyken sunucunun güncel satırlarını (arada iptal edileni basmadan), çevrimdışıyken önbelleği
/// "son eşitleme" notuyla basar; çevrimdışıyken sunucu satırlarının "İptal et"i pasif ve araç ipucu gösterir.
/// </summary>
public sealed class MainShellPrintedLabelsRoutingTests
{
    private sealed class RecordingDrawers : IDrawerService
    {
        public List<string> Titles { get; } = new();

        public Task<bool> ShowAsync(string title, Func<Drawer, object> buildContent)
        {
            Titles.Add(title);
            return Task.FromResult(true);
        }

        public bool CloseTop() => false;
    }

    private sealed class Shell : IDisposable
    {
        public required MainShellTestHarness.Harness H { get; init; }
        public required SalesEditTestBed Bed { get; init; }
        public required RecordingDrawers Drawers { get; init; }
        public required PrintedLabelsNavigator Navigator { get; init; }

        public void Dispose()
        {
            H.Dispose();
            Bed.Dispose();
        }
    }

    private static Shell Build()
    {
        var drawers = new RecordingDrawers();
        var navigator = new PrintedLabelsNavigator();
        SalesEditTestBed? bed = null;
        var h = MainShellTestHarness.Build(drawers, printedNavigator: navigator,
            labelEditor: (db, labels) => (bed = new SalesEditTestBed(db, labels)).Router);
        return new Shell { H = h, Bed = bed!, Drawers = drawers, Navigator = navigator };
    }

    /// <summary>Rapordan "Basılan etiketler": sekme başka bilgisayarın devredilmiş yayınına açılır (K6).</summary>
    private static void OpenPrinted(Shell s, Guid session)
    {
        s.Navigator.Request(new PrintedLabelsRequest(N(session), "Akşam yayını", DateTime.Now));
        s.H.Vm.PrintedLabels.SessionId.Should().Be(N(session));
    }

    private static void Select(Shell s, params Guid[] ids)
    {
        foreach (var row in s.H.Vm.PrintedLabels.VisibleRows)
            row.IsSelected = ids.Select(N).Contains(row.Id);
    }

    private static async Task<(Guid Session, Guid A, Guid B)> RemotePrintedAsync(Shell s)
    {
        var session = s.Bed.RemoteSession();
        var customer = s.Bed.RemoteCustomer();
        var a = s.Bed.RemoteOrder(session, customer, 150m);
        var b = s.Bed.RemoteOrder(session, customer, 160m);
        await s.Bed.PullAsync();
        return (session, a, b);
    }

    // ── toplu iptal ───────────────────────────────────────────────────────

    [Fact]
    public async Task Mod_Offta_toplu_iptal_bugunku_gibi_ve_istek_gitmez()
    {
        using var s = Build();
        var h = s.H;
        h.Vm.ActiveCode = "A12";
        MainShellTestHarness.EnqueueLabel(h.Vm, "@a", 250m);
        MainShellTestHarness.EnqueueLabel(h.Vm, "@b", 250m);
        var ids = h.Vm.PrintQueue.Select(r => r.Id).ToList();
        await h.Vm.PrintCommand.ExecuteAsync(null);
        h.Vm.IsPrintedTabActive = true;

        var withBackups = await h.Vm.CancelLabelsAsync(ids, CancelReasonCodes.WrongProduct);

        withBackups.Should().BeEmpty();
        h.Vm.PrintedLabels.Message.Should().Be("2 etiket iptal edildi.");
        h.CustomerRepo.FindByPlatformAndUsername("instagram", "@a")!.TotalAmount.Should().Be(0m);
        new LabelRepository(h.Db).GetById(ids[0])!.CancelReason.Should().Be(CancelReasonCodes.WrongProduct);
        h.Vm.PrintedLabels.VisibleRows.Should().BeEmpty();
        s.Bed.HttpCount.Should().Be(0);
    }

    [Fact]
    public async Task Mod_Onda_devredilmis_yayinin_toplu_iptali_tek_komutla_ve_yedekleri_tek_cekmecede()
    {
        using var s = Build();
        s.Bed.ModeOn();
        var session = s.Bed.RemoteSession();
        var customer = s.Bed.RemoteCustomer();
        var a = s.Bed.RemoteOrder(session, customer, 150m);
        var b = s.Bed.RemoteOrder(session, customer, 160m);
        s.Bed.RemoteOrder(session, customer, 140m, tentative: true, parent: a, username: "yedek_alici");
        await s.Bed.PullAsync();
        OpenPrinted(s, session);

        await s.H.Vm.CancelAndTransferBackupsAsync([N(a), N(b)], CancelReasonCodes.Customer);

        var call = s.Bed.Server.CommandCalls.Should().ContainSingle().Subject.Command;
        call.Kind.Should().Be("cancel");
        s.Bed.Local(a).CancelledAt.Should().NotBeNull("yerel ayna yanıttan");
        s.Bed.Local(b).CancelledAt.Should().NotBeNull();
        s.Drawers.Titles.Should().Equal("İptal edilen etiketlerin yedekleri (1)");
        s.H.Vm.PrintedLabels.VisibleRows.Select(r => r.Id).Should().NotContain([N(a), N(b)]);
    }

    [Fact]
    public async Task Session_live_reddi_basilanlar_satirinda()
    {
        using var s = Build();
        s.Bed.ModeOn();
        var (session, a, _) = await RemotePrintedAsync(s);
        OpenPrinted(s, session);
        s.Bed.Server.EnqueueProblem(FakeSalesServer.Commands, 409, "session-live",
            new Dictionary<string, object?> { ["machine"] = "PC-A" });

        var withBackups = await s.H.Vm.CancelLabelsAsync([N(a)], CancelReasonCodes.Customer);

        withBackups.Should().BeEmpty();
        s.H.Vm.PrintedLabels.Message.Should().Be("Bu yayın PC-A'da canlı.");
        s.Bed.Local(a).CancelledAt.Should().BeNull();
    }

    [Fact]
    public async Task Cevrimdisiyken_sunucu_satirlarinin_iptal_et_dugmesi_pasif_arac_ipucuyla()
    {
        using var s = Build();
        s.Bed.ModeOn();
        var (session, a, b) = await RemotePrintedAsync(s);
        OpenPrinted(s, session);
        s.Bed.Online = false;

        Select(s, a, b);

        s.H.Vm.CancelPrintedCommand.CanExecute(null).Should().BeFalse();
        s.H.Vm.PrintedCancelBlockedReason.Should().Be("Bu değişiklik için sunucu bağlantısı gerekli");

        s.Bed.Online = true;
        s.H.Vm.RefreshHeroStats();   // 1 sn'lik zamanlayıcının işi: bağlantı geri gelince düğme açılır
        s.H.Vm.CancelPrintedCommand.CanExecute(null).Should().BeTrue();
        s.H.Vm.PrintedCancelBlockedReason.Should().BeNull();
    }

    [Fact]
    public async Task Mod_Offta_iptal_et_dugmesi_secimle_etkin()
    {
        using var s = Build();
        var h = s.H;
        MainShellTestHarness.EnqueueLabel(h.Vm, "@a", 250m);
        await h.Vm.PrintCommand.ExecuteAsync(null);
        h.Vm.IsPrintedTabActive = true;
        s.Bed.Online = false;   // mod Off'ta bağlantının hükmü yok

        h.Vm.PrintedLabels.VisibleRows.Single().IsSelected = true;

        h.Vm.CancelPrintedCommand.CanExecute(null).Should().BeTrue();
        h.Vm.PrintedCancelBlockedReason.Should().BeNull();
        s.Bed.HttpCount.Should().Be(0);
    }

    // ── yeniden baskı (§5.6) ──────────────────────────────────────────────

    [Fact]
    public async Task Yeniden_baski_cevrimiciyken_sunucunun_guncel_satirlarini_basar_arada_iptal_edileni_basmaz()
    {
        using var s = Build();
        s.Bed.ModeOn();
        var (session, a, b) = await RemotePrintedAsync(s);
        OpenPrinted(s, session);
        Select(s, a, b);
        // Başka bilgisayar bu arada b'yi iptal etti; akış henüz inmedi.
        s.Bed.Server.MutateOrder(b, o => o with { CancelledAt = DateTimeOffset.UtcNow, CancelReason = "musteri" });

        await s.H.Vm.ReprintPrintedCommand.ExecuteAsync(null);

        s.H.Printer.Calls.Should().ContainSingle().Which.Select(l => l.Id).Should().Equal(N(a));
        s.H.Vm.PrintedLabels.Message.Should().Be("1 etiket yeniden basıldı.");
        s.Bed.Server.CommandCalls.Should().ContainSingle().Which.Command.Kind.Should().Be("reprint");
        s.Bed.Local(b).CancelledAt.Should().NotBeNull("yanıtın güncel satırı yerele yazıldı");
    }

    [Fact]
    public async Task Yeniden_baski_cevrimdisiyken_onbellekten_basar_ve_son_esitleme_notu_duser()
    {
        using var s = Build();
        s.Bed.ModeOn();
        var (session, a, b) = await RemotePrintedAsync(s);
        OpenPrinted(s, session);
        Select(s, a, b);
        s.Bed.Online = false;
        var synced = DateTimeOffset.UtcNow.AddMinutes(-12);
        s.Bed.LastFeedSyncAt = synced;
        var http = s.Bed.HttpCount;

        await s.H.Vm.ReprintPrintedCommand.ExecuteAsync(null);

        s.H.Printer.Calls.Should().ContainSingle().Which.Select(l => l.Id).Should().BeEquivalentTo([N(a), N(b)]);
        s.H.Vm.PrintedLabels.Message.Should().Be(
            $"2 etiket yeniden basıldı (sunucuya ulaşılamadı; son eşitleme {SalesCommandMessages.Clock(synced)}).");
        s.Bed.HttpCount.Should().Be(http);
    }

    [Fact]
    public async Task Mod_Offta_yeniden_baski_bugunku_gibi_ve_istek_gitmez()
    {
        using var s = Build();
        var h = s.H;
        MainShellTestHarness.EnqueueLabel(h.Vm, "@a", 250m);
        await h.Vm.PrintCommand.ExecuteAsync(null);
        h.Vm.IsPrintedTabActive = true;
        h.Vm.PrintedLabels.VisibleRows.Single().IsSelected = true;

        await h.Vm.ReprintPrintedCommand.ExecuteAsync(null);

        h.Printer.Calls.Should().HaveCount(2);
        h.Vm.PrintedLabels.Message.Should().Be("1 etiket yeniden basıldı.");
        s.Bed.HttpCount.Should().Be(0);
    }
}
