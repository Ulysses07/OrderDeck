using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Windows.Threading;
using Dapper;
using FluentAssertions;
using OrderDeck.App.Services.Sync;
using OrderDeck.App.ViewModels;
using OrderDeck.Core.Sales;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Tests.Sync;
using Xunit;
using static OrderDeck.Tests.Sync.SalesEditTestBed;

namespace OrderDeck.Tests.App;

/// <summary>
/// Kalıntı kuyruk kabukta (spec 2026-10-13 §5.3 "Kuyruktan çıkar", "Kuyruğu temizle", "Kuyruk baskısı"; §5.5; §5.6; W2-07).
/// Bu bilgisayarın devredilmiş yayınından kalan etiketler yayın açılışında kuyruğa alınır (R7-04 teklifi). Mod On'da
/// çevrimdışı iptal ve ilk baskı bekleyen komut olur: satır değişmez ama kuyrukta, teklifte ve baskıda bir daha
/// görünmez; bağlantı gelince tur komutu akıştan önce gönderir. Kâğıdın komutu sunucunun cancelled[]'ini "kâğıtları imha
/// et" şeridine yazar. Baskıdan önce akış turu en çok 3 sn sürer ve başka bilgisayarda az önce iptal edileni basmaz.
/// Karışık kâğıt tek basış kimliği ve kesintisiz sıra taşır. Mod Off'ta bugünkü yerel yol: bekleyen komut yok, istek yok.
/// </summary>
public sealed class MainShellResidualQueueTests
{
    private sealed class Shell : IDisposable
    {
        public required MainShellTestHarness.Harness H { get; init; }
        public required SalesEditTestBed Bed { get; init; }
        public SalesChangesPullService? Pull { get; init; }

        public MainShellViewModel Vm => H.Vm;

        public void Dispose()
        {
            H.Dispose();
            Bed.Dispose();
        }
    }

    private static Shell Build(bool withPull = false)
    {
        SalesEditTestBed? bed = null;
        SalesChangesPullService? pull = null;
        var h = MainShellTestHarness.Build(
            labelEditor: (db, labels) => (bed = new SalesEditTestBed(db, labels)).Router,
            salesPull: withPull ? _ => pull = bed!.CreatePull() : null);
        return new Shell { H = h, Bed = bed!, Pull = pull };
    }

    private static List<string> QueueIds(Shell s) => s.Vm.PrintQueue.Select(r => r.Id).ToList();

    /// <summary>Yayını bitirip yenisini açar (kuyruk boşken); kalıntı sorusu evet.</summary>
    private static async Task RestartStreamAsync(Shell s)
    {
        s.Vm.PrintQueue.Should().BeEmpty("ön koşul: kuyruk boşken yayın bitirilir");
        s.H.Dialogs.ConfirmResult = _ => true;
        await s.Vm.EndStreamCommand.ExecuteAsync(null);
        s.Vm.StartStreamCommand.Execute(null);
    }

    /// <summary>Devredilmiş kendi yayınının kalıntıları yeni yayının kuyruğuna (R7-04 teklifi).</summary>
    private static async Task<IReadOnlyList<string>> OfferResidualAsync(Shell s, int count = 1, bool modeOff = false)
    {
        var (session, _, labels) = s.Bed.OwnResidual(count);
        // Mod Off'ta kendi yayını yerel otoritede kalır (§2).
        if (modeOff) s.Bed.Exec("UPDATE StreamSession SET Authority = 'local' WHERE Id = @session", new { session });
        await RestartStreamAsync(s);
        QueueIds(s).Should().BeEquivalentTo(labels, "ön koşul: kalıntılar teklifle kuyrukta");
        return labels;
    }

    private static bool OfferedResidual(Shell s) =>
        s.H.Dialogs.Confirmations.Any(c => c.Message.Contains("basılmamış etiket"));

    private static (string? BatchId, long? Seq, long? PrintedAt) PrintStamp(Shell s, string id)
    {
        using var c = s.H.Db.Open();
        return c.QuerySingle<(string?, long?, long?)>("SELECT PrintBatchId, PrintSeq, PrintedAt FROM Label WHERE Id = @id",
            new { id });
    }

    // ── mod Off ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Mod_Offta_kalinti_kuyruk_bugunku_gibi_istek_ve_bekleyen_komut_yok()
    {
        using var s = Build(withPull: true);
        s.Bed.ModeOff();
        s.Bed.Online = false;
        var labels = await OfferResidualAsync(s, count: 3, modeOff: true);

        s.Vm.SelectedQueueItems.Add(s.Vm.PrintQueue.Single(r => r.Id == labels[0]));
        await s.Vm.RemoveSelectedFromQueueCommand.ExecuteAsync(null);
        s.Vm.SelectedQueueItems.Add(s.Vm.PrintQueue.Single(r => r.Id == labels[1]));
        await s.Vm.PrintCommand.ExecuteAsync(null);
        await s.Vm.ClearQueueCommand.ExecuteAsync(null);

        s.Bed.Local(labels[0]).CancelledAt.Should().NotBeNull();
        PrintStamp(s, labels[1]).PrintedAt.Should().NotBeNull();
        s.Bed.Local(labels[2]).CancelledAt.Should().NotBeNull();
        s.H.Printer.Calls.Should().ContainSingle();
        s.Vm.PrintQueue.Should().BeEmpty();
        s.Bed.HttpCount.Should().Be(0, "mod Off'ta (bugün her lisans) baskı ve iptal ağa gitmez");
        s.Bed.Scalar<long>("SELECT COUNT(*) FROM PendingSalesCommand").Should().Be(0);
        s.Vm.SalesPushWarning.Should().BeNull();
    }

    // ── kuyruktan çıkar ───────────────────────────────────────────────────

    [Fact]
    public async Task Cevrimdisi_kuyruktan_cikarilan_kalinti_bekler_teklif_edilmez_baglaninca_tur_akistan_once_gonderir()
    {
        using var s = Build(withPull: true);
        s.Bed.ModeOn();
        var labels = await OfferResidualAsync(s);
        var residual = labels[0];
        var session = s.Bed.LabelRepo.GetById(residual)!.SessionId;
        var before = s.Bed.Local(residual);
        s.Bed.Online = false;

        s.Vm.SelectedQueueItems.Add(s.Vm.PrintQueue.Single());
        await s.Vm.RemoveSelectedFromQueueCommand.ExecuteAsync(null);

        s.Vm.PrintQueue.Should().BeEmpty();
        var row = s.Bed.Pending.List().Should().ContainSingle().Subject;
        row.Kind.Should().Be("cancel");
        row.LabelIds.Should().Equal(residual);
        s.Bed.Local(residual).Should().BeEquivalentTo(before, "etiket satırı değişmez");
        s.Bed.Labels.GetQueue(session).Should().BeEmpty();
        s.Bed.Server.CommandCalls.Should().BeEmpty();
        s.Vm.SalesPushWarning.Should().BeNull("bekleyen iptal sorun değil");

        // Yeniden açılış: kalıntı teklif edilmez.
        s.H.Dialogs.Confirmations.Clear();
        await RestartStreamAsync(s);
        OfferedResidual(s).Should().BeFalse("örtülü etiket yeniden teklif edilmez");
        s.Vm.PrintQueue.Should().BeEmpty();

        // Bağlantı geldi: tur komutu sales/changes'ten önce gönderir, sonuç yanıttan yazılır, örtü kalkar.
        s.Bed.Online = true;
        s.Bed.StateReceivedAt = DateTimeOffset.UtcNow;
        int? commandsAtFirstChanges = null;
        s.Bed.Server.OnChanges = _ => commandsAtFirstChanges ??= s.Bed.Server.CommandCalls.Count;
        (await Task.Run(() => s.Pull!.TourOnceAsync(CancellationToken.None))).Should().Be(SalesPullOutcome.CaughtUp);

        commandsAtFirstChanges.Should().Be(1);
        s.Bed.Local(residual).CancelledAt.Should().NotBeNull();
        s.Bed.Local(residual).CancelReason.Should().Be(CancelReasonCodes.QueueRemoved);
        s.Bed.Pending.List().Should().BeEmpty();
        s.Bed.Pending.GetCoveredLabelIds(labels).Should().BeEmpty();
    }

    [Fact]
    public async Task Cevrimiciyken_kuyrugu_temizle_kalintiyi_komutla_canli_etiketi_yerelde_iptal_eder()
    {
        using var s = Build();
        s.Bed.ModeOn();
        var labels = await OfferResidualAsync(s);
        MainShellTestHarness.EnqueueLabel(s.Vm, "@canli", 120m);
        var live = s.Vm.PrintQueue.Last().Id;

        await s.Vm.ClearQueueCommand.ExecuteAsync(null);

        s.Vm.PrintQueue.Should().BeEmpty();
        s.Bed.Local(labels[0]).CancelledAt.Should().NotBeNull("komut yanıtından");
        s.Bed.Local(live).CancelledAt.Should().NotBeNull();
        s.Bed.Server.CommandCalls.Should().ContainSingle().Which.Command.Kind.Should().Be("cancel");
        s.Bed.Pending.List().Should().BeEmpty();
    }

    // ── kuyruk baskısı ────────────────────────────────────────────────────

    [Fact]
    public async Task Cevrimdisi_basilan_kalinti_bir_kez_basilir_bekleyen_mark_printed_olur_yeniden_teklif_edilmez()
    {
        using var s = Build(withPull: true);
        s.Bed.ModeOn();
        var labels = await OfferResidualAsync(s);
        s.Bed.Online = false;

        await s.Vm.PrintCommand.ExecuteAsync(null);

        s.H.Printer.Calls.Should().ContainSingle().Which.Select(l => l.Id).Should().Equal(labels);
        s.Vm.PrintQueue.Should().BeEmpty();
        var row = s.Bed.Pending.List().Should().ContainSingle().Subject;
        row.Kind.Should().Be("mark-printed");
        row.LabelIds.Should().Equal(labels);
        PrintStamp(s, labels[0]).PrintedAt.Should().BeNull("sunucu otoritesindeki satır yerelde yazılmaz");
        s.Bed.HttpCount.Should().Be(0);

        // Yeniden açılış: kâğıdı basılmış kalıntı teklif edilmez, ikinci kâğıt çıkmaz.
        s.H.Dialogs.Confirmations.Clear();
        await RestartStreamAsync(s);
        OfferedResidual(s).Should().BeFalse();
        await s.Vm.PrintCommand.ExecuteAsync(null);
        s.H.Printer.Calls.Should().ContainSingle("tek kâğıt");
    }

    [Fact]
    public async Task Sunucuda_iptal_edilmis_kalintinin_kagidi_imha_seridine_yazilir()
    {
        using var s = Build();                                    // akış turu yok: iptal baskıdan önce inmez
        s.Bed.ModeOn();
        var labels = await OfferResidualAsync(s);
        s.Bed.Server.MutateOrder(Guid.ParseExact(labels[0], "N"),
            o => o with { CancelledAt = T0.AddHours(2), CancelReason = "musteri" });

        await s.Vm.PrintCommand.ExecuteAsync(null);

        s.H.Printer.Calls.Should().ContainSingle("kâğıt çıktı");
        s.Vm.SalesPushWarning.Should().Be("1 etiket başka bilgisayarda iptal edilmişti; kâğıtları imha et.");
        s.H.Dialogs.Shown.Should().BeEmpty("açılır pencere yok");
        s.Bed.Local(labels[0]).CancelledAt.Should().NotBeNull();
        s.Bed.Pending.List().Should().BeEmpty();

        s.Vm.DismissSalesPushWarningCommand.Execute(null);
        s.Vm.SalesPushWarning.Should().BeNull();
    }

    [Fact]
    public async Task Baski_oncesi_tur_baska_bilgisayarda_iptal_edilen_kalintiyi_basmaz()
    {
        using var s = Build(withPull: true);
        s.Bed.ModeOn();
        var labels = await OfferResidualAsync(s, count: 2);
        s.Bed.Server.MutateOrder(Guid.ParseExact(labels[0], "N"),
            o => o with { CancelledAt = T0.AddHours(2), CancelReason = "musteri" });

        await s.Vm.PrintCommand.ExecuteAsync(null);

        s.H.Printer.Calls.Should().ContainSingle().Which.Select(l => l.Id).Should().Equal(new[] { labels[1] },
            "akış turu iptali baskıdan önce indirdi");
        s.Vm.PrintQueue.Should().BeEmpty();
        s.Bed.Local(labels[0]).CancelledAt.Should().NotBeNull();
        PrintStamp(s, labels[1]).PrintedAt.Should().NotBeNull("mark-printed yanıtından");
        s.Vm.SalesPushWarning.Should().BeNull("kâğıdı çıkmayan etiket imha listesine girmez");
    }

    [Fact]
    public async Task Baski_oncesi_tur_asilirsa_3_sn_beklenir_sonra_basilir()
    {
        using var s = Build(withPull: true);
        s.Bed.ModeOn();
        var labels = await OfferResidualAsync(s);
        s.Bed.Server.HangEndpoint = FakeSalesServer.Changes;
        var watch = Stopwatch.StartNew();

        await s.Vm.PrintCommand.ExecuteAsync(null);

        watch.Stop();
        s.Bed.Server.RequestHanging.IsCompleted.Should().BeTrue("ön koşul: tur akış isteğinde asılı kaldı");
        watch.Elapsed.Should().BeGreaterThan(TimeSpan.FromSeconds(2.5));
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(8), "3 sn'lik tur bütçesi aşılmaz");
        s.H.Printer.Calls.Should().ContainSingle().Which.Select(l => l.Id).Should().Equal(labels);
        PrintStamp(s, labels[0]).PrintedAt.Should().NotBeNull("komut ucu asılı değil: mark-printed uygulandı");
    }

    [Fact]
    public async Task Karisik_kuyruk_baskisi_tek_basis_kimligi_ve_kesintisiz_sira_tasir()
    {
        using var s = Build();
        s.Bed.ModeOn();
        var labels = await OfferResidualAsync(s);
        MainShellTestHarness.EnqueueLabel(s.Vm, "@canli", 120m);
        var live = s.Vm.PrintQueue.Last().Id;

        await s.Vm.PrintCommand.ExecuteAsync(null);

        s.H.Printer.Calls.Should().ContainSingle().Which.Select(l => l.Id).Should().Equal(labels[0], live);
        var command = s.Bed.Server.CommandCalls.Should().ContainSingle().Subject.Command;
        command.Kind.Should().Be("mark-printed");
        var batchId = command.Payload!.Value.GetProperty("batchId").GetString();
        command.Payload!.Value.GetProperty("items")[0].GetProperty("seq").GetInt32().Should().Be(1);
        PrintStamp(s, live).BatchId.Should().Be(batchId, "yerel satır aynı basışta");
        PrintStamp(s, live).Seq.Should().Be(2, "kesintisiz sıra");
        PrintStamp(s, labels[0]).Should().Be((batchId, 1L, PrintStamp(s, labels[0]).PrintedAt));
        s.Bed.Pending.List().Should().BeEmpty();
    }

    // ── arka plandaki sonuç ───────────────────────────────────────────────

    /// <summary>Gövdeyi KENDİ Dispatcher döngüsü olan STA iş parçacığında koşturur (bkz.
    /// <c>MainShellSalesFeedTests.RunOnOwnDispatcher</c>): arka plandaki sonuç dispatcher'a taşınır.</summary>
    private static void RunOnOwnDispatcher(Func<Task> body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try { await body(); }
                catch (Exception ex) { failure = ex; }
                finally { dispatcher.InvokeShutdown(); }
            }));
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(60)).Should().BeTrue("STA dispatcher koşusu zaman aşımına uğramamalı");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    [Fact]
    public void Turun_kalici_ret_sonucu_seride_dispatcher_uzerinden_yazilir()
        => RunOnOwnDispatcher(async () =>
    {
        using var s = Build(withPull: true);
        s.Bed.ModeOn();
        var labels = await OfferResidualAsync(s);
        s.Bed.Online = false;
        s.Vm.SelectedQueueItems.Add(s.Vm.PrintQueue.Single());
        await s.Vm.RemoveSelectedFromQueueCommand.ExecuteAsync(null);
        s.Bed.Pending.List().Should().ContainSingle("ön koşul");
        s.Bed.Server.EnqueueProblem(FakeSalesServer.Commands, 409, "content-conflict");

        await Task.Run(() => s.Pull!.TourOnceAsync(CancellationToken.None));
        await Dispatcher.Yield(DispatcherPriority.Background);

        s.Vm.SalesPushWarning.Should().Be(
            "1 etiketin bekleyen iptali sunucuda işlenemedi: İşlem kaydedilemedi (iç hata); tekrar dene.");
        s.Bed.Pending.List().Should().BeEmpty();
        s.Bed.Scalar<long>("SELECT COUNT(*) FROM SalesConflictLog").Should().Be(1);
        s.Bed.Local(labels[0]).CancelledAt.Should().BeNull("iptal uygulanmadı; örtü kalktı");
    });
}
