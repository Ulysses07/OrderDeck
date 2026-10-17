using System.Diagnostics;
using FluentAssertions;
using OrderDeck.App.Services.Sync;
using OrderDeck.Core.Sales;
using OrderDeck.Core.Storage.Repositories;
using Xunit;
using static OrderDeck.Tests.Sync.SalesEditTestBed;

namespace OrderDeck.Tests.Sync;

/// <summary>
/// Bekleyen satış komutlarının gönderimi (spec 2026-10-13 §5.5, §5.6-2, §6.4-1; W2-07): sabit kimlikli komut çevrimdışıyken
/// kuyrukta kalır, deneme sayılır; bağlantı gelince satış akışı turu onu akış isteğinden ÖNCE gönderir, sonuç yanıttan
/// yazılır ve örtü kalkar. <c>stale</c> tam bir kez yeniden dener; <c>content-conflict</c> satırı düşürür ve bir kez iz
/// bırakır; öbür kalıcı retler iz ve uyarı; <c>mode-off</c> satırı tutar (çevirisi Off turunun). <c>mark-printed</c>'in
/// <c>cancelled[]</c>/<c>alreadyPrinted[]</c>'i "kâğıtları imha et" olur. Yanıtı kaybolan komut aynı içerikle yeniden
/// oynatılır (ikinci kez uygulanmaz). Baskı öncesi tur süresiyle sınırlıdır. Mod Off'ta hiçbir komut gitmez.
/// </summary>
public sealed class PendingSalesCommandSenderTests : IDisposable
{
    private readonly SalesEditTestBed _bed = new();

    public void Dispose() => _bed.Dispose();

    private SalesCommandIntent QueueCancel(IReadOnlyList<string> labels)
    {
        var intent = _bed.Commands.Cancel(labels, CancelReasonCodes.QueueRemoved);
        _bed.Sender.Enqueue(intent, labels).Should().BeTrue();
        return intent;
    }

    private SalesCommandIntent QueueMarkPrinted(IReadOnlyList<string> labels)
    {
        var intent = _bed.Commands.MarkPrinted(LabelService.NewPrintBatchId(),
            labels.Select((id, i) => (id, i + 1)).ToList());
        _bed.Sender.Enqueue(intent, labels).Should().BeTrue();
        return intent;
    }

    private long ConflictRows(string kind) =>
        _bed.Scalar<long>("SELECT COUNT(*) FROM SalesConflictLog WHERE Kind = @kind", new { kind });

    [Fact]
    public async Task Cevrimdisi_komut_kuyrukta_kalir_baglaninca_uygulanir_ve_ortu_kalkar()
    {
        _bed.ModeOn();
        var (session, _, labels) = _bed.OwnResidual();
        var intent = QueueCancel(labels);
        _bed.Labels.GetQueue(session).Should().BeEmpty("örtü: bekleyen iptal uygulanmış sayılır");

        _bed.Server.Offline = true;
        var offline = await _bed.Sender.FlushAsync(CancellationToken.None);

        offline.Should().BeEquivalentTo(new { Sent = 0, Kept = 1, Dropped = 0, Remaining = 1 });
        var row = _bed.Pending.Get(intent.CommandId.ToString("N"))!;
        row.Attempts.Should().Be(1);
        row.LastError.Should().Be("Offline");
        _bed.Local(labels[0]).CancelledAt.Should().BeNull("etiket satırına dokunulmadı");
        _bed.Settled.Should().BeEmpty();

        _bed.Server.Offline = false;
        var online = await _bed.Sender.FlushAsync(CancellationToken.None);

        online.Should().BeEquivalentTo(new { Sent = 1, Kept = 0, Dropped = 0, Remaining = 0 });
        _bed.Local(labels[0]).CancelledAt.Should().NotBeNull("sonuç yanıttan yazıldı");
        _bed.Local(labels[0]).CancelReason.Should().Be(CancelReasonCodes.QueueRemoved);
        _bed.Local(labels[0]).ServerVersion.Should().Be(2);
        _bed.Pending.List().Should().BeEmpty();
        _bed.Pending.GetCoveredLabelIds(labels).Should().BeEmpty("örtü kalktı");
        _bed.Server.CommandCalls.Should().ContainSingle()
            .Which.Command.CommandId.Should().Be(intent.CommandId, "sabit kimlik");
        _bed.Settled.Should().ContainSingle().Which.ChangedLabelIds.Should().Contain(labels[0]);
        _bed.Server.Faults.Should().BeEmpty();
    }

    [Fact]
    public async Task Tur_bekleyen_komutu_akis_isteginden_once_gonderir()
    {
        _bed.ModeOn(caughtUp: true);
        var (_, _, labels) = _bed.OwnResidual();
        QueueCancel(labels);
        var pull = _bed.CreatePull();
        var changed = new List<string>();
        pull.SalesChanged += (_, e) => { lock (changed) changed.AddRange(e.LabelIds); };
        int? commandsAtFirstChanges = null;
        _bed.Server.OnChanges = _ => commandsAtFirstChanges ??= _bed.Server.CommandCalls.Count;

        (await pull.TourOnceAsync(CancellationToken.None)).Should().Be(SalesPullOutcome.CaughtUp);

        commandsAtFirstChanges.Should().Be(1, "bekleyen komut sales/changes isteğinden önce gitti");
        _bed.Local(labels[0]).CancelledAt.Should().NotBeNull();
        _bed.Pending.List().Should().BeEmpty();
        changed.Should().Contain(labels[0], "arayüz kopyası tazelensin");
        _bed.Server.Faults.Should().BeEmpty();
    }

    [Fact]
    public async Task Flush_ucu_kuyrugu_gonderir_degisikligi_bildirir_bos_kuyrukta_istek_yok()
    {
        _bed.ModeOn();
        var pull = _bed.CreatePull();
        (await pull.FlushPendingSalesCommandsAsync(CancellationToken.None)).Should().Be(PendingSalesCommandFlush.Empty);
        _bed.HttpCount.Should().Be(0);

        var (_, _, labels) = _bed.OwnResidual();
        QueueCancel(labels);
        var changed = new List<string>();
        pull.SalesChanged += (_, e) => changed.AddRange(e.LabelIds);
        var settled = new List<PendingSalesCommandsSettled>();
        pull.PendingCommandsSettled += (_, e) => settled.Add(e);

        var flush = await pull.FlushPendingSalesCommandsAsync(CancellationToken.None);

        flush.Sent.Should().Be(1);
        changed.Should().Contain(labels[0]);
        settled.Should().ContainSingle();
        _bed.Server.ChangesCalls.Should().BeEmpty("yalnız komutlar; akış turun işi");
    }

    [Fact]
    public async Task Stale_satirlari_tazeler_ve_tam_bir_kez_yeniden_dener()
    {
        _bed.ModeOn();
        var (_, _, labels) = _bed.OwnResidual();
        var id = Guid.ParseExact(labels[0], "N");
        QueueCancel(labels);
        // Başka bilgisayar bu arada (komutla) fiyatı değiştirdi: sunucuda sürüm 2.
        _bed.Server.MutateOrder(id, o => o with { Price = 175m });

        var flush = await _bed.Sender.FlushAsync(CancellationToken.None);

        flush.Sent.Should().Be(1);
        var calls = _bed.Server.CommandCalls.Select(c => c.Command).ToList();
        calls.Should().HaveCount(2, "stale → güncel satırlar → taze sürümle TEK yeniden deneme");
        calls.Select(c => c.CommandId).Distinct().Should().ContainSingle("aynı kimlik");
        calls.Select(c => c.Expected!.Single()!.Version).Should().Equal(1, 2);
        _bed.Local(labels[0]).Price.Should().Be(175m);
        _bed.Local(labels[0]).CancelledAt.Should().NotBeNull();
        _bed.Pending.List().Should().BeEmpty();
    }

    [Fact]
    public async Task Iki_kez_bayat_kalirsa_satir_kuyrukta_kalir_sonraki_gonderim_taze_surumle()
    {
        _bed.ModeOn();
        var (_, _, labels) = _bed.OwnResidual();
        var intent = QueueCancel(labels);
        // Sunucunun güncel satırı yeni bir şey getirmiyor: taze sürüm aynı, yeniden deneme olmaz.
        _bed.Server.EnqueueProblem(FakeSalesServer.Commands, 409, "stale");

        var first = await _bed.Sender.FlushAsync(CancellationToken.None);

        first.Kept.Should().Be(1);
        _bed.Pending.Get(intent.CommandId.ToString("N"))!.LastError.Should().Be("Stale");
        (await _bed.Sender.FlushAsync(CancellationToken.None)).Sent.Should().Be(1);
        _bed.Server.CommandCalls.Should().HaveCount(2);
        _bed.Local(labels[0]).CancelledAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Content_conflict_satiri_dusurur_ortuyu_kaldirir_ve_bir_kez_iz_birakir()
    {
        _bed.ModeOn();
        var (session, _, labels) = _bed.OwnResidual();
        QueueCancel(labels);
        _bed.Server.EnqueueProblem(FakeSalesServer.Commands, 409, "content-conflict");

        var flush = await _bed.Sender.FlushAsync(CancellationToken.None);

        flush.Dropped.Should().Be(1);
        _bed.Pending.List().Should().BeEmpty();
        _bed.Labels.GetQueue(session).Select(l => l.Id).Should().Equal(labels, "örtü kalktı");
        ConflictRows(SalesConflictLog.CommandContentConflict).Should().Be(1);
        _bed.Scalar<long>("SELECT COUNT(*) FROM SalesConflictLog").Should().Be(1, "iz bir kez");
        _bed.Settled.Should().ContainSingle().Which.Warnings.Should().Equal(
            "1 etiketin bekleyen iptali sunucuda işlenemedi: İşlem kaydedilemedi (iç hata); tekrar dene.");
        _bed.Server.CommandCalls.Should().ContainSingle("yeniden denenmez");
    }

    [Fact]
    public async Task Baska_kalici_ret_iz_ve_uyari_birakir_satiri_dusurur()
    {
        _bed.ModeOn();
        var (_, _, labels) = _bed.OwnResidual();
        var intent = QueueMarkPrinted(labels);
        _bed.Server.EnqueueProblem(FakeSalesServer.Commands, 400, "unknown-order");

        var flush = await _bed.Sender.FlushAsync(CancellationToken.None);

        flush.Dropped.Should().Be(1);
        _bed.Pending.List().Should().BeEmpty();
        ConflictRows(SalesConflictLog.PendingCommandRejected).Should().Be(1);
        _bed.Scalar<string>("SELECT Reason FROM SalesConflictLog").Should().Be(
            $"mark-printed; command={intent.CommandId:N}; code=unknown-order");
        _bed.Settled.Should().ContainSingle().Which.Warnings.Should().ContainSingle()
            .Which.Should().StartWith("1 etiketin bekleyen baskı onayı sunucuda işlenemedi:");
    }

    [Fact]
    public async Task Mode_off_yanitinda_satir_kalir()
    {
        _bed.ModeOn();
        var (_, _, labels) = _bed.OwnResidual();
        var intent = QueueCancel(labels);
        _bed.Server.Mode = 0;

        var flush = await _bed.Sender.FlushAsync(CancellationToken.None);

        flush.Kept.Should().Be(1);
        var row = _bed.Pending.Get(intent.CommandId.ToString("N"))!;
        row.Attempts.Should().Be(1);
        row.LastError.Should().Be("ModeOff");
        _bed.Pending.GetCoveredLabelIds(labels).Should().Equal(labels, "çevirisi Off turunun (W2-11)");
        _bed.Settled.Should().BeEmpty();
    }

    [Fact]
    public async Task Client_update_required_alan_bekleyen_komut_kuyrukta_kalir_tur_guncelleme_sorusunu_hic_acmaz()
    {
        _bed.ModeOn();
        var (_, _, labels) = _bed.OwnResidual();
        var intent = QueueCancel(labels);
        _bed.Server.MinClientVersion = "9.0.0";                              // Burak yeni sürümü zorunlu kıldı

        for (var tour = 0; tour < 3; tour++)
            (await _bed.Sender.FlushAsync(CancellationToken.None)).Kept.Should().Be(1);

        _bed.UpdatePrompts.Should().Be(0,
            "arka plandaki her tur modal soruyu yeniden açar, yayın sırasında ana pencereyi kilitlerdi (#508)");
        var row = _bed.Pending.Get(intent.CommandId.ToString("N"))!;
        row.LastError.Should().Be("ClientUpdateRequired");
        row.Attempts.Should().Be(3);
        _bed.Pending.GetCoveredLabelIds(labels).Should().Equal(labels, "güncellenince gider");
    }

    [Fact]
    public async Task Operatorun_aninda_gonderimi_guncelleme_sorusunu_surec_basina_bir_kez_acar()
    {
        _bed.ModeOn();
        var (_, _, labels) = _bed.OwnResidual(count: 2);
        var first = QueueCancel([labels[0]]);
        var second = QueueCancel([labels[1]]);
        _bed.Server.MinClientVersion = "9.0.0";

        (await _bed.Sender.SendNowAsync(first.CommandId)).Kept.Should().Be(1);
        (await _bed.Sender.SendNowAsync(second.CommandId)).Kept.Should().Be(1);
        await _bed.Sender.FlushAsync(CancellationToken.None);

        _bed.UpdatePrompts.Should().Be(1, "operatörün işlemi: soru bir kez; 'Hayır'dan sonra aynı süreçte yeniden sorulmaz");
    }

    [Fact]
    public async Task Yaniti_kaybolan_komut_ayni_icerikle_yeniden_oynatilir_ikinci_kez_uygulanmaz()
    {
        _bed.ModeOn();
        var (_, _, labels) = _bed.OwnResidual();
        var intent = QueueMarkPrinted(labels);
        _bed.Server.LoseNextResponse(FakeSalesServer.Commands);

        (await _bed.Sender.FlushAsync(CancellationToken.None)).Kept.Should().Be(1);
        // Akış (başka bir tur) bu arada satırın yeni sürümünü indirse de saklı niyet AYNEN gider.
        await _bed.PullAsync();
        _bed.Local(labels[0]).ServerVersion.Should().Be(2, "ön koşul: yerel sürüm ilerledi");

        (await _bed.Sender.FlushAsync(CancellationToken.None)).Sent.Should().Be(1);

        var log = _bed.Server.CommandLog(intent.CommandId)!;
        log.Applications.Should().Be(1);
        log.Replays.Should().Be(1, "aynı içerik: yeniden oynatma, content-conflict değil");
        _bed.Pending.List().Should().BeEmpty();
        _bed.Scalar<long>("SELECT COUNT(*) FROM SalesConflictLog").Should().Be(0);
    }

    [Fact]
    public async Task Mark_printed_cancelled_ve_alreadyPrinted_kagit_imha_listesine_duser()
    {
        _bed.ModeOn();
        var (_, _, labels) = _bed.OwnResidual(count: 3);
        QueueMarkPrinted(labels);
        _bed.Server.MutateOrder(Guid.ParseExact(labels[0], "N"), o => o with { CancelledAt = T0.AddHours(2), CancelReason = "musteri" });
        _bed.Server.MutateOrder(Guid.ParseExact(labels[1], "N"), o => o with { PrintedAt = T0.AddHours(2) });

        var flush = await _bed.Sender.FlushAsync(CancellationToken.None);

        flush.Sent.Should().Be(1);
        flush.Settled.DiscardLabelIds.Should().BeEquivalentTo([labels[0], labels[1]]);
        _bed.Settled.Should().ContainSingle().Which.NeedsAttention.Should().BeTrue();
        _bed.Local(labels[2]).Should().NotBeNull();
        _bed.LabelRepo.GetById(labels[2])!.PrintedAt.Should().NotBeNull("üçüncüsü bu komutla basıldı");
        _bed.Pending.List().Should().BeEmpty();
    }

    [Fact]
    public async Task Yerel_kip_Offta_hicbir_komut_gonderilmez()
    {
        _bed.ModeOff();
        // Off turu (W2-11) çevirmeden önce kalmış bir satır: Enqueue mod Off'ta yazmaz, SQL ile kurulur.
        _bed.Exec("INSERT INTO PendingSalesCommand (CommandId, Kind, PayloadJson, CreatedAt) VALUES (@id, 'cancel', '{}', 1)",
            new { id = Guid.NewGuid().ToString("N") });

        var flush = await _bed.Sender.FlushAsync(CancellationToken.None);

        flush.Should().Be(PendingSalesCommandFlush.Empty);
        _bed.HttpCount.Should().Be(0);
        _bed.Pending.List().Should().ContainSingle().Which.Attempts.Should().Be(0);
    }

    [Fact]
    public async Task Baski_oncesi_tur_suresiyle_sinirli_sunucu_asilirsa_bekletmez()
    {
        _bed.ModeOn();
        var pull = _bed.CreatePull();
        _bed.Server.HangEndpoint = FakeSalesServer.Changes;
        var watch = Stopwatch.StartNew();

        var refresh = pull.RefreshAsync(TimeSpan.FromSeconds(1));
        await _bed.Server.RequestHanging.WaitAsync(TimeSpan.FromSeconds(10));   // ön koşul: tur akış isteğinde asılı
        var refreshed = await refresh;

        watch.Stop();
        refreshed.Should().BeFalse();
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(4), "süre dolunca asılı tur beklenmez");
        watch.Elapsed.Should().BeGreaterThan(TimeSpan.FromMilliseconds(900));
    }

    [Fact]
    public async Task Baski_oncesi_tur_sunucudaki_iptali_indirir()
    {
        _bed.ModeOn();
        var (_, _, labels) = _bed.OwnResidual();
        var pull = _bed.CreatePull();
        _bed.Server.MutateOrder(Guid.ParseExact(labels[0], "N"), o => o with { CancelledAt = T0.AddHours(2), CancelReason = "musteri" });

        (await pull.RefreshAsync(TimeSpan.FromSeconds(10))).Should().BeTrue();

        _bed.Local(labels[0]).CancelledAt.Should().NotBeNull();
    }
}
