using FluentAssertions;
using OrderDeck.App.Services;
using OrderDeck.App.Services.Sync;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Licensing.Api.Models;
using Xunit;

namespace OrderDeck.Tests.Sync;

/// <summary>
/// İki bilgisayarlı ödeme yakınsaması (W3-12; spec 2026-10-13 §10.3 "kira: in-progress, already-requested, Unconfirmed,
/// members-stale", "ödeme geçmişi", "ön gönderim", "miras iş scope-has-deduction"; §6.8 sayısal örnek; §10.4-5/-9'un test
/// karşılıkları). Her bilgisayar AppHost'un örnek paylaşımıyla kurulmuş gerçek ödeme servisini, kira sarmalını, ödeme kaydı
/// göndericisini ve geçmiş geri doldurmasını koşturur (<see cref="SalesPc"/>); sunucu kuralları
/// <see cref="FakeSalesServer.LeaseRules"/>. Senaryolar sıra permütasyonlarıyla koşar; ödeme adımları nedenseldir.
/// </summary>
public sealed partial class TwoPcSalesConvergenceTests
{
    private const string Cumulative = "cumulative";

    /// <summary>Kişinin telefonu (mesaj gidebilsin): her bilgisayarın kendi kaydı.</summary>
    private static void Phone(SalesPc pc, string customerId) =>
        pc.Customers.UpdatePhone(customerId, OrderDeck.Tests.TestHelpers.TestPhone.NewE164()).Should().Be(1);

    /// <summary>Kartın tutarı: kişinin bütün üyelerinin kapsamdaki katkı veren etiketleri.</summary>
    private static decimal LocalTotal(SalesPc pc, string customerId, string scope) =>
        pc.LabelRepo.GetPrintedTotal(pc.Customers.GetPaymentPerson(customerId).MemberIds.ToList(),
            PaymentHistoryRecords.SessionIdOf(scope)).Total;

    /// <summary>Sunucunun toplamı (§6.4-f): kişinin basılmış, iptal edilmemiş, geçici yedek olmayan siparişleri.</summary>
    private decimal ServerTotal(params string[] customerIds) =>
        _server.AllOrders()
            .Where(o => customerIds.Contains(o.Row.CustomerId) && o.Row.PrintedAt is not null && o.Row.CancelledAt is null
                        && !o.Row.IsTentativeBackup)
            .Sum(o => o.Row.Price);

    private static Task<PaymentRequestOutcome> RequestAsync(SalesPc pc, string customerId, string scope,
        IReadOnlySet<string>? confirmed, CancellationToken ct = default)
        => pc.Payments.RequestPaymentAsync(pc.Customers.GetById(customerId)!,
            p => pc.LabelRepo.GetPrintedTotal(p.MemberIds.ToList(), PaymentHistoryRecords.SessionIdOf(scope)).Total,
            DateTime.Now, scope, confirmed, ct);

    private IReadOnlyList<System.Text.Json.JsonElement> Begins() =>
        _server.Calls(FakeSalesServer.PaymentRequestsBegin).Select(c => c.Json).ToList();

    private static string Message(PaymentRequestOutcome outcome) =>
        PaymentResultPresenter.PromptText(outcome.Prompt!, DateTimeOffset.Now).Message;

    // ── §6.8: iki bilgisayar, bir kişi ─────────────────────────────────────

    /// <summary>
    /// §6.8 sayısal örneği: lisans geneli ayar eşik 500, ücret 60; kişi T (GUID'li sahip, telefonlu) ve I, aynı grup; PC-A
    /// yayın, PC-B ofis bilgisayarı. Dün S1 devredildi (350, iki bilgisayarda da). Bugün S2 canlıyken PC-B'nin kümülatif
    /// isteği <c>stale-total {470, liveElsewhere}</c> alır ve mesaj gitmez; yayın bitip devredilince ikisi de 550 görür.
    /// Ertesi gün aynı anda istenir: PC-A'nın kirası (120 sn) sürerken PC-B <c>in-progress-elsewhere {PC-A}</c>; PC-A 550 ≥
    /// 500 → ücretsiz kargo, 550 teslim; PC-B yeniden dener → "PC-A'dan 550,00 TL istendi (…); tekrar gönderilsin mi?".
    /// </summary>
    [Theory]
    [MemberData(nameof(Orders))]
    public async Task Sayisal_ornek_canli_yayinda_stale_total_es_zamanli_istekte_in_progress_sonra_PC_A_dan_istendi(
        string order)
    {
        Use(order);
        var (a, b) = await PcsAsync(modeOn: false);
        await EnableAsync(new FakeSalesServer.ShippingBody(3, 500m, 60m), a, b);

        // Dün: S1 (PC-A) — T K1 150 ve I K2 200 basıldı; S1 devredildi.
        var s1 = a.StartStream();
        var k1 = a.Sell(s1, "ornek_kisi", "K1", 150m);
        var k2 = a.Sell(s1, "ornek_kisi", "K2", 200m, platform: "instagram");
        await a.PrintAsync(k1.Id, k2.Id);
        a.EndStream(s1);
        var (t, i) = (k1.CustomerId, k2.CustomerId);
        var group = a.Customers.MergeIntoGroup([t, i])!;
        await SyncAsync(a, b);
        _server.ServerGroupOf(t).Should().Be(group, "A'nın müşteri gönderimi grubu sunucuya taşıdı");
        b.Customers.SetGroupId(t, group);                                // müşteri akışı grubu getirdi (taklit)
        b.Customers.SetGroupId(i, group);
        Phone(a, t);
        Phone(b, t);
        LocalTotal(a, t, Cumulative).Should().Be(350m);
        LocalTotal(b, t, Cumulative).Should().Be(350m);
        ServerTotal(t, i).Should().Be(350m);

        // Bugün 21:10: S2 canlı — T K3 120 basıldı ve gönderildi; T K4 80 kuyrukta.
        var s2 = a.StartStream();
        var k3 = a.Sell(s2, "ornek_kisi", "K3", 120m);
        var k4 = a.Sell(s2, "ornek_kisi", "K4", 80m);
        await a.PrintAsync(k3.Id);
        await SyncAsync(a, b);
        LocalTotal(a, t, Cumulative).Should().Be(470m);
        LocalTotal(b, t, Cumulative).Should().Be(350m, "S2 B'de görünmez (Y1)");
        ServerTotal(t, i).Should().Be(470m);

        // 21:12: PC-B'nin kümülatif isteği — engel, mesaj yok.
        var live = await RequestAsync(b, t, Cumulative, null);
        live.Result.Should().Be(PaymentRequestResult.Blocked);
        live.Message.Should().Be(
            "Bu kişinin PC-A'da süren yayında etiketi var; istek yayın bitip devredilince yapılabilir.");
        Begins().Should().HaveCount(2, "stale-total bir satış turundan sonra bir kez yeniden denenir");
        Begins().Should().OnlyContain(x => x.GetProperty("productTotal").GetDecimal() == 350m);
        _server.PaymentRecords().Should().BeEmpty("ret kira yazmaz");
        b.Launcher.LaunchedUrls.Should().BeEmpty();

        // 23:40: PC-A yayını bitirir, K4 basılır; etiketler gider, yayın devredilir.
        await a.PrintAsync(k4.Id);
        a.EndStream(s2);
        await SyncAsync(a, b);
        LocalTotal(a, t, Cumulative).Should().Be(550m);
        LocalTotal(b, t, Cumulative).Should().Be(550m);
        ServerTotal(t, i).Should().Be(550m);

        // Ertesi gün 10:00:05 / :06 — aynı anda: PC-A'nın kirası sürerken (bakiye adımında) PC-B ister.
        var hold = _server.Hold(FakeSalesServer.BalancePreviewEndpoint);
        var first = RequestAsync(a, t, Cumulative, null);
        await hold.Arrived.WaitAsync(TimeSpan.FromSeconds(30));
        var lease = _server.PaymentRecords().Should().ContainSingle().Subject;
        lease.State.Should().Be("sending");
        lease.Machine.Should().Be("PC-A");
        (lease.LeaseUntil - lease.StartedAt).Should().Be(TimeSpan.FromSeconds(120));
        lease.Members.Should().BeEquivalentTo([t, i]);
        lease.PersonKey.Should().Be("g:" + group);

        var busy = await RequestAsync(b, t, Cumulative, null);

        busy.Result.Should().Be(PaymentRequestResult.PaymentInProgress);
        busy.Message.Should().Be("PC-A'da bu kişiye ödeme isteniyor — biraz sonra tekrar deneyin.");
        hold.Release();
        (await first).Result.Should().Be(PaymentRequestResult.Opened);
        var delivered = _server.PaymentRecord(lease.Id)!;
        delivered.State.Should().Be("delivered");
        delivered.NetAmount.Should().Be(550m, "550 ≥ 500: ücretsiz kargo");
        delivered.TotalBeforeBalance.Should().Be(550m);
        a.Launcher.LaunchedUrls.Should().ContainSingle();
        b.Launcher.LaunchedUrls.Should().BeEmpty();

        // 10:03: PC-B yeniden dener — soru, makine adıyla; mesaj gitmez.
        var asked = await RequestAsync(b, t, Cumulative, null);

        asked.Result.Should().Be(PaymentRequestResult.AlreadyRequested);
        var prior = asked.Prompt!.Requests.Should().ContainSingle().Subject;
        prior.Amount.Should().Be(550m);
        prior.Machine.Should().Be("PC-A");
        prior.Unconfirmed.Should().BeFalse();
        Message(asked).Should().StartWith("PC-A'dan 550,00 TL istendi (").And.EndWith("); tekrar gönderilsin mi?");
        b.Launcher.LaunchedUrls.Should().BeEmpty();
        await AssertConvergedAsync(a, b);
    }

    /// <summary>
    /// §6.8'in son cümlesi: PC-A'nın süreci teslimden önce çöker (kira alındı, mesaj aşamasında süreç gider) — kira 120 sn
    /// sonra dolar. PC-B'nin sonraki denemesi kaydı <c>Unconfirmed</c> yapar ve "10:00'da PC-A'dan 550,00 TL istenmiş
    /// olabilir (sonucu bilinmiyor)" diye sorar; kapanış anı kira sonudur.
    /// </summary>
    [Theory]
    [MemberData(nameof(Orders))]
    public async Task Kira_dolar_PC_A_teslimden_once_cokerse_PC_B_istenmis_olabilir_diye_sorar(string order)
    {
        Use(order);
        var (a, b) = await PcsAsync(modeOn: false);
        await EnableAsync(new FakeSalesServer.ShippingBody(3, 500m, 60m), a, b);
        var s1 = a.StartStream();
        var k1 = a.Sell(s1, "ornek_kisi", "K1", 350m);
        var k2 = a.Sell(s1, "ornek_kisi", "K2", 200m);
        await a.PrintAsync(k1.Id, k2.Id);
        a.EndStream(s1);
        await SyncAsync(a, b);
        var t = k1.CustomerId;
        Phone(a, t);
        Phone(b, t);

        var hold = _server.Hold(FakeSalesServer.BalancePreviewEndpoint);
        using var crash = new CancellationTokenSource();
        var dying = RequestAsync(a, t, Cumulative, null, crash.Token);
        await hold.Arrived.WaitAsync(TimeSpan.FromSeconds(30));
        var lease = _server.PaymentRecords().Should().ContainSingle().Subject;

        _server.PaymentClock = () => DateTimeOffset.UtcNow.AddSeconds(121);  // kira doldu
        var asked = await RequestAsync(b, t, Cumulative, null);

        asked.Result.Should().Be(PaymentRequestResult.AlreadyRequested);
        var prior = asked.Prompt!.Requests.Should().ContainSingle().Subject;
        prior.Unconfirmed.Should().BeTrue("teslim kaydı olmadan dolan kira: gitmiş olabilir");
        prior.Amount.Should().Be(550m, "kiranın bakiye öncesi tutarı (net yok)");
        prior.Machine.Should().Be("PC-A");
        Message(asked).Should().EndWith("PC-A'dan 550,00 TL istenmiş olabilir (sonucu bilinmiyor); tekrar gönderilsin mi?");
        var expired = _server.PaymentRecord(lease.Id)!;
        expired.State.Should().Be("unconfirmed");
        expired.FinishedAt.Should().Be(lease.LeaseUntil, "kapanış anı kira sonu; B'nin begin anı değil");
        b.Launcher.LaunchedUrls.Should().BeEmpty();

        // Çöken süreç hiçbir şey göndermez: bağlantısı kopar, akış iptal olur.
        a.Offline = true;
        crash.Cancel();
        hold.Release();
        try { await dying; } catch (OperationCanceledException) { /* süreç gitti */ }
        a.Launcher.LaunchedUrls.Should().BeEmpty();
        _server.PaymentRecord(lease.Id)!.State.Should().Be("unconfirmed", "sonucu sunucuya hiç ulaşmadı");
    }

    // ── members-stale: grup senkronu gecikmesi ─────────────────────────────

    /// <summary>
    /// A, T ile I'yı birleştirir; grup sunucuya geçer ama B'ye henüz inmemiştir. B'nin isteği <c>members-stale</c> alır,
    /// bir müşteri akışı turundan sonra bir kez yeniden dener ve grup gelmediyse "Müşteri grupları eşitleniyor…" ile durur.
    /// Grup turda gelince kişi yeniden kurulur ve istek geçer: kira bütün üyeleri ve grup anahtarını taşır; A'nın sonraki
    /// isteği B'nin isteğini sorar.
    /// </summary>
    [Theory]
    [MemberData(nameof(Orders))]
    public async Task Grup_senkronu_gecikmesinde_members_stale_tur_sonrasi_grup_gelince_gecer_A_soruyu_gorur(string order)
    {
        Use(order);
        var (a, b) = await PcsAsync();
        var s1 = a.StartStream();
        var kt = a.Sell(s1, "grup_kisi", "K1", 150m);
        var ki = a.Sell(s1, "grup_kisi", "K2", 200m, platform: "instagram");
        await a.PrintAsync(kt.Id, ki.Id);
        a.EndStream(s1);
        await SyncAsync(a, b);
        var (t, i) = (kt.CustomerId, ki.CustomerId);
        var group = a.Customers.MergeIntoGroup([t, i])!;
        await SyncAsync(a, b);
        _server.ServerGroupOf(t).Should().Be(group, "A'nın müşteri gönderimi grubu sunucuya taşıdı");
        b.Customers.GetPaymentPerson(t).MemberIds.Should().Equal(new[] { t }, "ön koşul: grup B'ye inmedi");
        Phone(b, t);

        var stale = await RequestAsync(b, t, Cumulative, null);

        stale.Result.Should().Be(PaymentRequestResult.Blocked);
        stale.Message.Should().Be("Müşteri grupları eşitleniyor; biraz sonra tekrar dene.");
        Begins().Should().HaveCount(2, "bir müşteri akışı turundan sonra bir kez yeniden denendi");
        _server.PaymentRecords().Should().BeEmpty();

        // Grup bu turda iner (müşteri akışı; FakeCustomerServer'ın işi, burada turun içinde yerelde taklit).
        _server.DuringCustomerChanges = () =>
        {
            b.Customers.SetGroupId(t, group);
            b.Customers.SetGroupId(i, group);
        };
        var passed = await RequestAsync(b, t, Cumulative, null);
        _server.DuringCustomerChanges = null;

        passed.Result.Should().Be(PaymentRequestResult.Opened);
        var record = _server.PaymentRecords().Should().ContainSingle().Subject;
        record.Members.Should().BeEquivalentTo([t, i]);
        record.PersonKey.Should().Be("g:" + group);
        record.Machine.Should().Be("PC-B");
        record.State.Should().Be("delivered");
        record.NetAmount.Should().Be(410m, "350 + lisans geneli kargo 60 (eşik 750)");
        b.Launcher.LaunchedUrls.Should().ContainSingle();

        Phone(a, t);
        var asked = await RequestAsync(a, t, Cumulative, null);
        asked.Result.Should().Be(PaymentRequestResult.AlreadyRequested, "A'nın kişisi (grup anahtarı) B'nin kaydını bulur");
        asked.Prompt!.Requests.Should().ContainSingle().Which.Machine.Should().Be("PC-B");
        a.Launcher.LaunchedUrls.Should().BeEmpty();
        await AssertConvergedAsync(a, b);
    }

    // ── ödeme geçmişi: Ws'den önceki iş (§8.2-6, §10.4-9) ───────────────────

    /// <summary>
    /// Ws'den önce yapılmış istek (yerelde iş var, mod Off kaydı hiç yazılmadı): modun açılış kapısının koşulu olan ödeme
    /// geçmişi geri doldurması işi kirasız kayda çevirir (<c>Kind = Record</c>, kapsadığı siparişlerle); mod On'da PC-B'nin
    /// sorusunda ve rozetlerinde görünür, A'nın kendi rozetinde çift sayılmaz.
    /// </summary>
    [Theory]
    [MemberData(nameof(Orders))]
    public async Task Odeme_gecmisi_Ws_oncesi_is_geri_doldurmayla_gecer_B_nin_sorusunda_ve_rozetinde_gorunur(string order)
    {
        Use(order);
        var (a, b) = await PcsAsync(modeOn: false);
        var session = a.StartStream();
        var label = a.Sell(session, "alici_ws_oncesi", "K1", 300m);
        await a.PrintAsync(label.Id);
        a.EndStream(session);
        await a.PushAsync();
        await a.PushAsync();
        var customerId = label.CustomerId;
        Phone(a, customerId);
        var scope = $"session:{session}";
        (await RequestAsync(a, customerId, scope, null)).Result.Should().Be(PaymentRequestResult.Opened);
        a.Exec("DELETE FROM PendingPaymentRecord");                      // Ws öncesi: mod Off kaydı hiç yazılmadı
        _server.PaymentRecords().Should().BeEmpty();

        await EnableAsync(a, b);

        var history = _server.PaymentRecords().Should().ContainSingle().Subject;
        history.Kind.Should().Be("record");
        history.State.Should().Be("delivered");
        history.Machine.Should().Be("PC-A");
        history.NetAmount.Should().Be(350m, "A'nın mod Off'ta istediği: 300 + yerel kargo 50");
        history.OrderIds.Should().Equal(G(label.Id));
        history.ScopeKey.Should().Be(scope);
        a.State.Get().PaymentHistorySentAt.Should().NotBeNull();

        await SyncAsync(a, b);
        var onB = b.LabelRepo.GetByIds([label.Id]);
        (await b.PaymentFollowUp.PrefetchAsync(session, onB)).Should().BeTrue();
        b.PaymentFollowUp.Badges(session, onB).RequestedLabels.Should().Equal([label.Id]);
        var onA = a.LabelRepo.GetByIds([label.Id]);
        await a.PaymentFollowUp.PrefetchAsync(session, onA);
        a.PaymentFollowUp.Plan(session, [(onA[0], 0m)]).RequestedPersons.Should().Be(1, "kendi kaydı yerel işle temsil edilir");

        Phone(b, customerId);
        var asked = await RequestAsync(b, customerId, scope, null);
        asked.Result.Should().Be(PaymentRequestResult.AlreadyRequested);
        asked.Prompt!.Requests.Should().ContainSingle().Which.Should().Match<PriorPaymentRequest>(
            p => p.Amount == 350m && p.Machine == "PC-A" && !p.Unconfirmed);
        b.Launcher.LaunchedUrls.Should().BeEmpty();
        await AssertConvergedAsync(a, b);
    }

    /// <summary>
    /// W3 son incelemesi: Cloud API <c>in_progress</c> (<c>SendPending</c>) yerelde teslim yazar (<c>MarkDelivered</c>: teslim
    /// ve deneme sayısı eşit). Mod Off kaydı ve aynı işi sonradan okuyan ödeme geçmişi AYNI kaydı kurar (Id ve içerik):
    /// sunucu ikincisini <c>unchanged</c> sayar, tek mesaja tek kayıt kalır, PC-B'nin sorusu tek öncül gösterir. Eskiden mod
    /// Off kaydı Unconfirmed(J,1,1), geçmiş Delivered(J,1) üretiyordu: ikisi de <c>applied</c>, aynı istek iki öncül.
    /// </summary>
    [Fact]
    public async Task Mod_Off_SendPending_kaydi_ve_odeme_gecmisi_ayni_kaydi_kurar_sunucuda_tek_kayit_B_tek_oncul_gorur()
    {
        Use("AB");
        var (a, b) = await PcsAsync(modeOn: false);
        var session = a.StartStream();
        var label = a.Sell(session, "alici_isleniyor", "K1", 300m);
        await a.PrintAsync(label.Id);
        a.EndStream(session);
        await a.PushAsync();
        await a.PushAsync();
        var customerId = label.CustomerId;
        Phone(a, customerId);
        var scope = $"session:{session}";
        a.Store.Update(s => s.Payment.UseCloudApi = true);
        _server.EnqueueJson(FakeSalesServer.WhatsAppSend, 200,
            """{"ok":false,"errorCode":"in_progress","errorMessage":"işleniyor","messageId":null}""");

        (await RequestAsync(a, customerId, scope, null)).Result.Should().Be(PaymentRequestResult.SendPending);

        // 1. yol: mod Off kaydı kuyruktan sunucuya.
        var offRecord = PendingPaymentRecordCodec.DecodeRecord(
            a.PendingPaymentRecords.List().Should().ContainSingle().Subject);
        var first = await a.PaymentRecords.FlushAsync(CancellationToken.None);
        (first.Sent, first.Dropped).Should().Be((1, 0));
        _server.PaymentRecords().Should().ContainSingle();

        // 2. yol: ödeme geçmişi aynı işi okur — aynı kayıt, sunucuda unchanged (content-conflict değil).
        await a.PaymentHistory.TourOnceAsync(CancellationToken.None);
        var historyRecord = PendingPaymentRecordCodec.DecodeRecord(
            a.PendingPaymentRecords.List().Should().ContainSingle("geçmiş işi yeniden kuyruğa yazar").Subject);
        historyRecord.Should().BeEquivalentTo(offRecord, o => o.ComparingRecordsByMembers());
        var second = await a.PaymentRecords.FlushAsync(CancellationToken.None);
        (second.Sent, second.Dropped).Should().Be((1, 0), "yineleme unchanged; içerik çakışması kaydı düşürürdü");
        _server.Calls(FakeSalesServer.PaymentRequestsBackfill).Should().HaveCount(2, "iki yol da gönderdi");

        var stored = _server.PaymentRecords().Should().ContainSingle("tek mesaja tek kayıt").Subject;
        stored.Id.Should().Be(offRecord.RecordId);
        stored.Kind.Should().Be("record");
        stored.State.Should().Be("delivered", "işin satırı teslim diyor (SendPending, #506)");
        stored.OrderIds.Should().Equal(G(label.Id));

        await EnableAsync(a, b);
        _server.PaymentRecords().Should().ContainSingle();
        await SyncAsync(a, b);
        Phone(b, customerId);
        var asked = await RequestAsync(b, customerId, scope, null);
        asked.Result.Should().Be(PaymentRequestResult.AlreadyRequested);
        asked.Prompt!.Requests.Should().ContainSingle("aynı istek tek öncül").Which.Machine.Should().Be("PC-A");
        b.Launcher.LaunchedUrls.Should().BeEmpty();
        await AssertConvergedAsync(a, b);
    }

    /// <summary>
    /// W3 son incelemesi (doğrulayıcı): ödeme geçmişi gönderimi uçuştaki işi okumaz. <c>BeginSend</c> yazılmış,
    /// <c>MarkDelivered</c> henüz yazılmamış iş <c>HasUnconfirmedSend</c> görünür; geçmiş o anda okusaydı Unconfirmed(J,0,1)
    /// kurardı, gönderim bitince mod Off yazıcısı Delivered(J,1) yazardı: tek mesaja iki Id, sunucu ikisini de
    /// <c>applied</c> sayar, PC-B'nin sorusu iki öncül gösterirdi. Geçmiş, kişisi ödeme akışında olan işten önce keser
    /// (ödeme akışıyla aynı <c>CustomerBusySet</c>); kira bitince işin teslimden sonraki satırından mod Off kaydıyla aynı
    /// kaydı kurar, sunucu <c>unchanged</c> sayar.
    /// </summary>
    [Fact]
    public async Task Mod_Off_gonderim_ucustayken_odeme_gecmisi_isi_okumaz_sunucuda_tek_kayit_B_tek_oncul_gorur()
    {
        Use("AB");
        var (a, b) = await PcsAsync(modeOn: false);
        var session = a.StartStream();
        var label = a.Sell(session, "alici_ucusta", "K1", 350m);
        await a.PrintAsync(label.Id);
        a.EndStream(session);
        await a.PushAsync();
        await a.PushAsync();
        var customerId = label.CustomerId;
        Phone(a, customerId);
        var scope = $"session:{session}";
        a.Store.Update(s => s.Payment.UseCloudApi = true);

        var hold = _server.Hold(FakeSalesServer.WhatsAppSend);
        Task<PaymentRequestOutcome> request;
        PaymentHistoryTourOutcome duringSend;
        try
        {
            request = RequestAsync(a, customerId, scope, null);
            await hold.Arrived.WaitAsync(TimeSpan.FromSeconds(30));
            a.Jobs.GetPersonScopeJobs([customerId], scope).Jobs.Should().ContainSingle()
                .Which.HasUnconfirmedSend.Should().BeTrue("ön koşul: gönderim uçuşta (deneme yazıldı, teslim yazılmadı)");
            duringSend = await a.PaymentHistory.TourOnceAsync(CancellationToken.None);
        }
        finally { hold.Release(); }
        (await request).Result.Should().Be(PaymentRequestResult.Sent);

        var job = a.Jobs.GetPersonScopeJobs([customerId], scope).Jobs.Should().ContainSingle().Subject;
        (job.Deliveries, job.HasUnconfirmedSend).Should().Be((1, false));
        // Geri doldurma turunun sırasıyla (önce kuyruk, sonra geçmiş) geçmiş tamamlanana dek.
        var tours = new List<PaymentHistoryTourOutcome>();
        while (tours.Count < 5 && tours.LastOrDefault() != PaymentHistoryTourOutcome.Completed)
        {
            await a.PaymentRecords.FlushAsync(CancellationToken.None);
            tours.Add(await a.PaymentHistory.TourOnceAsync(CancellationToken.None));
        }

        var stored = _server.PaymentRecords().Should().ContainSingle("tek mesaja tek kayıt").Subject;
        stored.Id.Should().Be(OrderDeck.Core.Payments.PaymentRecordIds.Delivered(job.Id, 1));
        stored.State.Should().Be("delivered");
        stored.OrderIds.Should().Equal(G(label.Id));
        duringSend.Should().Be(PaymentHistoryTourOutcome.InProgress, "sayfa uçuştaki işten önce kesildi, imleç onu geçmedi");
        tours.Should().Equal(PaymentHistoryTourOutcome.AwaitingQueue, PaymentHistoryTourOutcome.Completed);
        _server.Calls(FakeSalesServer.PaymentRequestsBackfill).Should().HaveCount(2,
            "mod Off kaydı ve kira bittikten sonra geçmiş: ikincisi unchanged");

        await EnableAsync(a, b);
        _server.PaymentRecords().Should().ContainSingle();
        await SyncAsync(a, b);
        Phone(b, customerId);
        var asked = await RequestAsync(b, customerId, scope, null);
        asked.Result.Should().Be(PaymentRequestResult.AlreadyRequested);
        var prior = asked.Prompt!.Requests.Should().ContainSingle("aynı istek tek öncül").Subject;
        (prior.Machine, prior.Unconfirmed, prior.Amount).Should().Be(("PC-A", false, job.PromptNet));
        b.Launcher.LaunchedUrls.Should().BeEmpty();
        await AssertConvergedAsync(a, b);
    }

    /// <summary>
    /// W3 son incelemesi (doğrulayıcı): iki yazıcının Id'si her durumda aynı, içerik ise teslimden sonra değişebilir. Mod
    /// Off kaydı K1+K2 ile gittikten sonra K2 iptal edilir (yeni teslim yok); geçmiş aynı Id'yi yalnız K1 ile kurar. Sunucu
    /// ikinciyi <c>content-conflict</c> sayar, kuyruktan son durumla düşer; ilk kayıt (mesaj anının görüntüsü) kalır, geçmiş
    /// yine tamamlanır. Yinelenme ya da kayıp yok.
    /// </summary>
    [Fact]
    public async Task Teslimden_sonra_icerik_degisirse_gecmisin_ayni_Idli_kaydi_content_conflict_ile_duser_ilk_kayit_kalir()
    {
        Use("AB");
        var (a, _) = await PcsAsync(modeOn: false);
        var session = a.StartStream();
        var k1 = a.Sell(session, "alici_vazgecen", "K1", 200m);
        var k2 = a.Sell(session, "alici_vazgecen", "K2", 150m);
        k2.CustomerId.Should().Be(k1.CustomerId);
        await a.PrintAsync(k1.Id, k2.Id);
        a.EndStream(session);
        await a.PushAsync();
        await a.PushAsync();
        var customerId = k1.CustomerId;
        Phone(a, customerId);
        var scope = $"session:{session}";

        (await RequestAsync(a, customerId, scope, null)).Result.Should().Be(PaymentRequestResult.Opened);
        var offRecord = PendingPaymentRecordCodec.DecodeRecord(
            a.PendingPaymentRecords.List().Should().ContainSingle().Subject);
        offRecord.OrderIds.Should().BeEquivalentTo([G(k1.Id), G(k2.Id)]);
        (await a.PaymentRecords.FlushAsync(CancellationToken.None)).Sent.Should().Be(1);

        (await a.Editor.CancelAsync([k2.Id], "musteri-vazgecti")).HasProblems.Should().BeFalse();
        await a.PaymentHistory.TourOnceAsync(CancellationToken.None);
        var historyRecord = PendingPaymentRecordCodec.DecodeRecord(
            a.PendingPaymentRecords.List().Should().ContainSingle().Subject);
        historyRecord.RecordId.Should().Be(offRecord.RecordId, "Id yalnız işin satırından: aynı teslim");
        historyRecord.OrderIds.Should().Equal(new[] { G(k1.Id) },
            "içerik okunduğu andan: iptal edilen etiket katkı vermez");

        var second = await a.PaymentRecords.FlushAsync(CancellationToken.None);
        (second.Sent, second.Dropped).Should().Be((0, 1), "aynı Id başka içerik: content-conflict, son durum");
        var stored = _server.PaymentRecords().Should().ContainSingle("yinelenme yok").Subject;
        stored.Id.Should().Be(offRecord.RecordId);
        stored.OrderIds.Should().BeEquivalentTo([G(k1.Id), G(k2.Id)], "ilk kayıt kalır: mesaj anının görüntüsü");
        (await a.PaymentHistory.TourOnceAsync(CancellationToken.None)).Should().Be(PaymentHistoryTourOutcome.Completed,
            "düşen kayıt tamamlanmayı bekletmez");
    }

    // ── ön gönderim (§6.4-1) ───────────────────────────────────────────────

    /// <summary>
    /// A'nın devredilmiş yayınında X'in kalıntı etiketi çevrimdışı basıldı (bekleyen komut, §5.5) ve komut ucu takıldı:
    /// X'in isteği "Bekleyen baskı ya da iptal onayı gönderilemedi…" ile durur, kira istenmez; aynı takılı kuyruk ilgisiz Y'nin
    /// isteğini engellemez (ön gönderimin zaman aşımı engel değil, karar sunucunun). Uç açılınca komut gider ve X'in isteği
    /// basılmış kalıntıyla geçer; B sonra X'i isterse soru gelir.
    /// </summary>
    [Theory]
    [MemberData(nameof(Orders))]
    public async Task On_gonderim_bekleyen_komut_istegi_engeller_ilgisiz_takili_kuyruk_engellemez(string order)
    {
        Use(order);
        var (a, b) = await PcsAsync();
        var s1 = a.StartStream();
        var x1 = a.Sell(s1, "kisi_x", "A1", 120m);
        var x2 = a.Sell(s1, "kisi_x", "A2", 80m);
        var y1 = a.Sell(s1, "kisi_y", "B1", 100m);
        await a.PrintAsync(x1.Id, y1.Id);
        a.EndStream(s1);
        await SyncAsync(a, b);
        a.Offline = true;
        (await a.PrintAsync(x2.Id)).Pending.Should().Equal(x2.Id);
        a.Offline = false;
        var (x, y) = (x1.CustomerId, y1.CustomerId);
        Phone(a, x);
        Phone(a, y);
        var scope = $"session:{s1}";
        _server.HangEndpoint = FakeSalesServer.Commands;
        a.Lease.PrePushTimeout = TimeSpan.FromMilliseconds(500);

        var blocked = await RequestAsync(a, x, scope, null);

        blocked.Result.Should().Be(PaymentRequestResult.Blocked);
        blocked.Message.Should().Be("Bekleyen baskı ya da iptal onayı gönderilemedi; bağlantı gelince tekrar dene.");
        Begins().Should().BeEmpty("engel kiradan önce");
        a.PendingCount.Should().Be(1);

        var other = await RequestAsync(a, y, scope, null);
        other.Result.Should().Be(PaymentRequestResult.Opened, "ilgisiz takılı kuyruk Y'yi engellemez");
        Begins().Should().ContainSingle().Which.GetProperty("ownerId").GetString().Should().Be(y);

        _server.HangEndpoint = null;
        var passed = await RequestAsync(a, x, scope, null);

        passed.Result.Should().Be(PaymentRequestResult.Opened);
        var begin = Begins()[^1];
        begin.GetProperty("productTotal").GetDecimal().Should().Be(200m, "ön gönderimde onaylanan kalıntı baskısı girdi");
        begin.GetProperty("orderIds").EnumerateArray().Select(o => o.GetGuid()).Should().BeEquivalentTo([G(x1.Id), G(x2.Id)]);
        a.PendingCount.Should().Be(0);
        a.Papers[x2.Id].Should().Be(1, "tek kâğıt");
        _server.Order(G(x2.Id))!.Row.PrintedAt.Should().NotBeNull();

        await SyncAsync(a, b);
        Phone(b, x);
        var asked = await RequestAsync(b, x, scope, null);
        asked.Result.Should().Be(PaymentRequestResult.AlreadyRequested);
        asked.Prompt!.Requests.Single().Machine.Should().Be("PC-A");
        await AssertConvergedAsync(a, b);
    }

    // ── miras iş scope-has-deduction (§6.6) ─────────────────────────────────

    /// <summary>
    /// B, X'in yayın kapsamında bakiyeden 100 düşerek istedi. A'da aynı kapsamın birleştirmede indirilmiş miras işi var
    /// (anahtarı sunucuda hiç yazılmamış, <c>apply_uncertain</c>). A istediğinde önce B'nin isteği sorulur; onaydan sonra miras
    /// işin anahtarı kapsamda B'nin düşümünü bulur (409 <c>scope-has-deduction</c>): miras iş benimsemez, #506'nın
    /// birleştirme sorusu döner, hiçbir şey yazılmaz, mesaj gitmez, kira vazgeçilmiş kapanır.
    /// </summary>
    [Theory]
    [MemberData(nameof(Orders))]
    public async Task Miras_is_kapsamda_baskasinin_dusumunu_bulunca_benimsemez_birlestirme_sorusuna_duser(string order)
    {
        Use(order);
        var (a, b) = await PcsAsync();
        _server.BalancePreview = 100m;
        var s1 = a.StartStream();
        var label = a.Sell(s1, "kisi_miras", "A1", 600m);
        await a.PrintAsync(label.Id);
        a.EndStream(s1);
        await SyncAsync(a, b);
        var x = label.CustomerId;
        var scope = $"session:{s1}";
        Phone(b, x);
        (await RequestAsync(b, x, scope, null)).Result.Should().Be(PaymentRequestResult.Opened);
        var deduction = _server.BalanceTransactions().Should().ContainSingle().Subject;
        deduction.Applied.Should().Be(100m);
        deduction.SaleScope.Should().Be(scope);

        // A'nın miras işi: anahtar diske indi, sunucuya hiç gitmedi; sonra birleştirmede miras kapsamına indirildi.
        var legacyKey = Guid.NewGuid();
        var legacy = a.Jobs.FindOrCreate(x, scope, 600m);
        a.Jobs.BeginApply(legacy.Id, legacyKey).Should().BeTrue();
        // PaymentJobSql.DemoteToLegacy'nin para taşıyan iş için etkisi (iş açık kalır, asıl kapsam PriorScopeKey'de).
        a.Exec(@"UPDATE PaymentJob SET PriorScopeKey = COALESCE(PriorScopeKey, ScopeKey),
                        ScopeKey = 'legacy:' || COALESCE(ApplyKey, Id) || ':' || Id, ClosedAt = NULL, UpdatedAt = @now
                 WHERE Id = @id", new { id = legacy.Id, now = DateTimeOffset.UtcNow.ToUnixTimeSeconds() });
        a.Jobs.Get(legacy.Id)!.ScopeKey.Should().StartWith("legacy:", "ön koşul: miras iş");
        Phone(a, x);

        var asked = await RequestAsync(a, x, scope, null);
        asked.Result.Should().Be(PaymentRequestResult.AlreadyRequested, "önce B'nin isteği sorulur");
        asked.Prompt!.Requests.Should().ContainSingle().Which.Machine.Should().Be("PC-B");

        var merge = await RequestAsync(a, x, scope, asked.Prompt.Tokens.ToHashSet());

        merge.Result.Should().Be(PaymentRequestResult.MergeRequired);
        merge.Prompt!.Kind.Should().Be(PersonScopePromptKind.Merge);
        merge.Prompt.RemoteDeductions.Should().Equal(100m);
        a.Launcher.LaunchedUrls.Should().BeEmpty();
        a.Jobs.Get(legacy.Id)!.State.Should().Be(PaymentJobState.ApplyUncertain, "miras iş düşümü sahiplenmez");
        _server.BalanceTransactions().Should().ContainSingle("hiçbir şey yazılmadı").Which.Id.Should().Be(deduction.Id);
        var aLease = _server.PaymentRecords().Single(r => r.Machine == "PC-A");
        aLease.State.Should().Be("abandoned", "mesaj gitmedi: kira vazgeçilmiş kapandı");
        await AssertConvergedAsync(a, b);
    }

    // ── 600 öncül tek onay turunda (#523) ──────────────────────────────────

    /// <summary>
    /// Kişi × kapsamda 600 öncül birikmiş (B'nin kayıtları): soru tek 409'la gelir; onayda <c>begin</c>
    /// <c>confirmedPriorsDigest</c>'i 409'un <c>priorsDigest</c>'iyle aynen ve en çok 500 Id taşır, özet bütün kümeyi onaylar
    /// — tek onay turunda geçer, soru döngüye girmez.
    /// </summary>
    [Theory]
    [MemberData(nameof(Orders))]
    public async Task Alti_yuz_oncul_ozetle_tek_onay_turunda_gecer(string order)
    {
        Use(order);
        var (a, b) = await PcsAsync();
        var s1 = a.StartStream();
        var label = a.Sell(s1, "sadik_musteri", "A1", 400m);
        await a.PrintAsync(label.Id);
        a.EndStream(s1);
        await SyncAsync(a, b);
        var x = label.CustomerId;
        Phone(a, x);
        var at = DateTimeOffset.UtcNow.AddDays(-700);
        for (var n = 0; n < 600; n++)
            _server.AddPaymentPrior(new FakeSalesServer.PaymentPrior(Guid.NewGuid(), "PC-B", [x], Cumulative, 100m, null,
                at.AddDays(n), at.AddDays(n), false, ReplicaId: b.Replica));

        var asked = await RequestAsync(a, x, Cumulative, null);

        asked.Result.Should().Be(PaymentRequestResult.AlreadyRequested);
        asked.Prompt!.Requests.Should().HaveCount(600);
        var passed = await RequestAsync(a, x, Cumulative, asked.Prompt.Tokens.ToHashSet());

        passed.Result.Should().Be(PaymentRequestResult.Opened);
        var begins = Begins();
        begins.Should().HaveCount(2, "tek onay turu");
        var digest = begins[1].GetProperty("confirmedPriorsDigest").GetString();
        digest.Should().StartWith("v1.").And.HaveLength(46);
        begins[1].GetProperty("confirmedPriorIds").GetArrayLength().Should().Be(500, "fazlası gönderilmez");
        begins[0].TryGetProperty("confirmedPriorsDigest", out _).Should().BeFalse("ilk istekte onay yok");
        _server.PaymentRecords().Count(r => r.Machine == "PC-A" && r.State == "delivered").Should().Be(1);
        a.Launcher.LaunchedUrls.Should().ContainSingle();
    }

    // ── lisans geneli kargo ayarı (Y4, §6.2) ───────────────────────────────

    /// <summary>
    /// Lisans geneli kargo iki bilgisayarda aynı; B ayar ekranından değiştirir (PUT, sürüm 3). Henüz tur dönmemiş A eski
    /// sürümle (2: eşik 750, ücret 60) ister: <c>begin</c> <c>settings-stale</c> alır, önbellek sunucunun güncel ayarıyla
    /// yenilenir, kargo yeniden hesaplanır ve kira yerinde bir kez daha istenir — mesaj yeni ücretle (70) gider.
    /// </summary>
    [Theory]
    [MemberData(nameof(Orders))]
    public async Task Lisans_geneli_kargo_iki_PCde_ayni_obur_PCnin_PUTundan_sonra_settings_stale_yeni_ucretle_gecer(
        string order)
    {
        Use(order);
        var (a, b) = await PcsAsync();
        var s1 = a.StartStream();
        var label = a.Sell(s1, "kargo_kisi", "A1", 400m);
        await a.PrintAsync(label.Id);
        a.EndStream(s1);
        await SyncAsync(a, b);
        foreach (var pc in new[] { a, b })
        {
            var current = pc.Shipping.Current();
            current.LicenseWide.Should().BeTrue();
            current.Version.Should().Be(2);
            current.Settings.FreeShippingThreshold.Should().Be(750m, $"{pc.Name}: yerel 500/50 değil, lisans geneli");
            current.Settings.ShippingFee.Should().Be(60m);
        }

        var saved = await b.ShippingWriter.SaveAsync(2, 1000m, 70m);
        saved.Status.Should().Be(ShippingSettingsSaveStatus.Saved);
        b.Shipping.Current().Version.Should().Be(3);
        a.Shipping.Current().Version.Should().Be(2, "ön koşul: A henüz tur dönmedi");
        var x = label.CustomerId;
        Phone(a, x);

        var outcome = await RequestAsync(a, x, $"session:{s1}", null);

        outcome.Result.Should().Be(PaymentRequestResult.Opened);
        var begins = Begins();
        begins.Should().HaveCount(2, "settings-stale'den sonra kira yerinde bir kez daha");
        begins[0].GetProperty("shippingSettingsVersion").GetInt32().Should().Be(2);
        begins[0].GetProperty("totalBeforeBalance").GetDecimal().Should().Be(460m);
        begins[1].GetProperty("shippingSettingsVersion").GetInt32().Should().Be(3);
        begins[1].GetProperty("totalBeforeBalance").GetDecimal().Should().Be(470m);
        _server.PaymentRecords().Single().NetAmount.Should().Be(470m, "mesaj yeni ücretle gitti");

        await SyncAsync(a, b);
        foreach (var pc in new[] { a, b })
        {
            var current = pc.Shipping.Current();
            current.Version.Should().Be(3, $"{pc.Name}: lisans geneli ayar iki bilgisayarda aynı");
            current.Settings.FreeShippingThreshold.Should().Be(1000m);
            current.Settings.ShippingFee.Should().Be(70m);
        }
        await AssertConvergedAsync(a, b);
    }
}
