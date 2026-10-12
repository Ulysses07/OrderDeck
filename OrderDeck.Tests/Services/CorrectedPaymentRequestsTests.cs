using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using FluentAssertions;
using Moq;
using OrderDeck.App.Services;
using OrderDeck.App.Services.PriceCorrection;
using OrderDeck.Core.Chat;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Sales;
using OrderDeck.Core.Sessions;
using OrderDeck.Core.Settings;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Core.Time;
using OrderDeck.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.Tests.Services;

/// <summary>
/// Fiyat düzeltmesinin ödeme tarafı (K5): teslim edilmiş isteğinin tutarı yeni toplamdan farklı olanlar
/// KİŞİ düzeyinde listelenir (#506), ödemesi alınmışa otomatik bir şey gitmez, "düzeltilmiş isteği gönder"
/// ek soru gerektireni atlar.
/// </summary>
public sealed class CorrectedPaymentRequestsTests : IDisposable
{
    private readonly InMemorySqlite _db = new();
    private readonly LabelService _labels;
    private readonly LabelRepository _labelRepo;
    private readonly CustomerRepository _customers;
    private readonly PaymentJobRepository _jobs;
    private readonly ShipmentRepository _shipments;

    /// <summary>Etiketlerin saati (basılma anı). Ödeme işlerinin teslim anı gerçek saattir: varsayılan
    /// 1000 ile her teslim basılmadan SONRA sayılır.</summary>
    private readonly Mock<IClock> _clock = new();

    public CorrectedPaymentRequestsTests()
    {
        new MigrationRunner(_db).Run();
        _clock.Setup(c => c.UnixNow()).Returns(1000L);
        new SessionRepository(_db).Insert(new StreamSession("s1", null, 1000, null, new[] { "instagram" }, null));
        _customers = new CustomerRepository(_db);
        _labelRepo = new LabelRepository(_db);
        _shipments = new ShipmentRepository(_db);
        _jobs = new PaymentJobRepository(_db);
        _labels = new LabelService(_labelRepo,
            new CustomerService(_customers, new SessionRepository(_db), _labelRepo, _clock.Object), _db, _clock.Object,
            _shipments);
    }

    public void Dispose() => _db.Dispose();

    private Label Add(string platform, string user, decimal price, string code = "A12")
        => _labels.Add("s1", new ChatMessage(Guid.NewGuid().ToString("N"), platform, null, user, user, null,
            $"{user} aldım", 1000, Array.Empty<string>()), price, code);

    private CorrectedPaymentRequests Sut(CorrectedPaymentRequests.RequestPayment? request = null)
        => new(_customers, _labelRepo, _jobs, () => new AppSettings(),
            request ?? ((_, _, _, _, _) => throw new InvalidOperationException("gönderim beklenmiyordu")));

    /// <summary>Kişiye bu kapsamda teslim edilmiş (mesajı ulaşmış) bir istek: sonucu kesin (bakiye yok)
    /// ve teslim kaydı yazılmış iş.</summary>
    private PaymentJob Delivered(string ownerId, string scope, decimal net)
    {
        var job = _jobs.FindOrCreate(ownerId, scope, net);
        _jobs.MarkNoBalance(job.Id, null, job.Revision).Should().BeTrue();
        _jobs.MarkDelivered(job.Id, net);
        return _jobs.Get(job.Id)!;
    }

    /// <summary>Teslim edilmiş istek, düşümün bir kısmı ya da tamamı bakiyeden: müşteriden
    /// <c>total − applied</c> istendi.</summary>
    private PaymentJob AppliedAndDelivered(string ownerId, string scope, decimal total, decimal applied)
    {
        var job = _jobs.FindOrCreate(ownerId, scope, total);
        var key = Guid.NewGuid();
        _jobs.BeginApply(job.Id, key).Should().BeTrue();
        _jobs.MarkApplied(job.Id, key, job.Revision, applied).Should().BeTrue();
        _jobs.MarkDelivered(job.Id, total - applied);
        return _jobs.Get(job.Id)!;
    }

    /// <summary>İşin teslimini geçmişe taşır (günler önceki istek).</summary>
    private void DeliveredLongAgo(string jobId)
    {
        var at = DateTimeOffset.UtcNow.AddDays(-11).ToUnixTimeSeconds();
        using var conn = _db.Open();
        conn.Execute("UPDATE PaymentJob SET DeliveredAt=@at, ClosedAt=@at, UpdatedAt=@at WHERE Id=@jobId",
            new { at, jobId });
    }

    /// <summary>Etiketler gerçek saatle basılsın (teslimler onlardan önce ya da sonra olabilsin).</summary>
    private void PrintClockIsNow() => _clock.Setup(c => c.UnixNow()).Returns(DateTimeOffset.UtcNow.ToUnixTimeSeconds());

    private (Label A1, Label A2, Label B, PaymentPerson Person) GroupedPersonWithRequest()
    {
        var a1 = Add("instagram", "@ayse", 250m);
        var a2 = Add("instagram", "@ayse", 250m);
        var b = Add("tiktok", "@ayse_tt", 100m, "B7");
        _labels.MarkPrintedAndRecord(new[] { a1.Id, a2.Id, b.Id });
        _customers.MergeIntoGroup(new[] { a1.CustomerId, b.CustomerId });
        var person = _customers.GetPaymentPerson(a1.CustomerId);
        Delivered(person.OwnerId!, "session:s1", 600m);
        return (a1, a2, b, person);
    }

    [Fact]
    public void Changed_request_is_listed_once_per_person_with_the_person_total()
    {
        var (a1, a2, _, person) = GroupedPersonWithRequest();
        var result = _labels.CorrectPrices(new[] { a1.Id, a2.Id }, 205m);

        var plan = Sut().Plan("s1", result.Corrected.Select(c => (c.Before, c.Delta)));

        plan.RequestedPersons.Should().Be(1);
        var row = plan.ToResend.Should().ContainSingle("iki satır tek kişi — kişi düzeyi (#506)").Subject;
        row.ScopeKey.Should().Be("session:s1");
        row.PriorAmount.Should().Be(600m);
        row.NewTotal.Should().Be(510m, "kişinin yayın toplamı: 2 × 205 + 100 (öbür satırı dahil)");
        person.MemberIds.Should().Contain(row.CustomerId);
        plan.Paid.Should().BeEmpty();
    }

    [Fact]
    public void Unchanged_total_is_not_listed()
    {
        var (a1, _, _, _) = GroupedPersonWithRequest();

        // Düzeltilmemiş (fark 0) — istek hâlâ doğru tutarda.
        var plan = Sut().Plan("s1", new[] { (_labelRepo.GetById(a1.Id)!, 0m) });

        plan.RequestedPersons.Should().Be(1);
        plan.ToResend.Should().BeEmpty();
    }

    [Fact]
    public void Person_without_a_delivered_request_is_not_listed()
    {
        var l = Add("instagram", "@yeni", 250m);
        _labels.MarkPrintedAndRecord(new[] { l.Id });
        var result = _labels.CorrectPrices(new[] { l.Id }, 205m);

        var plan = Sut().Plan("s1", result.Corrected.Select(c => (c.Before, c.Delta)));

        plan.RequestedPersons.Should().Be(0);
        plan.ToResend.Should().BeEmpty("isteği gitmemiş kişinin sıradaki isteği zaten yeni tutarı ister");
    }

    [Fact]
    public void Dekont_paid_person_gets_nothing_automatic_and_shows_the_difference()
    {
        var l = Add("instagram", "@odedi", 250m);
        _labels.MarkPrintedAndRecord(new[] { l.Id });
        var shipmentSvc = new ShipmentService(_shipments, _labelRepo, () => new AppSettings());
        var open = shipmentSvc.GetOrCreateOpenShipment(l.CustomerId);
        shipmentSvc.AttachLabels(open.Id, new[] { l.Id });   // dekont kaydı etiketleri dosyaya bağlar
        Delivered(l.CustomerId, "session:s1", 250m);
        var result = _labels.CorrectPrices(new[] { l.Id }, 205m);

        var plan = Sut().Plan("s1", result.Corrected.Select(c => (c.Before, c.Delta)));

        plan.ToResend.Should().BeEmpty("ödemesi alınmış kişiye otomatik istek gitmez");
        var paid = plan.Paid.Should().ContainSingle().Subject;
        paid.ByDekont.Should().BeTrue();
        paid.Delta.Should().Be(-45m);
    }

    [Fact]
    public void Person_whose_request_was_fully_paid_from_balance_gets_nothing_automatic()
    {
        var l = Add("instagram", "@bakiye", 250m);
        _labels.MarkPrintedAndRecord(new[] { l.Id });
        AppliedAndDelivered(l.CustomerId, "session:s1", 250m, applied: 250m);   // müşteriden 0 TL istendi
        var result = _labels.CorrectPrices(new[] { l.Id }, 205m);

        var plan = Sut().Plan("s1", result.Corrected.Select(c => (c.Before, c.Delta)));

        plan.ToResend.Should().BeEmpty();
        var paid = plan.Paid.Should().ContainSingle().Subject;
        paid.ByBalance.Should().BeTrue();
        paid.Delta.Should().Be(-45m);
    }

    [Fact]
    public void Partial_balance_deduction_is_not_paid_and_the_request_is_listed()
    {
        // 250 istendi, 50'si bakiyeden: müşteriden 200 istendi, ödenmedi. "Ödendi (bakiye)" demek yanlış
        // tutarlı isteği geçerli bırakırdı.
        var l = Add("instagram", "@kismi", 250m);
        _labels.MarkPrintedAndRecord(new[] { l.Id });
        AppliedAndDelivered(l.CustomerId, "session:s1", 250m, applied: 50m);
        var result = _labels.CorrectPrices(new[] { l.Id }, 205m);

        var plan = Sut().Plan("s1", result.Corrected.Select(c => (c.Before, c.Delta)));

        plan.Paid.Should().BeEmpty();
        plan.RequestedPersons.Should().Be(1);
        var row = plan.ToResend.Should().ContainSingle().Subject;
        row.PriorAmount.Should().Be(200m, "soruda 'istendi' denen tutar — mesajdaki net");
        row.NewTotal.Should().Be(205m);
        // Net bakiye SONRASI, yeni toplam bakiye ÖNCESİ: satır bunu söyler, yoksa 250→205 düşüşü
        // "200 istendi; yeni tutar 205" diye artış gibi görünürdü (ödeme sorusuyla aynı ifade).
        row.PriorHadBalance.Should().BeTrue();
        new OrderDeck.App.ViewModels.ResendRowViewModel(row).Text
            .Should().Contain("200,00 TL istendi; yeni toplam 205,00 TL (bakiye düşülmeden önce)");
    }

    [Fact]
    public void Unchanged_total_with_a_balance_deduction_is_not_listed()
    {
        // 250 + 300 = 550 istendi, 100'ü bakiyeden: müşteriden 450 istendi. İkisi 275'e düzeltildi — toplam yine
        // 550. Net (450) brüt yeni toplamla (550) karşılaştırılıyordu: kişi listeye giriyor, "düzeltilmiş
        // isteği gönder" aynı 450 TL'lik isteği müşteriye ikinci kez gönderiyordu.
        var x = Add("instagram", "@denk", 250m);
        var y = Add("instagram", "@denk", 300m, "B7");
        _labels.MarkPrintedAndRecord(new[] { x.Id, y.Id });
        AppliedAndDelivered(x.CustomerId, "session:s1", 550m, applied: 100m);
        var result = _labels.CorrectPrices(new[] { x.Id, y.Id }, 275m);
        result.Corrected.Sum(c => c.Delta).Should().Be(0m);

        var plan = Sut().Plan("s1", result.Corrected.Select(c => (c.Before, c.Delta)));

        plan.RequestedPersons.Should().Be(1);
        plan.ToResend.Should().BeEmpty("isteğin bakiye öncesi toplamı (450 + 100) yeni toplama eşit");
    }

    [Fact]
    public void Dekont_difference_includes_queued_labels_attached_to_the_file()
    {
        // Dekont kaydı kuyruktaki etiketi de açık dosyaya bağlar; düzeltme dosya toplamını 500'den 455'e
        // indirir. Çekmece "ödendi (dekont), fark +0,00 TL" diyordu: fark yalnız basılmış etiketlerden.
        var printed = Add("instagram", "@q", 250m);
        _labels.MarkPrintedAndRecord(new[] { printed.Id });
        var queued = Add("instagram", "@q", 250m);
        var shipmentSvc = new ShipmentService(_shipments, _labelRepo, () => new AppSettings());
        var open = shipmentSvc.GetOrCreateOpenShipment(printed.CustomerId);
        shipmentSvc.AttachLabels(open.Id, new[] { printed.Id, queued.Id });
        var result = _labels.CorrectPrices(new[] { queued.Id }, 205m);

        var plan = Sut().Plan("s1", result.Corrected.Select(c => (c.Before, c.Delta)));

        var paid = plan.Paid.Should().ContainSingle().Subject;
        paid.ByDekont.Should().BeTrue();
        paid.Delta.Should().Be(-45m, "dosya toplamına yazılan farkla aynı");
        _shipments.GetById(open.Id)!.CumulativeAmount.Should().Be(455m);
    }

    [Fact]
    public void Request_for_an_unattached_label_is_listed_beside_a_dekont_paid_one()
    {
        // Aynı kişinin iki etiketi: biri dekontla dosyaya bağlı (ödendi), öbürü için eski tutarla istek gitmiş,
        // ödenmemiş. İkisi birlikte düzeltilince kişi yalnız "ödendi"ye giriyordu; ikinci istek
        // "düzeltilmiş isteği gönder" listesine hiç girmiyordu.
        var paidOne = Add("instagram", "@iki", 250m);
        var unpaid = Add("instagram", "@iki", 250m);
        _labels.MarkPrintedAndRecord(new[] { paidOne.Id, unpaid.Id });
        var shipmentSvc = new ShipmentService(_shipments, _labelRepo, () => new AppSettings());
        shipmentSvc.AttachLabels(shipmentSvc.GetOrCreateOpenShipment(paidOne.CustomerId).Id, new[] { paidOne.Id });
        Delivered(paidOne.CustomerId, "session:s1", 500m);
        var result = _labels.CorrectPrices(new[] { paidOne.Id, unpaid.Id }, 205m);

        var plan = Sut().Plan("s1", result.Corrected.Select(c => (c.Before, c.Delta)));

        var paid = plan.Paid.Should().ContainSingle().Subject;
        paid.ByDekont.Should().BeTrue();
        paid.Delta.Should().Be(-45m, "yalnız dosyaya bağlı etiketin farkı");
        plan.RequestedPersons.Should().Be(1);
        var row = plan.ToResend.Should().ContainSingle().Subject;
        row.PriorAmount.Should().Be(500m);
        row.NewTotal.Should().Be(410m);
    }

    [Fact]
    public void Old_balance_deduction_in_another_scope_does_not_mark_todays_request_paid()
    {
        // 01.10'da genel istek: 500 istendi, 30'u bakiyeden düşüldü (iş kapandı). Bugün bu yayında 250 TL'lik
        // etiket basıldı ve bakiyesiz yayın isteği gitti. Eski düşüm bugünkü isteği ödenmiş yapmaz.
        PrintClockIsNow();
        var l = Add("instagram", "@x", 250m);
        var old = AppliedAndDelivered(l.CustomerId, "cumulative", 500m, applied: 30m);
        DeliveredLongAgo(old.Id);
        _labels.MarkPrintedAndRecord(new[] { l.Id });
        Delivered(l.CustomerId, "session:s1", 250m);
        var result = _labels.CorrectPrices(new[] { l.Id }, 205m);

        var plan = Sut().Plan("s1", result.Corrected.Select(c => (c.Before, c.Delta)));

        plan.Paid.Should().BeEmpty("bugünkü 250 TL'lik istek ödenmedi");
        var row = plan.ToResend.Should().ContainSingle().Subject;
        row.ScopeKey.Should().Be("session:s1");
        row.PriorAmount.Should().Be(250m);
        row.NewTotal.Should().Be(205m);
    }

    [Fact]
    public void Request_sent_before_the_labels_were_printed_is_not_listed()
    {
        // Genel istek günler önce gitti (1000 TL); yanlış fiyatlı etiket bugün basıldı, bugün istek yok. O
        // istek yanlış fiyatı hiç içermedi — "eski tutarla istek gitmiş" değil.
        PrintClockIsNow();
        var l = Add("instagram", "@eski", 250m);
        var old = _jobs.FindOrCreate(l.CustomerId, "cumulative", 1000m);
        _jobs.MarkNoBalance(old.Id, null, old.Revision).Should().BeTrue();
        _jobs.MarkDelivered(old.Id, 1000m);
        DeliveredLongAgo(old.Id);
        _labels.MarkPrintedAndRecord(new[] { l.Id });
        var result = _labels.CorrectPrices(new[] { l.Id }, 205m);

        var plan = Sut().Plan("s1", result.Corrected.Select(c => (c.Before, c.Delta)));

        plan.RequestedPersons.Should().Be(0);
        plan.ToResend.Should().BeEmpty();
        plan.Paid.Should().BeEmpty();
    }

    [Fact]
    public void Correcting_only_queued_labels_does_not_count_a_request()
    {
        // Kişinin önceki ürün için isteği var; düzeltilen etiket kuyrukta (basılmamış) — hiçbir istekte yok.
        var (_, _, _, person) = GroupedPersonWithRequest();
        var queued = Add("instagram", "@ayse", 250m);
        _customers.GetPaymentPerson(queued.CustomerId).MemberKey.Should().Be(person.MemberKey);
        var result = _labels.CorrectPrices(new[] { queued.Id }, 205m);

        var plan = Sut().Plan("s1", result.Corrected.Select(c => (c.Before, c.Delta)));

        plan.RequestedPersons.Should().Be(0, "özetteki 'eski tutarla istek gitmiş' sayısı");
        plan.ToResend.Should().BeEmpty();
    }

    [Fact]
    public void Badges_flag_covered_labels_and_fully_balance_paid_labels()
    {
        var (a1, _, b, _) = GroupedPersonWithRequest();
        var other = Add("instagram", "@baska", 100m);
        var paidByBalance = Add("instagram", "@bakiye", 250m);
        var partial = Add("instagram", "@kismi", 250m);
        _labels.MarkPrintedAndRecord(new[] { other.Id, paidByBalance.Id, partial.Id });
        AppliedAndDelivered(paidByBalance.CustomerId, "session:s1", 250m, applied: 250m);
        AppliedAndDelivered(partial.CustomerId, "session:s1", 250m, applied: 50m);

        var badges = Sut().Badges("s1",
            _labelRepo.GetByIds(new[] { a1.Id, b.Id, other.Id, paidByBalance.Id, partial.Id }));

        badges.RequestedLabels.Should().BeEquivalentTo(new[] { a1.Id, b.Id, partial.Id },
            "istek kişinin (#506: b öbür satırda) ama rozet etiket düzeyinde; tamamı bakiyeden düşülmüş istek 'ödendi'");
        badges.BalancePaidLabels.Should().Equal(new[] { paidByBalance.Id }, "kısmi düşüm ödendi sayılmaz");

        // "Basılanlar" rozeti fiyat düzeltme çekmecesiyle AYNI tanımı kullanır.
        var printed = new OrderDeck.App.ViewModels.PrintedLabelsViewModel(_labelRepo, Sut().Badges);
        printed.Load("s1");
        printed.Rows.Single(r => r.Id == paidByBalance.Id).StatusText.Should().Be("ödendi");
        printed.Rows.Single(r => r.Id == partial.Id).StatusText.Should().Be("ödeme istendi");
        printed.Rows.Single(r => r.Id == other.Id).StatusText.Should().BeEmpty();
    }

    [Fact]
    public void Request_badge_is_only_on_labels_the_request_covers()
    {
        // @ali A'yı aldı, A basıldı; yayın isteği gitti; sonra B'yi aldı, B basıldı. B hiçbir istekte yok —
        // fiyat düzeltme çekmecesi "istek gitmemiş" diyor, Basılanlar "ödeme istendi" diyordu.
        var a = Add("instagram", "@ali", 250m);
        _labels.MarkPrintedAndRecord(new[] { a.Id });                       // saat 1000: istekten önce
        Delivered(a.CustomerId, "session:s1", 250m);                        // teslim gerçek saatle
        _clock.Setup(c => c.UnixNow()).Returns(DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds());
        var b = Add("instagram", "@ali", 300m, "B7");
        _labels.MarkPrintedAndRecord(new[] { b.Id });

        var printed = new OrderDeck.App.ViewModels.PrintedLabelsViewModel(_labelRepo, Sut().Badges);
        printed.Load("s1");

        printed.Rows.Single(r => r.Id == a.Id).StatusText.Should().Be("ödeme istendi");
        printed.Rows.Single(r => r.Id == b.Id).StatusText.Should().BeEmpty("istek B basılmadan önce gitti");
        Sut().Plan("s1", new[] { (_labelRepo.GetById(b.Id)!, 0m) }).RequestedPersons
            .Should().Be(0, "çekmeceyle aynı tanım");
    }

    [Fact]
    public void Old_general_request_does_not_badge_a_label_printed_today()
    {
        // Genel istek 11 gün önce gitti; bugün basılan etiket "ödeme istendi" görünüyordu — genel istek almış
        // her müşteride kalıcı olarak (teslim edilmiş iş silinmez).
        PrintClockIsNow();
        var l = Add("instagram", "@eski", 250m);
        var old = Delivered(l.CustomerId, "cumulative", 1000m);
        DeliveredLongAgo(old.Id);
        _labels.MarkPrintedAndRecord(new[] { l.Id });

        var printed = new OrderDeck.App.ViewModels.PrintedLabelsViewModel(_labelRepo, Sut().Badges);
        printed.Load("s1");

        printed.Rows.Single().StatusText.Should().BeEmpty("istek etiketi içermiyor");
    }

    [Fact]
    public void Shipped_and_dekont_attached_labels_show_their_badges()
    {
        var shippedLabel = Add("instagram", "@kargo", 250m);
        var dekont = Add("instagram", "@dekont", 250m);
        _labels.MarkPrintedAndRecord(new[] { shippedLabel.Id, dekont.Id });
        var shipmentSvc = new ShipmentService(_shipments, _labelRepo, () => new AppSettings());
        var shipped = shipmentSvc.AttachLabels(shipmentSvc.GetOrCreateOpenShipment(shippedLabel.CustomerId).Id,
            new[] { shippedLabel.Id });
        shipmentSvc.ApplyDecision(shipped.Id, ShipmentDecision.ShipNow);
        shipmentSvc.AttachLabels(shipmentSvc.GetOrCreateOpenShipment(dekont.CustomerId).Id, new[] { dekont.Id });

        var printed = new OrderDeck.App.ViewModels.PrintedLabelsViewModel(_labelRepo, Sut().Badges);
        printed.Load("s1");

        printed.Rows.Single(r => r.Id == shippedLabel.Id).StatusText.Should().Be("kargoda");
        printed.Rows.Single(r => r.Id == dekont.Id).StatusText.Should().Be("ödendi", "dekont kaydı dosyaya bağlar");
    }

    [Fact]
    public async Task Opened_whatsapp_window_is_not_reported_as_sent()
    {
        // Cloud API kapalı: wa.me penceresi açılır, gönder tuşuna operatör basar. "Gönderildi" sayılıyordu —
        // özet "15 istek gönderildi" derken penceresi göndermeden kapatılan kişi gitmiş sanılırdı.
        var (_, _, _, person) = GroupedPersonWithRequest();
        var sut = Sut((_, _, _, _, _) => Task.FromResult(new PaymentRequestOutcome(PaymentRequestResult.Opened)));

        var outcomes = await sut.ResendAsync("s1", DateTime.Now, new[] { Candidate(person.LeasedId) });

        outcomes.Should().ContainSingle().Which.Status.Should().Be(ResendStatus.Opened);
    }

    [Fact]
    public void Resend_summary_counts_each_outcome_separately()
    {
        var c = Candidate("c1");
        var outcomes = new[]
        {
            new ResendOutcome(c, ResendStatus.Sent, ""),
            new ResendOutcome(c, ResendStatus.Opened, ""),
            new ResendOutcome(c, ResendStatus.Opened, ""),
            new ResendOutcome(c, ResendStatus.Pending, ""),
            new ResendOutcome(c, ResendStatus.Manual, ""),
        };

        OrderDeck.App.ViewModels.PriceCorrectionViewModel.OutcomeSummary(outcomes).Should().Be(
            "1 istek gönderildi; 2 WhatsApp penceresi açıldı — her birinde gönder tuşuna bas; " +
            "1 istek WhatsApp'ta doğrulanmalı; 1 istek elle bakılmalı.");
        OrderDeck.App.ViewModels.PriceCorrectionViewModel.OutcomeSummary(outcomes.Take(1).ToList())
            .Should().Be("1 istek gönderildi.");
    }

    [Fact]
    public void Resend_button_counts_messages_when_one_person_has_two_requests()
    {
        // Aynı kişiye yayın ve genel istek için iki mesaj gider; düğme "(1 kişi)" diyordu.
        var l = Add("instagram", "@iki", 250m);
        _labels.MarkPrintedAndRecord(new[] { l.Id });
        Delivered(l.CustomerId, "session:s1", 250m);
        Delivered(l.CustomerId, "cumulative", 250m);
        var vm = new OrderDeck.App.ViewModels.PriceCorrectionViewModel(_labels, Sut(), "s1", new[] { l.Id },
            OrderDeck.App.ViewModels.PriceCorrectionMode.Printed, DateTime.Now, inFlight: () => null,
            applied: _ => { }, reprint: _ => Task.FromResult<string?>(null), cloudApiEnabled: true);
        vm.NewPriceText = "205";

        vm.ConfirmCommand.Execute(null);

        vm.ResendRows.Should().HaveCount(2);
        vm.ResendButtonText.Should().Be("Düzeltilmiş isteği gönder (1 kişi, 2 istek)");
    }

    // ── Düzeltilmiş isteği gönder ─────────────────────────────────────

    private static CorrectedRequestCandidate Candidate(string customerId, decimal prior = 600m)
        => new(customerId, "Ayşe", "session:s1", prior, null, false, 510m);

    private static PaymentRequestOutcome Asked(string scope, decimal amount)
        => new(PaymentRequestResult.AlreadyRequested,
            new PaymentPrompt(PersonScopePromptKind.Resend, scope,
                new[] { new PriorPaymentRequest(amount, null) }, "belirtec", NewTotal: 510m));

    [Fact]
    public async Task Resend_confirms_the_listed_request_once_and_sends_through_the_payment_flow()
    {
        var (_, _, _, person) = GroupedPersonWithRequest();
        var calls = new List<(IReadOnlySet<string>? Tokens, decimal? Total)>();
        var sut = Sut((c, total, _, scope, tokens) =>
        {
            calls.Add((tokens, total(_customers.GetPaymentPerson(c.Id))));
            return Task.FromResult(tokens is null ? Asked(scope, 600m) : new PaymentRequestOutcome(PaymentRequestResult.Sent));
        });

        var outcomes = await sut.ResendAsync("s1", DateTime.Now, new[] { Candidate(person.LeasedId) });

        outcomes.Should().ContainSingle().Which.Status.Should().Be(ResendStatus.Sent);
        calls.Should().HaveCount(2);
        calls[0].Tokens.Should().BeNull("önce soru gelir");
        calls[1].Tokens.Should().BeEquivalentTo(new[] { "belirtec" }, "operatörün tek onayı listedeki isteğin sorusu");
        calls.Should().OnlyContain(c => c.Total == 600m, "kişinin yayın toplamı (düzeltilmemiş burada: 250+250+100)");
    }

    [Theory]
    [InlineData(PaymentRequestResult.MergeRequired)]
    [InlineData(PaymentRequestResult.PhoneRequired)]
    [InlineData(PaymentRequestResult.BalanceUncertain)]
    [InlineData(PaymentRequestResult.PaymentInProgress)]
    [InlineData(PaymentRequestResult.ListStale)]
    public async Task Resend_skips_whoever_needs_an_extra_question(PaymentRequestResult result)
    {
        var (_, _, _, person) = GroupedPersonWithRequest();
        var calls = 0;
        var sut = Sut((_, _, _, scope, _) =>
        {
            calls++;
            return Task.FromResult(result == PaymentRequestResult.MergeRequired
                ? new PaymentRequestOutcome(result, new PaymentPrompt(PersonScopePromptKind.Merge, scope,
                    Array.Empty<PriorPaymentRequest>(), "birlestir"))
                : new PaymentRequestOutcome(result));
        });

        var outcomes = await sut.ResendAsync("s1", DateTime.Now, new[] { Candidate(person.LeasedId) });

        outcomes.Should().ContainSingle().Which.Status.Should().Be(ResendStatus.Manual, "elle bakılmalı");
        calls.Should().Be(1, "ek soru onaylanmaz — ikinci çağrı yok");
    }

    [Fact]
    public async Task Resend_skips_everyone_while_the_balance_server_is_unreachable()
    {
        // Çevrimdışıyken akış bakiyeyi denemeden tam tutarı isteyebilir (K5: çevrimdışı atlanır).
        var (_, _, _, person) = GroupedPersonWithRequest();
        var calls = 0;
        var sut = new CorrectedPaymentRequests(_customers, _labelRepo, _jobs, () => new AppSettings(),
            (_, _, _, _, _) =>
            {
                calls++;
                return Task.FromResult(new PaymentRequestOutcome(PaymentRequestResult.Sent));
            },
            balanceOffline: () => Task.FromResult(true));

        var outcomes = await sut.ResendAsync("s1", DateTime.Now, new[] { Candidate(person.LeasedId) });

        calls.Should().Be(0);
        var o = outcomes.Should().ContainSingle().Subject;
        o.Status.Should().Be(ResendStatus.Manual);
        o.Note.Should().Contain("çevrimdışı");
    }

    [Fact]
    public async Task Resend_does_not_confirm_a_question_about_a_different_request()
    {
        var (_, _, _, person) = GroupedPersonWithRequest();
        var calls = 0;
        var sut = Sut((_, _, _, scope, _) =>
        {
            calls++;
            return Task.FromResult(Asked(scope, 700m));   // listede 600 gösterildi; arada başka istek gitmiş
        });

        var outcomes = await sut.ResendAsync("s1", DateTime.Now, new[] { Candidate(person.LeasedId) });

        outcomes.Single().Status.Should().Be(ResendStatus.Manual);
        calls.Should().Be(1);
    }

    [Fact]
    public async Task Resend_against_the_real_payment_service_revises_and_sends_the_new_amount()
    {
        // Uçtan uca: gerçek PaymentRequestService (lisanssız — bakiye akışı kapalı, iş açılmaz) wa.me açar.
        var l = Add("instagram", "@uctan", 250m);
        _customers.UpdatePhone(l.CustomerId, TestPhone.NewE164());
        _labels.MarkPrintedAndRecord(new[] { l.Id });
        _labels.CorrectPrices(new[] { l.Id }, 205m);
        var launcher = new OrderDeck.Tests.Fakes.FakeUrlLauncher();
        var settingsPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var store = new SettingsStore(settingsPath);
            var s = new AppSettings();
            s.Payment.WhatsAppMessageTemplate = "Ödeme: {tutar} TL";
            store.Save(s);
            var payments = new PaymentRequestService(store, new WhatsAppMessageBuilder(), launcher,
                PaymentRequestServiceTestHelpers.StubApiClient(), new PaymentRequestServiceTestHelpers.NullLicenseProvider(),
                _jobs, customers: _customers);
            var sut = new CorrectedPaymentRequests(_customers, _labelRepo, _jobs, store, payments);

            var outcomes = await sut.ResendAsync("s1", DateTime.Now,
                new[] { new CorrectedRequestCandidate(l.CustomerId, "Uçtan", "session:s1", 250m, null, false, 205m) });

            outcomes.Single().Status.Should().Be(ResendStatus.Opened, "wa.me penceresi açıldı — gönderen operatör");
            launcher.LaunchedUrls.Should().ContainSingle().Which.Should().Contain("205");
        }
        finally
        {
            if (System.IO.File.Exists(settingsPath)) System.IO.File.Delete(settingsPath);
        }
    }
}
