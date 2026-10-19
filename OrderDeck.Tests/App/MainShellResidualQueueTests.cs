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
    public async Task Cevrimdisiyken_baska_bilgisayarin_etiketi_basilmaz_kuyrukta_kalir_canli_etiket_basilir_baglaninca_o_da_basilir()
    {
        using var s = Build();
        s.Bed.ModeOn();
        // Başka bilgisayarın devredilmiş yayınındaki etiket (ör. add-backup ile eklenmiş yedek) kuyrukta.
        var (_, _, remote) = await s.Bed.RemoteLabelAsync(printed: false);
        var remoteId = remote.ToString("N");
        MainShellTestHarness.EnqueueLabel(s.Vm, "@canli_alici", 100m);
        var live = s.Vm.PrintQueue.Last().Id;
        s.Vm.PrintQueue.Add(new LabelViewModel(s.Bed.LabelRepo.GetById(remoteId)!, isCustomerBlacklisted: false));
        s.Bed.Online = false;

        await s.Vm.PrintCommand.ExecuteAsync(null);

        s.H.Printer.Calls.Should().ContainSingle().Which.Select(l => l.Id).Should().Equal([live],
            "sahip kararı Y11: canlı yayının etiketi internetsiz de basılır; başka bilgisayarınki basılmaz — örtü yalnız "
          + "bu bilgisayarda olurdu, yayının sahibi aynı satırı kalıntı teklifinden basabilirdi (risk 18)");
        QueueIds(s).Should().Equal([remoteId], "kuyrukta bekler; bağlantı gelince basılabilir");
        s.Bed.LabelRepo.GetById(remoteId)!.PrintedAt.Should().BeNull();
        s.Bed.Pending.List().Should().BeEmpty("basılmadı: örtü de yok");
        s.Vm.SalesPushWarning.Should().Be(
            "1 etiket başka bilgisayarın yayınında; sunucu bağlantısı gelince basılabilir, kuyrukta bekliyor.");
        s.H.Dialogs.Shown.Should().BeEmpty("açılır pencere yok");

        // Bağlantı geldi: aynı kuyruk düğmesi satırı basar, onay Y11 yolundan (kalıcı bekleyen komut, hemen gönderilir).
        s.Bed.Online = true;
        s.Bed.StateReceivedAt = DateTimeOffset.UtcNow;
        await s.Vm.PrintCommand.ExecuteAsync(null);

        s.H.Printer.Calls.Should().HaveCount(2);
        s.H.Printer.Calls[1].Select(l => l.Id).Should().Equal([remoteId]);
        s.Vm.PrintQueue.Should().BeEmpty();
        var sent = s.Bed.Server.CommandCalls.Should().ContainSingle().Subject.Command;
        sent.Kind.Should().Be("mark-printed");
        sent.CommandId.Should().NotBeNull("bekleyen komutun sabit kimliği");
        s.Bed.Server.Order(remote)!.Row.PrintedAt.Should().NotBeNull();
        PrintStamp(s, remoteId).PrintedAt.Should().NotBeNull("yanıttan yazıldı");
        s.Bed.Pending.List().Should().BeEmpty();
        s.Vm.SalesPushWarning.Should().BeNull(
            "bekletilen satır basıldı: \"kuyrukta bekliyor\" notu bayat, şerit operatörü beklemeden kapanır");
    }

    [Fact]
    public async Task Yayini_bitirirken_kuyrukta_yalniz_bekletilen_satir_kalirsa_soru_yinelenmez_yayin_acik_kalir_sebep_seritte()
    {
        using var s = Build();
        s.Bed.ModeOn();
        var (_, _, remote) = await s.Bed.RemoteLabelAsync(printed: false);
        var remoteId = remote.ToString("N");
        MainShellTestHarness.EnqueueLabel(s.Vm, "@canli_alici", 100m);
        s.Vm.PrintQueue.Add(new LabelViewModel(s.Bed.LabelRepo.GetById(remoteId)!, isCustomerBlacklisted: false));
        s.Bed.Online = false;
        var answers = 0;
        s.H.Dialogs.ThreeWayResult = _ => ++answers <= 3 ? true : null;   // üç kez "Evet", sonra "İptal"

        await s.Vm.EndStreamCommand.ExecuteAsync(null);

        s.H.Dialogs.ThreeWayConfirmations.Should().ContainSingle(
            "basılabilen basıldı; kuyrukta yalnız bekletilen satır kaldı — aynı soru sonsuza dek yinelenmez");
        s.H.Printer.Calls.Should().ContainSingle();
        QueueIds(s).Should().Equal([remoteId], "bekletilen satır kuyrukta kalır");
        s.H.Sessions.GetActive().Should().NotBeNull("bekletilen satır basılmadı: yayın açık kalır (başarısız baskı gibi)");
        s.H.Dialogs.Shown.Should().BeEmpty("satış durumu açılır pencereyle değil şeritte söylenir (#508)");
        s.Vm.SalesPushWarning.Should().Be(
            "1 etiket başka bilgisayarın yayınında; sunucu bağlantısı olmadan basılamaz. Yayın açık bırakıldı. "
          + "Bağlantı gelince tekrar dene veya \"basmadan bitir\"i seç.",
            "şeritte tek bildirim: baskının \"kuyrukta bekliyor\" notu yinelenmez, yerine yayının neden açık kaldığı");
    }

    [Fact]
    public async Task Yayini_bitirirken_seritte_farkli_sayili_eski_bekletme_notu_kalmaz()
    {
        using var s = Build();
        s.Bed.ModeOn();
        var (remoteSession, customer, first) = await s.Bed.RemoteLabelAsync(printed: false);
        var second = s.Bed.RemoteOrder(remoteSession, customer, printed: false);   // aynı uzak yayın
        await s.Bed.PullAsync();
        s.Vm.PrintQueue.Add(new LabelViewModel(s.Bed.LabelRepo.GetById(first.ToString("N"))!, isCustomerBlacklisted: false));
        s.Bed.Online = false;
        await s.Vm.PrintCommand.ExecuteAsync(null);
        s.Vm.SalesPushWarning.Should().Be(
            "1 etiket başka bilgisayarın yayınında; sunucu bağlantısı gelince basılabilir, kuyrukta bekliyor.",
            "ön koşul: şerit kapatılmadı");
        // Kuyruğa başka bilgisayarın ikinci satırı girdi (ör. bağlantı varken add-backup ile eklenen yedek).
        s.Vm.PrintQueue.Add(new LabelViewModel(s.Bed.LabelRepo.GetById(second.ToString("N"))!, isCustomerBlacklisted: false));
        var answers = 0;
        s.H.Dialogs.ThreeWayResult = _ => ++answers <= 3 ? true : null;   // "Evet", sonra "İptal"

        await s.Vm.EndStreamCommand.ExecuteAsync(null);

        s.H.Sessions.GetActive().Should().NotBeNull("ön koşul: bekletilen satırlar basılmadı, yayın açık");
        s.Vm.SalesPushWarning.Should().Be(
            "2 etiket başka bilgisayarın yayınında; sunucu bağlantısı olmadan basılamaz. Yayın açık bırakıldı. "
          + "Bağlantı gelince tekrar dene veya \"basmadan bitir\"i seç.",
            "bekletme notu tek: eski baskının \"1 etiket … kuyrukta bekliyor\"u sayısı farklı diye şeritte kalmaz");
    }

    [Fact]
    public async Task Baglanti_gelince_basmadan_bitirilen_yayinda_bekletme_notu_kalkar_ilgisiz_not_kalir()
    {
        using var s = Build();
        s.Bed.ModeOn();
        var (_, _, remote) = await s.Bed.RemoteLabelAsync(printed: false);
        var remoteId = remote.ToString("N");
        s.Vm.PrintQueue.Add(new LabelViewModel(s.Bed.LabelRepo.GetById(remoteId)!, isCustomerBlacklisted: false));
        s.Bed.Online = false;
        s.H.Dialogs.ThreeWayResult = _ => true;                     // "Evet: hepsini bas"
        await s.Vm.EndStreamCommand.ExecuteAsync(null);
        s.H.Sessions.GetActive().Should().NotBeNull("ön koşul: bekletilen satır basılmadı, yayın açık");
        // Şeritte bekletme notundan bağımsız bir uyarı da var (ör. arka planda işlenemeyen bir komut).
        const string unrelated = "1 etiketin bekleyen iptali sunucuda işlenemedi; tekrar dene.";
        s.Vm.ShowSalesNotice([], [unrelated]);

        // Bağlantı geldi; operatör bu kez basmadan bitirir — kuyruk baskısı yok, yayın kapanır.
        s.Bed.Online = true;
        s.Bed.StateReceivedAt = DateTimeOffset.UtcNow;
        s.H.Dialogs.ThreeWayResult = _ => false;                    // "Hayır: basmadan bitir"
        await s.Vm.EndStreamCommand.ExecuteAsync(null);

        s.H.Sessions.GetActive().Should().BeNull("ön koşul: yayın bitti");
        s.H.Printer.Calls.Should().BeEmpty("ön koşul: hiçbir şey basılmadı");
        s.Vm.SalesPushWarning.Should().Be(unrelated,
            "\"Yayın açık bırakıldı\" bayat (yayın bitti, satır artık bekletilmiyor): bekletme notu kalkar, ilgisiz not "
          + "operatör kapatana kadar kalır");
    }

    [Fact]
    public async Task Baglanti_gelince_kuyruk_temizlenirse_bekletme_notu_kalkar_ilgisiz_not_kalir()
    {
        const string printHeld =
            "1 etiket başka bilgisayarın yayınında; sunucu bağlantısı gelince basılabilir, kuyrukta bekliyor.";
        using var s = Build();
        s.Bed.ModeOn();
        var (_, _, remote) = await s.Bed.RemoteLabelAsync(printed: false);
        var remoteId = remote.ToString("N");
        s.Vm.PrintQueue.Add(new LabelViewModel(s.Bed.LabelRepo.GetById(remoteId)!, isCustomerBlacklisted: false));
        s.Bed.Online = false;
        await s.Vm.PrintCommand.ExecuteAsync(null);
        s.Vm.SalesPushWarning.Should().Be(printHeld, "ön koşul: bekletme notu şeritte");
        // Bekletilen satır kuyruktayken başka bir satırı çıkarmak notu kaldırmaz: satır hâlâ bekliyor.
        MainShellTestHarness.EnqueueLabel(s.Vm, "@canli_alici", 100m);
        s.Vm.SelectedQueueItems.Add(s.Vm.PrintQueue.Last());
        await s.Vm.RemoveSelectedFromQueueCommand.ExecuteAsync(null);
        QueueIds(s).Should().Equal([remoteId], "ön koşul: canlı satır kuyruktan çıktı");
        s.Vm.SalesPushWarning.Should().Be(printHeld, "bekletilen satır kuyrukta: not hâlâ doğru");
        // Şeritte bekletme notundan bağımsız bir uyarı da var (ör. arka planda işlenemeyen bir komut).
        const string unrelated = "1 etiketin bekleyen iptali sunucuda işlenemedi; tekrar dene.";
        s.Vm.ShowSalesNotice([], [unrelated]);

        // Bağlantı geldi; operatör satırı basmak yerine kuyruğu temizler — kuyruk baskısı yok.
        s.Bed.Online = true;
        s.Bed.StateReceivedAt = DateTimeOffset.UtcNow;
        s.H.Dialogs.ConfirmResult = _ => true;
        await s.Vm.ClearQueueCommand.ExecuteAsync(null);

        s.Vm.PrintQueue.Should().BeEmpty("ön koşul: satır sunucuda iptal edildi");
        s.Bed.Server.CommandCalls.Should().ContainSingle().Which.Command.Kind.Should().Be("cancel");
        s.H.Printer.Calls.Should().BeEmpty("ön koşul: hiçbir şey basılmadı");
        s.Vm.SalesPushWarning.Should().Be(unrelated,
            "kuyrukta bekletilen satır kalmadı: \"kuyrukta bekliyor\" bayat, bekletme notu kalkar, ilgisiz not operatör "
          + "kapatana kadar kalır");
    }

    [Fact]
    public async Task Baska_bilgisayarin_satiri_orada_zaten_basilmissa_serit_zaten_basilmisti_der()
    {
        using var s = Build();
        s.Bed.ModeOn();
        var (_, _, remote) = await s.Bed.RemoteLabelAsync(printed: false);
        s.Vm.PrintQueue.Add(new LabelViewModel(s.Bed.LabelRepo.GetById(remote.ToString("N"))!, isCustomerBlacklisted: false));
        s.Bed.Server.MutateOrder(remote, o => o with { PrintedAt = T0.AddHours(2) });   // akış inmedi

        await s.Vm.PrintCommand.ExecuteAsync(null);

        s.H.Printer.Calls.Should().ContainSingle("kâğıt çıktı");
        s.Vm.SalesPushWarning.Should().Be("1 etiket başka bilgisayarda zaten basılmıştı; kâğıtları imha et.",
            "iptal edilmemiş: \"iptal edilmişti\" yanlış sebep olurdu");
    }

    [Fact]
    public async Task Kalintinin_kagidi_baska_yerde_zaten_basilmissa_serit_zaten_basilmisti_der()
    {
        using var s = Build();
        s.Bed.ModeOn();
        var labels = await OfferResidualAsync(s);
        s.Bed.Server.MutateOrder(Guid.ParseExact(labels[0], "N"), o => o with { PrintedAt = T0.AddHours(2) });

        await s.Vm.PrintCommand.ExecuteAsync(null);

        s.H.Printer.Calls.Should().ContainSingle("kâğıt çıktı");
        s.Vm.SalesPushWarning.Should().Be("1 etiket başka bilgisayarda zaten basılmıştı; kâğıtları imha et.");
        s.Bed.Pending.List().Should().BeEmpty();
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

    [Fact]
    public void Turun_bekleyen_baski_onayi_zaten_basilmis_donerse_serit_zaten_basilmisti_der()
        => RunOnOwnDispatcher(async () =>
    {
        using var s = Build(withPull: true);
        s.Bed.ModeOn();
        var labels = await OfferResidualAsync(s);
        s.Bed.Online = false;
        await s.Vm.PrintCommand.ExecuteAsync(null);
        s.Bed.Pending.List().Should().ContainSingle("ön koşul: kâğıdın onayı bekleyen komutta");
        s.Bed.Server.MutateOrder(Guid.ParseExact(labels[0], "N"), o => o with { PrintedAt = T0.AddHours(2) });

        await Task.Run(() => s.Pull!.TourOnceAsync(CancellationToken.None));
        await Dispatcher.Yield(DispatcherPriority.Background);

        s.Bed.Pending.List().Should().BeEmpty();
        s.Vm.SalesPushWarning.Should().Be("1 etiket başka bilgisayarda zaten basılmıştı; kâğıtları imha et.");
    });

    [Fact]
    public void Devir_kilidi_suresinde_alinamayan_satirin_isareti_arka_planda_yazilamazsa_serit_soyler()
        => RunOnOwnDispatcher(async () =>
    {
        using var s = Build();
        s.Bed.ModeOn();
        s.Bed.Online = false;
        var session = s.Bed.LocalSession(ended: true);             // kendi bitmiş, devredilmemiş yayını: kapılı
        var label = s.Bed.LocalLabel(session, Guid.NewGuid().ToString("N"), printed: false);
        s.Vm.PrintQueue.Add(new LabelViewModel(s.Bed.LabelRepo.GetById(label)!, isCustomerBlacklisted: false));
        s.Bed.Router.HandoverWaitBudget = TimeSpan.FromMilliseconds(100);
        var held = await s.Bed.Gate.EnterAsync(session);           // kapanış gönderimi sessions/sync'te asılı

        await s.Vm.PrintCommand.ExecuteAsync(null);
        s.H.Printer.Calls.Should().ContainSingle("ön koşul: kâğıt çıktı");
        s.Vm.SalesPushWarning.Should().BeNull("ön koşul: işaret kilit açılınca arka planda");

        // Beklenmeyen veritabanı hatası: kilit açılınca damga düşer.
        s.Bed.Exec("CREATE TRIGGER TestMarkFails BEFORE UPDATE OF PrintedAt ON Label WHEN new.Id = '" + label + "' "
                 + "BEGIN SELECT RAISE(ABORT, 'test-db-failure'); END");
        held.Dispose();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (s.Vm.SalesPushWarning is null && DateTime.UtcNow < deadline)
            await Task.Delay(20);                                   // dispatcher bu arada sonucu işler

        s.Vm.SalesPushWarning.Should().Be(
            "1 etiketin kâğıdı basıldı ama baskısı bu bilgisayara yazılamadı (iç hata); etiketleri kontrol et.",
            "kâğıdı basılan satırın kaydı yazılamadı: sessizce kaybolmaz");
        s.H.Dialogs.Shown.Should().BeEmpty("açılır pencere yok");
        PrintStamp(s, label).PrintedAt.Should().BeNull();
    });
}
