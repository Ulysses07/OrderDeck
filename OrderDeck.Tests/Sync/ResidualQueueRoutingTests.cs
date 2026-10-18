using System.Data;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using OrderDeck.App.Services.Sync;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Sales;
using OrderDeck.Core.Sessions;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Core.Time;
using OrderDeck.Tests.TestHelpers;
using Xunit;
using static OrderDeck.Tests.Sync.SalesEditTestBed;

namespace OrderDeck.Tests.Sync;

/// <summary>
/// Kalıntı kuyruğun yönlendirmesi (spec 2026-10-13 §5.3 "Kuyruktan çıkar", "Kuyruğu temizle", "Kuyruk baskısı"; §5.5;
/// §5.6-4; W2-07): mod Off'ta bugünkü yerel iptal ve baskı, sıfır istek, sıfır bekleyen komut. Mod On'da canlı yayının
/// etiketleri yerelde; devredilmiş KENDİ yayınının kalıntıları sabit kimlikli komutla — önce bekleyen komut (örtü),
/// çevrimiçiyse hemen gönderilir; çevrimdışıyken ya da komut ucu yokken kuyrukta bekler. Başka bilgisayarın yayınındaki
/// satırın kâğıdı da (sahip kararı Y11) aynı yoldan: sonucu bilinmeyen onay kuyrukta kalır, tur aynı kimlikle yineler.
/// Karışık kâğıt tek basış kimliği ve kesintisiz sıra taşır. Kendi bitmiş yayınının kapanış kararı sürerken işaretleme
/// kilidi bekler ve kararı kilit altında yeniden verir.
/// </summary>
public sealed class ResidualQueueRoutingTests : IDisposable
{
    private readonly SalesEditTestBed _bed = new();

    public void Dispose() => _bed.Dispose();

    private string ActiveLabel(out string session)
    {
        session = _bed.LocalSession(ended: false);
        return _bed.LocalLabel(session, Guid.NewGuid().ToString("N"), printed: false);
    }

    private (string? BatchId, long? Seq, long? PrintedAt) Print(string id)
    {
        using var c = _bed.Db.Open();
        return c.QuerySingle<(string?, long?, long?)>("SELECT PrintBatchId, PrintSeq, PrintedAt FROM Label WHERE Id = @id",
            new { id });
    }

    // ── mod Off ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Mod_Offta_kuyruk_iptali_ve_baski_bugunku_yerel_yol_istek_ve_bekleyen_komut_yok()
    {
        _bed.ModeOff();
        var (session, _, labels) = _bed.OwnResidual(count: 2);
        // Mod Off'ta kendi yayını yerel otoritede kalır (§2); kurulumun 'server'ı burada geri alınır.
        _bed.Exec("UPDATE StreamSession SET Authority = 'local' WHERE Id = @session", new { session });

        var cancel = await _bed.Router.CancelQueueAsync([labels[0]], CancelReasonCodes.QueueRemoved);
        var print = await _bed.Router.MarkPrintedAsync([labels[1]]);

        cancel.Applied.Should().Equal(labels[0]);
        cancel.PendingLabelIds.Should().BeEmpty();
        print.Marked.Should().Equal(labels[1]);
        _bed.Local(labels[0]).CancelledAt.Should().NotBeNull();
        Print(labels[1]).PrintedAt.Should().NotBeNull();
        _bed.HttpCount.Should().Be(0);
        _bed.Scalar<long>("SELECT COUNT(*) FROM PendingSalesCommand").Should().Be(0);
    }

    // ── kuyruktan çıkar ───────────────────────────────────────────────────

    [Fact]
    public async Task Cevrimiciyken_kalinti_iptali_komutla_uygulanir_kuyrukta_satir_kalmaz()
    {
        _bed.ModeOn();
        var (session, _, labels) = _bed.OwnResidual();

        var result = await _bed.Router.CancelQueueAsync(labels, CancelReasonCodes.QueueRemoved);

        result.Applied.Should().Equal(labels);
        result.HasProblems.Should().BeFalse();
        _bed.Local(labels[0]).CancelledAt.Should().NotBeNull("yanıttan");
        _bed.Server.CommandCalls.Should().ContainSingle().Which.Command.Kind.Should().Be("cancel");
        _bed.Pending.List().Should().BeEmpty();
        _bed.Labels.GetQueue(session).Should().BeEmpty();
    }

    [Fact]
    public async Task Cevrimdisi_kalinti_iptali_bekleyen_komut_olur_satir_degismez_kuyrukta_ve_teklifte_gorunmez()
    {
        _bed.ModeOn();
        _bed.Online = false;
        var (session, _, labels) = _bed.OwnResidual();
        var active = _bed.LocalSession(ended: false);
        var before = _bed.Local(labels[0]);

        var result = await _bed.Router.CancelQueueAsync(labels, CancelReasonCodes.QueueRemoved);

        result.PendingLabelIds.Should().Equal(labels);
        result.Applied.Should().BeEmpty();
        result.HasProblems.Should().BeFalse();
        var row = _bed.Pending.List().Should().ContainSingle().Subject;
        row.Kind.Should().Be("cancel");
        row.LabelIds.Should().Equal(labels);
        _bed.Local(labels[0]).Should().BeEquivalentTo(before, "etiket satırı değişmez");
        _bed.Labels.GetQueue(session).Should().BeEmpty();
        _bed.LabelRepo.GetUnprintedOutsideSession(active).Should().BeEmpty("yayın açılışında yeniden teklif edilmez");
        _bed.HttpCount.Should().Be(0);
    }

    [Fact]
    public async Task Komut_ucu_yokken_kalinti_iptali_bekleyen_komut_olur()
    {
        _bed.ModeOn();
        var (_, _, labels) = _bed.OwnResidual();
        _bed.Connectivity.Report(SalesCommandStatus.EndpointMissing);

        var result = await _bed.Router.CancelQueueAsync(labels, CancelReasonCodes.QueueRemoved);

        result.PendingLabelIds.Should().Equal(labels);
        _bed.Pending.List().Should().ContainSingle();
        _bed.Server.CommandCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task Gonderim_sonucu_bilinmezse_ayni_kimlik_kuyrukta_kalir()
    {
        _bed.ModeOn();
        var (_, _, labels) = _bed.OwnResidual();
        _bed.Server.EnqueueStatus(FakeSalesServer.Commands, 503);

        var result = await _bed.Router.CancelQueueAsync(labels, CancelReasonCodes.QueueRemoved);

        result.PendingLabelIds.Should().Equal(labels);
        var sent = _bed.Server.CommandCalls.Should().ContainSingle().Subject.Command.CommandId;
        var row = _bed.Pending.List().Should().ContainSingle().Subject;
        row.CommandId.Should().Be(sent!.Value.ToString("N"), "sabit kimlik: tur aynı komutu yineler");
        row.Attempts.Should().Be(1);
    }

    [Fact]
    public async Task Karisik_secim_bolunur_canli_etiket_yerelde_kalinti_komutla()
    {
        _bed.ModeOn();
        var live = ActiveLabel(out _);
        var (_, _, labels) = _bed.OwnResidual();

        var result = await _bed.Router.CancelQueueAsync([live, labels[0]], CancelReasonCodes.QueueRemoved);

        result.Applied.Should().BeEquivalentTo([live, labels[0]]);
        _bed.Local(live).CancelledAt.Should().NotBeNull();
        _bed.Local(live).SyncedAt.Should().BeNull("yerel yazım gönderim kuyruğuna girer");
        _bed.Server.CommandCalls.Should().ContainSingle().Which.Command.Payload!.Value
            .GetProperty("items").EnumerateArray().Select(e => e.GetGuid().ToString("N")).Should().Equal(labels);
    }

    [Fact]
    public async Task Baska_bilgisayarin_yayini_cevrimdisi_bekleyen_komuta_girmez()
    {
        _bed.ModeOn();
        var (_, _, remote) = await _bed.RemoteLabelAsync(printed: false);
        _bed.Online = false;

        var result = await _bed.Router.CancelQueueAsync([N(remote)], CancelReasonCodes.QueueRemoved);

        result.PendingLabelIds.Should().BeEmpty();
        result.ProblemText.Should().Be("Bu değişiklik için sunucu bağlantısı gerekli.");
        _bed.Pending.List().Should().BeEmpty();
    }

    // ── kuyruk baskısı ────────────────────────────────────────────────────

    [Fact]
    public async Task Karisik_kagit_tek_basis_kimligi_ve_kesintisiz_sira_tasir()
    {
        _bed.ModeOn();
        var live = ActiveLabel(out _);
        var (_, _, residual) = _bed.OwnResidual(count: 2);

        var result = await _bed.Router.MarkPrintedAsync([residual[0], live, residual[1]]);

        result.Marked.Should().BeEquivalentTo([residual[0], live, residual[1]]);
        result.Pending.Should().BeEmpty();
        var command = _bed.Server.CommandCalls.Should().ContainSingle().Subject.Command;
        command.Kind.Should().Be("mark-printed");
        var payload = command.Payload!.Value;
        var batchId = payload.GetProperty("batchId").GetString();
        payload.GetProperty("items").EnumerateArray()
            .ToDictionary(i => i.GetProperty("orderId").GetGuid().ToString("N"), i => i.GetProperty("seq").GetInt32())
            .Should().BeEquivalentTo(new Dictionary<string, int> { [residual[0]] = 1, [residual[1]] = 3 });
        Print(live).BatchId.Should().Be(batchId, "yerel satır aynı basışta");
        Print(live).Seq.Should().Be(2);
        Print(live).PrintedAt.Should().NotBeNull();
        Print(residual[0]).BatchId.Should().Be(batchId, "sunucunun damgası yanıttan");
        Print(residual[1]).Seq.Should().Be(3);
        _bed.Pending.List().Should().BeEmpty();
    }

    [Fact]
    public async Task Cevrimdisi_kalinti_baskisi_bekleyen_mark_printed_olur_yeniden_teklif_edilmez()
    {
        _bed.ModeOn();
        _bed.Online = false;
        var live = ActiveLabel(out var activeSession);
        var (_, _, residual) = _bed.OwnResidual();

        var result = await _bed.Router.MarkPrintedAsync([live, residual[0]]);

        result.Marked.Should().Equal(live);
        result.Pending.Should().Equal(residual);
        var row = _bed.Pending.List().Should().ContainSingle().Subject;
        row.Kind.Should().Be("mark-printed");
        using var doc = JsonDocument.Parse(row.PayloadJson);
        var payload = doc.RootElement.GetProperty("payload");
        payload.GetProperty("batchId").GetString().Should().Be(Print(live).BatchId, "aynı basış");
        payload.GetProperty("items")[0].GetProperty("seq").GetInt32().Should().Be(2);
        doc.RootElement.GetProperty("expected")[0].GetProperty("version").GetInt32().Should().Be(1);
        Print(residual[0]).PrintedAt.Should().BeNull("sunucu otoritesindeki satır yerelde yazılmaz");
        _bed.LabelRepo.GetUnprintedOutsideSession(activeSession).Should().BeEmpty("kâğıt ikinci kez teklif edilmez");
        _bed.HttpCount.Should().Be(0);
    }

    // ── başka bilgisayarın yayınındaki satırın kâğıdı (sahip kararı Y11) ────

    /// <summary>Satış akışının bir turu (bekleyen komutlar akıştan önce gider); sahte sunucu her isteği işleyebilmeli.</summary>
    private async Task TourAsync()
    {
        _bed.Online = true;
        _bed.StateReceivedAt = DateTimeOffset.UtcNow;
        (await Task.Run(() => _bed.CreatePull().TourOnceAsync(CancellationToken.None))).Should().Be(SalesPullOutcome.CaughtUp);
        _bed.Server.Faults.Should().BeEmpty();
    }

    private static Guid CommandIdOf(FakeSalesServer.PushCall call) => call.Command.CommandId!.Value;

    [Fact]
    public async Task Baska_bilgisayarin_satirinin_baski_onayi_zaman_asiminda_bekleyen_komut_kalir_tur_ayni_kimlikle_gonderir_bir_kez_uygulanir()
    {
        _bed.ModeOn();
        var (_, _, order) = await _bed.RemoteLabelAsync(printed: false);
        var remote = N(order);
        _bed.Server.LoseNextResponse(FakeSalesServer.Commands);       // sunucu uygular, yanıt kaybolur (zaman aşımı)

        var result = await _bed.Router.MarkPrintedAsync([remote]);

        result.Pending.Should().Equal(remote);
        result.Notices.Should().BeEmpty("sonucu bilinmeyen baskı onayı kaybolmaz: \"işlenemedi\" notu yok");
        var row = _bed.Pending.List().Should().ContainSingle().Subject;
        row.Kind.Should().Be("mark-printed");
        row.LabelIds.Should().Equal(remote);
        var sent = CommandIdOf(_bed.Server.CommandCalls.Should().ContainSingle().Subject);
        row.CommandId.Should().Be(N(sent), "sabit kimlik: tur aynı komutu yineler");
        _bed.Server.Order(order)!.Row.PrintedAt.Should().NotBeNull("ön koşul: sunucu uyguladı");
        _bed.LabelRepo.GetById(remote)!.PrintedAt.Should().BeNull("yanıt kayboldu");
        _bed.LabelRepo.GetPrintableByIds([remote]).Should().BeEmpty("örtü: kâğıt yeniden teklif edilmez");

        await TourAsync();

        _bed.Server.CommandCalls.Select(CommandIdOf).Should().Equal(sent, sent);
        _bed.Server.CommandLog(sent)!.Applications.Should().Be(1, "sunucu aynı kimliği ikinci kez uygulamaz");
        _bed.Server.CommandLog(sent)!.Replays.Should().Be(1);
        _bed.LabelRepo.GetById(remote)!.PrintedAt.Should().NotBeNull("yeniden oynatmanın yanıtından");
        _bed.Pending.List().Should().BeEmpty();
        _bed.Settled.SelectMany(s => s.Warnings).Should().BeEmpty();
        _bed.Settled.SelectMany(s => s.DiscardLabelIds).Should().BeEmpty();
    }

    [Fact]
    public async Task Cevrimdisi_basilan_baska_bilgisayarin_satiri_bekleyen_mark_printed_olur_ortulur_baglaninca_gonderilir()
    {
        _bed.ModeOn();
        var live = ActiveLabel(out _);
        var (remoteSession, _, order) = await _bed.RemoteLabelAsync(printed: false);
        var remote = N(order);
        var (_, _, own) = _bed.OwnResidual();
        _bed.Online = false;
        var requests = _bed.HttpCount;

        var result = await _bed.Router.MarkPrintedAsync([live, remote, own[0]]);

        result.Marked.Should().Equal(live);
        result.Pending.Should().BeEquivalentTo([remote, own[0]]);
        result.Notices.Should().BeEmpty();
        _bed.HttpCount.Should().Be(requests, "çevrimdışı: istek yok");
        var rows = _bed.Pending.List();
        rows.Should().HaveCount(2, "kendi kalıntısı ve başka bilgisayarın satırı ayrı bekleyen komut (Off kuralı farklı)");
        rows.Select(r => r.LabelIds.Single()).Should().BeEquivalentTo([remote, own[0]]);
        var batch = Print(live).BatchId;
        foreach (var r in rows)
        {
            using var doc = JsonDocument.Parse(r.PayloadJson);
            var payload = doc.RootElement.GetProperty("payload");
            payload.GetProperty("batchId").GetString().Should().Be(batch, "tek basış");
            var seq = payload.GetProperty("items")[0].GetProperty("seq").GetInt32();
            seq.Should().Be(r.LabelIds.Single() == remote ? 2 : 3, "kâğıt sırası");
        }
        _bed.LabelRepo.GetPrintableByIds([remote]).Should().BeEmpty("baskı öncesi yeniden okumada görünmez");
        _bed.LabelRepo.GetUnprintedBySession(N(remoteSession)).Should().BeEmpty("yayının kuyruğunda görünmez");
        // Yalnız kendi kalıntısı için anlamlı: bu sorgu Origin='local' oturumlara bakar, uzak satır örtüsüz de girmez.
        // Uzak satırın örtüsünü yukarıdaki iki sorgu kanıtlar.
        _bed.LabelRepo.GetUnprintedOutsideSession(Guid.NewGuid().ToString("N")).Should().BeEmpty(
            "kendi kalıntısı örtü altında teklif edilmez");

        await TourAsync();

        Print(remote).PrintedAt.Should().NotBeNull("bağlantı gelince sunucuda işlendi, yanıttan yazıldı");
        Print(remote).BatchId.Should().Be(batch);
        Print(remote).Seq.Should().Be(2);
        _bed.Server.Order(order)!.Row.PrintBatchId.Should().Be(batch);
        _bed.Pending.List().Should().BeEmpty();
    }

    [Fact]
    public async Task Basildiktan_sonra_baska_bilgisayarda_iptal_edilen_satirin_bekleyen_onayi_imha_bildirimiyle_duser()
    {
        _bed.ModeOn();
        var (_, _, order) = await _bed.RemoteLabelAsync(printed: false);
        var remote = N(order);
        _bed.Online = false;
        (await _bed.Router.MarkPrintedAsync([remote])).Pending.Should().Equal(remote);
        _bed.Server.MutateOrder(order, o => o with { CancelledAt = T0.AddHours(2), CancelReason = "musteri" });

        await TourAsync();

        _bed.Settled.SelectMany(s => s.DiscardLabelIds).Should().Equal(new[] { remote }, "\"kâğıdı imha et\"");
        _bed.Settled.SelectMany(s => s.AlreadyPrintedLabelIds).Should().BeEmpty("sebep iptal");
        _bed.Pending.List().Should().BeEmpty();
        _bed.Local(remote).CancelledAt.Should().NotBeNull();
        Print(remote).PrintedAt.Should().BeNull("iptal edilmiş siparişe baskı alınmaz");
    }

    [Fact]
    public async Task Bekleyen_uzak_baski_onayi_bayatsa_taze_surumle_bir_kez_yeniden_denenir_uygulanir()
    {
        _bed.ModeOn();
        var (_, _, order) = await _bed.RemoteLabelAsync(printed: false);
        var remote = N(order);
        _bed.Online = false;
        (await _bed.Router.MarkPrintedAsync([remote])).Pending.Should().Equal(remote);
        _bed.Server.MutateOrder(order, o => o with { Price = 175m });   // başka bilgisayarın fiyat düzeltmesi: sürüm 2

        await TourAsync();

        _bed.Server.CommandCalls.Select(CommandIdOf).Distinct().Should().ContainSingle("aynı kimlik, taze sürüm");
        _bed.Server.CommandCalls.Should().HaveCount(2, "stale, ardından bir kez yeniden deneme");
        Print(remote).PrintedAt.Should().NotBeNull();
        _bed.Local(remote).Price.Should().Be(175m);
        _bed.Pending.List().Should().BeEmpty();
    }

    [Fact]
    public async Task Bekleyen_uzak_baski_onayi_guncelleme_gerekliyse_kuyrukta_kalir_arka_planda_soru_acilmaz()
    {
        _bed.ModeOn();
        var (_, _, order) = await _bed.RemoteLabelAsync(printed: false);
        var remote = N(order);
        _bed.Online = false;
        (await _bed.Router.MarkPrintedAsync([remote])).Pending.Should().Equal(remote);
        _bed.Server.MinClientVersion = "0.9.12";

        await TourAsync();

        var row = _bed.Pending.List().Should().ContainSingle().Subject;
        row.LabelIds.Should().Equal(remote);
        row.LastError.Should().Be(nameof(SalesCommandStatus.ClientUpdateRequired));
        _bed.UpdatePrompts.Should().Be(0, "arka plandaki bekleyen komut güncelleme sorusunu açmaz");
        _bed.LabelRepo.GetPrintableByIds([remote]).Should().BeEmpty("örtü sürer");

        _bed.Server.MinClientVersion = "0.9.11";
        await TourAsync();
        Print(remote).PrintedAt.Should().NotBeNull();
        _bed.Pending.List().Should().BeEmpty();
    }

    [Fact]
    public async Task Bekleyen_uzak_baski_onayi_kalici_retle_duser_iz_ve_uyari()
    {
        _bed.ModeOn();
        var (_, _, order) = await _bed.RemoteLabelAsync(printed: false);
        var remote = N(order);
        _bed.Online = false;
        (await _bed.Router.MarkPrintedAsync([remote])).Pending.Should().Equal(remote);
        _bed.Server.EnqueueProblem(FakeSalesServer.Commands, 409, "content-conflict");

        await TourAsync();

        _bed.Pending.List().Should().BeEmpty();
        _bed.Settled.SelectMany(s => s.Warnings).Should().Equal(
            SalesCommandMessages.PendingRejected("mark-printed", 1, SalesCommandMessages.ContentConflict));
        _bed.Scalar<long>("SELECT COUNT(*) FROM SalesConflictLog").Should().Be(1);
    }

    [Fact]
    public async Task Karar_ile_kuyruk_arasinda_Off_turu_islenirse_uzak_satirin_kagidi_sayili_uyari_olur_yerel_yazim_yok()
    {
        _bed.ModeOn();
        var live = ActiveLabel(out _);
        var (_, _, order) = await _bed.RemoteLabelAsync(printed: false);
        var remote = N(order);
        // Off turunun 2. adımı, kâğıdın yerel yazımıyla bekleyen komut arasında işlenir: yerel kip 0, uzak satırlar silinir.
        _bed.Exec("CREATE TRIGGER TestOffRoundStep2 AFTER UPDATE OF PrintedAt ON Label WHEN new.Id = '" + live + "' "
                + "BEGIN UPDATE SalesSyncState SET Mode = 0 WHERE Id = 1; "
                + "DELETE FROM Label WHERE Id = '" + remote + "'; END");

        var result = await _bed.Router.MarkPrintedAsync([live, remote]);

        result.Marked.Should().Equal(new[] { live }, "uzak satır yerelde işaretlenmiş sayılmaz");
        result.Notices.Should().Equal(SalesCommandMessages.PrintUnrecordedModeOff(1));
        _bed.Pending.List().Should().BeEmpty();
        _bed.Server.CommandCalls.Should().BeEmpty();
        _bed.Scalar<long>("SELECT COUNT(*) FROM SalesConflictLog WHERE Kind = @k AND Reason LIKE @r",
                new { k = SalesConflictLog.PendingCommandRejected, r = "mark-printed; command=%; code=remote-mode-off; labels=1" })
            .Should().Be(1, "Off turunun 3. adımıyla aynı iz: sessiz kayıp yok (§5.5)");
    }

    // ── sunucunun tek komut tavanı (500) ───────────────────────────────────

    private static int ItemCount(FakeSalesServer.CommandBody command) =>
        command.Payload!.Value.GetProperty("items").GetArrayLength();

    [Fact]
    public async Task Tavani_asan_kalinti_baskisi_500luk_bekleyen_komutlara_bolunur_ayni_basis_ve_kesintisiz_sira()
    {
        _bed.ModeOn();
        var (_, _, residual) = _bed.OwnResidual(count: 501);

        var result = await _bed.Router.MarkPrintedAsync(residual);
        // Anlık gönderim bütçesi (3 sn) yük altında aşılabilir: kalan parçalar kuyrukta, tur götürür.
        await _bed.Sender.FlushAsync(CancellationToken.None);

        result.Notices.Should().BeEmpty("tek komut 500'ü aşsaydı 400 alır, komut düşerdi");
        result.Marked.Concat(result.Pending).Should().BeEquivalentTo(residual, "her satır işaretli ya da örtülü");
        var calls = _bed.Server.CommandCalls.Select(c => c.Command).ToList();
        calls.Select(c => (c.Kind, ItemCount(c))).Should().Equal([("mark-printed", 500), ("mark-printed", 1)]);
        calls.Select(c => c.CommandId).Should().OnlyHaveUniqueItems("her parça kendi bekleyen komutu");
        calls.Select(c => c.Payload!.Value.GetProperty("batchId").GetString()).Distinct().Should().ContainSingle(
            "tek basış kimliği");
        calls[1].Payload!.Value.GetProperty("items")[0].GetProperty("seq").GetInt32().Should().Be(501, "sıra kesintisiz");
        Print(residual[^1]).PrintedAt.Should().NotBeNull();
        Print(residual[^1]).Seq.Should().Be(501);
        _bed.Pending.List().Should().BeEmpty();
    }

    [Fact]
    public async Task Tavani_asan_kalinti_iptali_parca_parca_komutla_uygulanir()
    {
        _bed.ModeOn();
        var (_, _, residual) = _bed.OwnResidual(count: 501);

        var result = await _bed.Router.CancelQueueAsync(residual, CancelReasonCodes.QueueRemoved);
        // Anlık gönderim bütçesi (3 sn) yük altında aşılabilir: kalan parçalar kuyrukta, tur götürür.
        await _bed.Sender.FlushAsync(CancellationToken.None);

        result.HasProblems.Should().BeFalse();
        result.NoticeTexts.Should().BeEmpty("tek komut 500'ü aşsaydı 400 alır, komut düşerdi");
        result.Applied.Concat(result.PendingLabelIds).Should().BeEquivalentTo(residual, "her satır uygulandı ya da örtülü");
        _bed.Server.CommandCalls.Select(c => (c.Command.Kind, ItemCount(c.Command))).Should().Equal(
            [("cancel", 500), ("cancel", 1)]);
        _bed.Local(residual[^1]).CancelledAt.Should().NotBeNull();
        _bed.Pending.List().Should().BeEmpty();
    }

    [Fact]
    public async Task Tavani_asan_kalinti_cevrimdisi_her_parca_ayri_bekleyen_komut_olur()
    {
        _bed.ModeOn();
        _bed.Online = false;
        var (_, _, residual) = _bed.OwnResidual(count: 501);

        var result = await _bed.Router.CancelQueueAsync(residual, CancelReasonCodes.QueueRemoved);

        result.PendingLabelIds.Should().HaveCount(501);
        _bed.Pending.List().Select(r => r.LabelIds.Count).Should().Equal([500, 1]);
        _bed.HttpCount.Should().Be(0);
    }

    [Fact]
    public async Task Tavani_asan_kalintida_ilk_parcanin_sonucu_bilinmezse_ikincisi_gonderilmez_kuyrukta_bekler()
    {
        _bed.ModeOn();
        var (_, _, residual) = _bed.OwnResidual(count: 501);
        _bed.Server.EnqueueStatus(FakeSalesServer.Commands, 503);

        var result = await _bed.Router.MarkPrintedAsync(residual);

        result.Pending.Should().HaveCount(501);
        result.Notices.Should().BeEmpty();
        _bed.Server.CommandCalls.Should().ContainSingle("sonucu bilinmeyen parçadan sonrası tur gönderir");
        _bed.Pending.List().Select(r => r.LabelIds.Count).Should().Equal([500, 1]);
        _bed.LabelRepo.GetUnprintedOutsideSession(Guid.NewGuid().ToString("N")).Should().BeEmpty(
            "iki parça da örtülü: kâğıt yeniden teklif edilmez");
    }

    [Fact]
    public async Task Tavani_asan_baska_bilgisayarin_kuyruk_iptali_parca_parca()
    {
        _bed.ModeOn();
        var session = _bed.RemoteSession();
        var customer = _bed.RemoteCustomer();
        var ids = Enumerable.Range(0, 501)
            .Select(_ => _bed.RemoteOrder(session, customer, printed: false).ToString("N")).ToList();
        await _bed.PullAsync();

        var result = await _bed.Router.CancelQueueAsync(ids, CancelReasonCodes.QueueRemoved);

        result.HasProblems.Should().BeFalse();
        result.Applied.Should().HaveCount(501);
        _bed.Server.CommandCalls.Select(c => (c.Command.Kind, ItemCount(c.Command))).Should().Equal(
            [("cancel", 500), ("cancel", 1)]);
    }

    [Fact]
    public async Task Sunucuda_iptal_edilmis_kalintinin_kagidi_imha_listesinde()
    {
        _bed.ModeOn();
        var (_, _, residual) = _bed.OwnResidual();
        _bed.Server.MutateOrder(Guid.ParseExact(residual[0], "N"),
            o => o with { CancelledAt = T0.AddHours(2), CancelReason = "musteri" });

        var result = await _bed.Router.MarkPrintedAsync(residual);

        result.DiscardLabelIds.Should().Equal(residual);
        _bed.Local(residual[0]).CancelledAt.Should().NotBeNull();
        _bed.Pending.List().Should().BeEmpty();
    }

    /// <summary>Yerel servisin yazma işlemini açarken bir kez araya giren yazım (akışın devir kapısı dışındaki yazımı).</summary>
    private sealed class InterleavingFactory(IDbConnectionFactory inner) : IDbConnectionFactory
    {
        public Action<IDbConnection>? BeforeNextOpen;

        public IDbConnection Open()
        {
            if (Interlocked.Exchange(ref BeforeNextOpen, null) is { } interleave)
            {
                using var other = inner.Open();
                interleave(other);
            }
            return inner.Open();
        }
    }

    private sealed class Clock : IClock
    {
        public long UnixNow() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    }

    [Fact]
    public async Task Akis_karar_ile_yerel_yazim_arasinda_devri_yazarsa_kagit_bekleyen_komuta_gecer_ABORT_disari_cikmaz()
    {
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var interleaving = new InterleavingFactory(db);
        var clock = new Clock();
        var labelRepo = new LabelRepository(db);
        var labels = new LabelService(labelRepo,
            new CustomerService(new CustomerRepository(db), new SessionRepository(db), labelRepo, clock), interleaving, clock);
        using var bed = new SalesEditTestBed(db, labels);
        bed.ModeOn();
        bed.Online = false;
        var session = bed.LocalSession(ended: true);              // kendi bitmiş, devri yerelde bilinmeyen yayını
        var label = bed.LocalLabel(session, Guid.NewGuid().ToString("N"), printed: false);
        // Kapanış yanıtı kaybolmuştu; akış turu devri tam karar ile yazım arasında yazar (devir kapısını almaz).
        interleaving.BeforeNextOpen = c => c.Execute(
            "UPDATE StreamSession SET ReleasedAt = 990, Authority = 'server' WHERE Id = @session", new { session });

        var result = await bed.Router.MarkPrintedAsync([label]);

        result.Pending.Should().Equal(new[] { label }, "karar yeniden verildi: artık sunucu satırı, bekleyen mark-printed");
        bed.LabelRepo.GetById(label)!.PrintedAt.Should().BeNull("yerel yazım geri çevrildi, hiçbir şey yazılmadı");
        bed.Pending.List().Should().ContainSingle().Which.Kind.Should().Be("mark-printed");
        bed.LabelRepo.GetUnprintedOutsideSession(Guid.NewGuid().ToString("N")).Should().BeEmpty(
            "örtü: kâğıt yeniden teklif edilmez");
    }

    [Fact]
    public async Task Baski_oncesi_yalniz_cevrimdisiyken_baska_bilgisayarin_satiri_tutulur()
    {
        _bed.ModeOn();
        var (_, _, remoteOrder) = await _bed.RemoteLabelAsync(printed: false);
        var remote = remoteOrder.ToString("N");
        var (_, _, own) = _bed.OwnResidual();
        var live = ActiveLabel(out _);

        _bed.Router.PrintHeldRows([remote, own[0], live]).Should().BeEmpty("çevrimiçi: komutla işaretlenir");
        _bed.Online = false;
        _bed.Router.PrintHeldRows([remote, own[0], live]).Should().BeEquivalentTo([remote],
            "kendi kalıntısı bekleyen komuta girer, canlı satır yerelde; yalnız başka bilgisayarınki kayıtsız kalırdı");
        _bed.ModeOff();
        _bed.Router.PrintHeldRows([remote, own[0], live]).Should().BeEmpty("mod Off: bugünkü yerel yol");
    }

    [Fact]
    public async Task Off_turu_isaretlemeyle_cakisirsa_kalinti_ayni_basis_ve_sirayla_yerelde_isaretlenir()
    {
        _bed.ModeOn();
        var live = ActiveLabel(out var activeSession);
        var (_, _, residual) = _bed.OwnResidual();
        // Off turunun 2. adımı, kâğıdın yerel yazımıyla kalıntının bekleyen komutu arasında işlenir: yerel kip 0 ve kendi
        // yayınları yerel otoritede (ApplyModeOff'un tek işlemi).
        _bed.Exec("CREATE TRIGGER TestOffRoundStep2 AFTER UPDATE OF PrintedAt ON Label WHEN new.Id = '" + live + "' "
                + "BEGIN UPDATE SalesSyncState SET Mode = 0 WHERE Id = 1; "
                + "UPDATE StreamSession SET Authority = 'local' WHERE Origin = 'local'; END");

        var result = await _bed.Router.MarkPrintedAsync([live, residual[0]]);

        result.Marked.Should().BeEquivalentTo([live, residual[0]]);
        result.Notices.Should().BeEmpty();
        Print(residual[0]).PrintedAt.Should().NotBeNull("kâğıt basıldı: işaret kaybolmaz");
        Print(residual[0]).BatchId.Should().Be(Print(live).BatchId, "aynı basış");
        Print(residual[0]).Seq.Should().Be(2);
        _bed.Pending.List().Should().BeEmpty();
        _bed.LabelRepo.GetUnprintedOutsideSession(activeSession).Should().BeEmpty("kâğıt ikinci kez teklif edilmez");
    }

    [Fact]
    public async Task Kapanis_karari_surerken_isaretleme_kilidi_bekler_ve_karari_kilit_altinda_yeniden_verir()
    {
        _bed.ModeOn();
        _bed.Online = false;
        var session = _bed.LocalSession(ended: true);
        var label = _bed.LocalLabel(session, Guid.NewGuid().ToString("N"), printed: false);
        // Bekleme bütçesi açıkça uzun: yavaş koşuda §5.6-2'nin 3 sn'si dolup ertelenmiş dala düşülmesin (c8167d08).
        _bed.Router.HandoverWaitBudget = TestPatience.Upper;
        var held = await _bed.Gate.EnterAsync(session);

        var mark = _bed.Router.MarkPrintedAsync([label]);
        await Task.Delay(200);
        mark.IsCompleted.Should().BeFalse("kapanış gönderimi kilidi tutuyor");
        // Kapanış o arada devri gördü: yayın sunucu otoritesine geçti.
        _bed.Exec("UPDATE StreamSession SET ReleasedAt = 990, Authority = 'server' WHERE Id = @session", new { session });
        held.Dispose();
        var result = await mark;

        result.Pending.Should().Equal(new[] { label }, "kilit altında yeniden karar: artık sunucu satırı");
        Print(label).PrintedAt.Should().BeNull("ABORT'a varmadı");
        _bed.Pending.List().Should().ContainSingle().Which.Kind.Should().Be("mark-printed");
    }

    [Fact]
    public async Task Kapanis_kilidi_tutulurken_karisik_kagitta_canli_etiket_kilidi_beklemeden_hemen_isaretlenir()
    {
        _bed.ModeOn();
        _bed.Online = false;
        var live = ActiveLabel(out _);
        var session = _bed.LocalSession(ended: true);              // kendi bitmiş, devredilmemiş yayını: kapılı
        var gated = _bed.LocalLabel(session, Guid.NewGuid().ToString("N"), printed: false);
        _bed.Router.HandoverWaitBudget = TestPatience.Upper;       // ertelenmiş dala düşülmesin (yavaş koşu)
        var held = await _bed.Gate.EnterAsync(session);            // kapanış gönderimi sessions/sync'te asılı

        var mark = _bed.Router.MarkPrintedAsync([gated, live]);

        Print(live).PrintedAt.Should().NotBeNull("canlı satır devir kilidine uğramaz: kilitlerden önce, kendi işleminde");
        Print(gated).PrintedAt.Should().BeNull("kapılı satır kapanış kararı sürerken kilitsiz yazılmaz (M2)");
        mark.IsCompleted.Should().BeFalse("kapılı satır kilidi bekliyor");
        held.Dispose();
        var result = await mark.WaitAsync(TestPatience.Upper);

        result.Marked.Should().BeEquivalentTo([gated, live]);
        result.Pending.Should().BeEmpty();
        Print(gated).BatchId.Should().Be(Print(live).BatchId, "tek basış: parçalar ayrı işlemlerde, kimlik aynı");
        Print(gated).Seq.Should().Be(1);
        Print(live).Seq.Should().Be(2, "kâğıt sırası kesintisiz");
        _bed.Gate.EntryCount.Should().Be(0, "kilit yazımdan sonra bırakılır");
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TestPatience.Upper;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"koşul {TestPatience.Upper.TotalSeconds} sn'de sağlanmadı");
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task Kapanis_kilidi_suresinde_birakilmazsa_isaretleme_beklemez_kapili_satir_kilit_acilinca_ayni_basisla_isaretlenir()
    {
        _bed.ModeOn();
        _bed.Online = false;
        var live = ActiveLabel(out _);
        var session = _bed.LocalSession(ended: true);
        var gated = _bed.LocalLabel(session, Guid.NewGuid().ToString("N"), printed: false);
        _bed.Router.HandoverWaitBudget = TimeSpan.FromMilliseconds(100);   // §5.6-2'nin 3 sn'si, kısaltılmış
        var settled = DeferredSettlement();
        var held = await _bed.Gate.EnterAsync(session);            // kapanış gönderimi sessions/sync'te asılı

        var result = await _bed.Router.MarkPrintedAsync([gated, live]).WaitAsync(TimeSpan.FromSeconds(10));

        result.Marked.Should().Equal(live);
        result.Pending.Should().Equal(new[] { gated }, "kilit süresinde alınamadı: kilit açılınca işaretlenecek");
        Print(gated).PrintedAt.Should().BeNull("süre doldu diye kilitsiz yazılmaz (M2)");
        _bed.Pending.List().Should().BeEmpty("yerel otoriteli yayının satırı bekleyen komuta girmez");

        held.Dispose();
        await WaitUntil(() => Print(gated).PrintedAt is not null && _bed.Gate.EntryCount == 0);

        Print(gated).BatchId.Should().Be(Print(live).BatchId, "aynı basış");
        Print(gated).Seq.Should().Be(1);
        Print(live).Seq.Should().Be(2);
        _bed.Pending.List().Should().BeEmpty();
        var notice = await settled.WaitAsync(TestPatience.Upper);
        notice.ChangedLabelIds.Should().Equal(gated);
        notice.NeedsAttention.Should().BeFalse("işaret yazıldı: uyarılacak bir şey yok");
    }

    /// <summary>Yönlendiricinin arka planda biten kâğıt işaretinin sonucu (<c>DeferredPrintSettled</c>).</summary>
    private Task<PendingSalesCommandsSettled> DeferredSettlement()
    {
        var settled = new TaskCompletionSource<PendingSalesCommandsSettled>(TaskCreationOptions.RunContinuationsAsynchronously);
        _bed.Router.DeferredPrintSettled += (_, e) => settled.TrySetResult(e);
        return settled.Task;
    }

    [Fact]
    public async Task Suresi_dolan_kapili_satirin_isareti_arka_planda_yazilamazsa_sessizce_kaybolmaz_uyari_doner()
    {
        _bed.ModeOn();
        _bed.Online = false;
        var session = _bed.LocalSession(ended: true);
        var label = _bed.LocalLabel(session, Guid.NewGuid().ToString("N"), printed: false);
        _bed.Router.HandoverWaitBudget = TimeSpan.FromMilliseconds(100);
        var settled = DeferredSettlement();
        var held = await _bed.Gate.EnterAsync(session);

        var result = await _bed.Router.MarkPrintedAsync([label]).WaitAsync(TimeSpan.FromSeconds(10));
        result.Pending.Should().Equal(new[] { label });
        result.Notices.Should().BeEmpty("ön koşul: sonuç henüz yok");

        // Beklenmeyen veritabanı hatası (devrin sales-server-authority ABORT'u değil): kilit açılınca damga düşer.
        _bed.Exec("CREATE TRIGGER TestMarkFails BEFORE UPDATE OF PrintedAt ON Label WHEN new.Id = '" + label + "' "
                + "BEGIN SELECT RAISE(ABORT, 'test-db-failure'); END");
        held.Dispose();
        var notice = await settled.WaitAsync(TestPatience.Upper);

        notice.Warnings.Should().Equal(SalesCommandMessages.PrintNotWritten(1));
        notice.NeedsAttention.Should().BeTrue("kâğıdı basılan satırın kaydı yazılamadı: operatör görmeli");
        Print(label).PrintedAt.Should().BeNull();
        _bed.Gate.EntryCount.Should().Be(0, "kilit hata olsa da bırakılır");
    }

    [Fact]
    public async Task Suresi_dolan_kapili_satirin_yayini_beklerken_devredilirse_kilit_acilinca_bekleyen_mark_printed_olur()
    {
        _bed.ModeOn();
        _bed.Online = false;
        var session = _bed.LocalSession(ended: true);
        var label = _bed.LocalLabel(session, Guid.NewGuid().ToString("N"), printed: false);
        _bed.Router.HandoverWaitBudget = TimeSpan.FromMilliseconds(100);
        var held = await _bed.Gate.EnterAsync(session);

        var result = await _bed.Router.MarkPrintedAsync([label]).WaitAsync(TimeSpan.FromSeconds(10));
        result.Pending.Should().Equal(new[] { label });

        // Kapanış o arada devri gördü: yayın sunucu otoritesine geçti.
        _bed.Exec("UPDATE StreamSession SET ReleasedAt = 990, Authority = 'server' WHERE Id = @session", new { session });
        held.Dispose();
        await WaitUntil(() => _bed.Pending.List().Count == 1 && _bed.Gate.EntryCount == 0);

        var row = _bed.Pending.List().Single();
        row.Kind.Should().Be("mark-printed", "kilit altında yeniden karar: artık sunucu satırı");
        row.LabelIds.Should().Equal(label);
        Print(label).PrintedAt.Should().BeNull("ABORT'a varmadı");
        _bed.LabelRepo.GetUnprintedOutsideSession(Guid.NewGuid().ToString("N")).Should().BeEmpty(
            "örtü: kâğıt yeniden teklif edilmez, baskı kaybolmaz");
    }
}
