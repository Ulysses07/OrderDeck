using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using OrderDeck.App.Services;
using OrderDeck.App.Services.PriceCorrection;
using OrderDeck.App.Services.Sync;
using OrderDeck.Core.Chat;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Sales;
using OrderDeck.Core.Sessions;
using OrderDeck.Core.Settings;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Core.Time;
using OrderDeck.Licensing.Api;
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
        // Y5'in tabanı açılışta türetilmiş say (ShipmentAmountDerivation): damga boşken dosya bloğu yazmaz.
        using (var c = _db.Open()) c.Execute("UPDATE SalesSyncState SET ShipmentsDerivedAt = 900 WHERE Id = 1");
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

    public void Dispose()
    {
        _server?.Dispose();
        _db.Dispose();
    }

    private Label Add(string platform, string user, decimal price, string code = "A12")
        => _labels.Add("s1", new ChatMessage(Guid.NewGuid().ToString("N"), platform, null, user, user, null,
            $"{user} aldım", 1000, Array.Empty<string>()), price, code);

    /// <summary>Fiyat düzeltme çekmecesinin yolu: bakiyeden ödenmiş etiketler yazımdan önce okunur, ödendikleri
    /// fiyat ilk düzeltmede saklanır.</summary>
    private PriceCorrectionResult Correct(string id, decimal price)
        => _labels.CorrectPrices(new[] { id }, price, Sut().BalancePaidLabels("s1", _labelRepo.GetByIds(new[] { id })));

    private PaidPerson? PaidOf(PriceCorrectionResult result)
        => Sut().Plan("s1", result.Corrected.Select(c => (c.Before, c.Delta))).Paid.SingleOrDefault();

    /// <summary>Uygulamanın canlı ayar örneği (kargo kapalı); kargo ayarı sağlayıcısı gerçek — mod Off'ta bu örnek.</summary>
    private readonly AppSettings _live = new();

    private CorrectedPaymentRequests Sut(CorrectedPaymentRequests.RequestPayment? request = null)
        => new(_customers, _labelRepo, _jobs, new ShippingSettingsProvider(new SalesSyncStateRepository(_db), _live),
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
    public void Mod_On_yeni_toplamin_kargosu_yerel_ayardan_degil_lisans_geneli_ayardan()
    {
        // Spec 2026-10-13 §6.2 (Y4): plan ödeme akışının isteyeceği tutarı göstermeli — mod On'da lisans geneli ayarla.
        // Kişinin yayın toplamı 2 × 205 + 100 = 510: yerel 500/60'ta eşik üstü (kargosuz), lisansın 1000/80'inde değil.
        var (a1, a2, _, _) = GroupedPersonWithRequest();
        var result = _labels.CorrectPrices(new[] { a1.Id, a2.Id }, 205m);
        _live.Shipping = new ShippingSettings { FreeShippingThreshold = 500m, ShippingFee = 60m };
        new SalesSyncStateRepository(_db).SaveServerState(3, 1000m, 80m, scrubOrderText: false);
        var affected = result.Corrected.Select(c => (c.Before, c.Delta)).ToList();

        Sut().Plan("s1", affected).ToResend.Should().ContainSingle()
            .Which.NewTotal.Should().Be(510m, "mod Off: yerel ayar, eşik üstü");

        using (var c = _db.Open()) c.Execute("UPDATE SalesSyncState SET Mode = 1 WHERE Id = 1");
        Sut().Plan("s1", affected).ToResend.Should().ContainSingle()
            .Which.NewTotal.Should().Be(590m, "mod On: lisans geneli 1000/80 — 510 + 80");
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
        var shipmentSvc = new ShipmentService(_shipments, _labelRepo, LocalShipping.Off());
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
        var shipmentSvc = new ShipmentService(_shipments, _labelRepo, LocalShipping.Off());
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
        var shipmentSvc = new ShipmentService(_shipments, _labelRepo, LocalShipping.Off());
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
    public void Balance_paid_difference_is_against_what_was_paid_not_only_the_last_correction()
    {
        // 250 TL'lik istek tamamen bakiyeden düşüldü; fiyat 200'e, sonra 190'a düzeltildi. "Fark" yalnız o düzeltmenin
        // farkıydı: ikinci düzeltme −10 diyordu, ilkinin −50'si hiçbir yerde kalmıyordu. Müşteri 250 ödedi, yeni
        // toplamı 190: fark −60.
        var l = Add("instagram", "@bakiye", 250m);
        _labels.MarkPrintedAndRecord(new[] { l.Id });
        AppliedAndDelivered(l.CustomerId, "session:s1", 250m, applied: 250m);

        var first = PaidOf(Correct(l.Id, 200m))!;
        first.Delta.Should().Be(-50m, "tek düzeltme");
        first.StepDelta.Should().Be(-50m);
        _labelRepo.GetPaidPrices(new[] { l.Id }).Should().ContainKey(l.Id).WhoseValue.Should().Be(250m,
            "bakiyeden ödenmiş etiketin ödendiği fiyat ilk düzeltmede saklanır");

        var paid = PaidOf(Correct(l.Id, 190m))!;

        paid.ByBalance.Should().BeTrue();
        paid.Delta.Should().Be(-60m, "ödenen 250, yeni fiyat 190");
        paid.StepDelta.Should().Be(-10m, "bu düzeltmenin kendi farkı");
    }

    [Fact]
    public void Dekont_paid_difference_is_against_the_paid_price_not_only_the_last_correction()
    {
        // Dekontla ödenmiş (dosyaya bağlı) etiket 250 → 200 → 190: ikinci sonuç −10 diyordu. Dekont tutarı etikete ya
        // da dosyaya bağlı saklanmıyor; ödenen fiyatı ilk düzeltme yazar.
        var l = Add("instagram", "@dekont", 250m);
        _labels.MarkPrintedAndRecord(new[] { l.Id });
        var shipmentSvc = new ShipmentService(_shipments, _labelRepo, LocalShipping.Off());
        shipmentSvc.AttachLabels(shipmentSvc.GetOrCreateOpenShipment(l.CustomerId).Id, new[] { l.Id });

        var first = _labels.CorrectPrices(new[] { l.Id }, 200m);
        Sut().Plan("s1", first.Corrected.Select(c => (c.Before, c.Delta)))
            .Paid.Should().ContainSingle().Which.Delta.Should().Be(-50m, "tek düzeltme");
        var second = _labels.CorrectPrices(new[] { l.Id }, 190m);

        var paid = Sut().Plan("s1", second.Corrected.Select(c => (c.Before, c.Delta)))
            .Paid.Should().ContainSingle().Subject;

        paid.ByDekont.Should().BeTrue();
        paid.Delta.Should().Be(-60m, "ödenen 250, yeni fiyat 190");
        paid.StepDelta.Should().Be(-10m);
    }

    [Fact]
    public void Dekont_paid_price_is_the_price_the_label_had_when_the_dekont_attached_it()
    {
        // 250 → 200 dekonttan ÖNCE düzeltildi, müşteri 200 ödedi (dekont kaydı etiketi dosyaya bağladı), sonra
        // 190'a düzeltildi: ödenen 200, fark −10 — dosyaya bağlanmadan önceki düzeltme ödenenin parçası değil.
        var l = Add("instagram", "@sonra", 250m);
        _labels.MarkPrintedAndRecord(new[] { l.Id });
        _labels.CorrectPrices(new[] { l.Id }, 200m);
        var shipmentSvc = new ShipmentService(_shipments, _labelRepo, LocalShipping.Off());
        shipmentSvc.AttachLabels(shipmentSvc.GetOrCreateOpenShipment(l.CustomerId).Id, new[] { l.Id });

        var result = _labels.CorrectPrices(new[] { l.Id }, 190m);
        var paid = Sut().Plan("s1", result.Corrected.Select(c => (c.Before, c.Delta)))
            .Paid.Should().ContainSingle().Subject;

        paid.Delta.Should().Be(-10m);
    }

    [Fact]
    public void Balance_paid_price_is_the_price_the_label_had_when_the_request_was_paid()
    {
        // 250 → 200 istekten ÖNCE düzeltildi (ödenmemişti), 200'lük istek tamamen bakiyeden düşüldü, sonra 190: ödenen
        // 200, fark −10.
        var l = Add("instagram", "@sonra", 250m);
        _labels.MarkPrintedAndRecord(new[] { l.Id });
        PaidOf(Correct(l.Id, 200m)).Should().BeNull("istek yok — ödenmedi");
        AppliedAndDelivered(l.CustomerId, "session:s1", 200m, applied: 200m);

        var paid = PaidOf(Correct(l.Id, 190m))!;

        paid.Delta.Should().Be(-10m);
        paid.StepDelta.Should().Be(-10m);
    }

    /// <summary>Bundan sonra basılan etiketler teslim edilmiş isteklerden SONRA basılmış olur.</summary>
    private void PrintClockAfterRequests()
        => _clock.Setup(c => c.UnixNow()).Returns(DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds());

    [Fact]
    public void Balance_paid_difference_leaves_out_a_purchase_made_after_the_request()
    {
        // @x'in 250'lik A1 isteği tamamen bakiyeden düşüldü; sonra B2'yi 100'e aldı (istenmedi, ödenmedi). A1 250 → 200:
        // fark kişinin bugünkü toplamından (300) hesaplanınca "+50" çıkıyordu — müşteriye 50 iade gerekirken.
        var a1 = Add("instagram", "@x", 250m, "A1");
        _labels.MarkPrintedAndRecord(new[] { a1.Id });
        AppliedAndDelivered(a1.CustomerId, "session:s1", 250m, applied: 250m);
        PrintClockAfterRequests();
        var b2 = Add("instagram", "@x", 100m, "B2");
        _labels.MarkPrintedAndRecord(new[] { b2.Id });

        var paid = PaidOf(Correct(a1.Id, 200m))!;

        paid.ByBalance.Should().BeTrue();
        paid.Delta.Should().Be(-50m, "yalnız düzeltilen ödenmiş etiket: 200 − 250");
        paid.StepDelta.Should().Be(-50m);
    }

    [Fact]
    public void Balance_paid_difference_in_the_cumulative_scope_leaves_out_later_streams()
    {
        // Genel istek (250) yayın dışında tamamen bakiyeden düşüldü; sonraki yayında @x 1000'lik ürün aldı. Yayın
        // Raporu → "Basılan etiketler"den A1 250 → 200: fark genel toplamdan hesaplanınca "+950" çıkıyordu.
        var a1 = Add("instagram", "@x", 250m, "A1");
        _labels.MarkPrintedAndRecord(new[] { a1.Id });
        AppliedAndDelivered(a1.CustomerId, "cumulative", 250m, applied: 250m);
        new SessionRepository(_db).Insert(new StreamSession("s2", null, 2000, null, new[] { "instagram" }, null));
        PrintClockAfterRequests();
        var big = _labels.Add("s2", new ChatMessage(Guid.NewGuid().ToString("N"), "instagram", null, "@x", "@x", null,
            "@x aldım", 1000, Array.Empty<string>()), 1000m, "Z9");
        _labels.MarkPrintedAndRecord(new[] { big.Id });
        _customers.GetPaymentPerson(a1.CustomerId).Members.Sum(m => m.TotalAmount)
            .Should().Be(1250m, "genel toplam sonraki yayının alımını da içeriyor");

        var paid = PaidOf(Correct(a1.Id, 200m))!;

        paid.ByBalance.Should().BeTrue();
        paid.Delta.Should().Be(-50m);
    }

    [Fact]
    public void Cumulative_new_total_is_read_from_the_labels_not_the_customer_counter()
    {
        // W3 (spec 2026-10-13 §6.1 "para yolları sayacı okumaz"): genel kapsamın yeni toplamı kişinin bütün
        // yayınlardaki etiketlerinden (LabelRules.Contribution) — Customer.TotalAmount önbelleği bozuk olsa da.
        var a1 = Add("instagram", "@x", 250m, "A1");
        _labels.MarkPrintedAndRecord(new[] { a1.Id });
        new SessionRepository(_db).Insert(new StreamSession("s2", null, 2000, null, new[] { "instagram" }, null));
        var later = _labels.Add("s2", new ChatMessage(Guid.NewGuid().ToString("N"), "instagram", null, "@x", "@x", null,
            "@x aldım", 1000, Array.Empty<string>()), 100m, "Z9");
        var queued = _labels.Add("s2", new ChatMessage(Guid.NewGuid().ToString("N"), "instagram", null, "@x", "@x", null,
            "@x aldım", 1000, Array.Empty<string>()), 40m, "Z8");
        _labels.MarkPrintedAndRecord(new[] { later.Id });
        _labelRepo.GetById(queued.Id)!.PrintedAt.Should().BeNull("ön koşul: kuyrukta");
        Delivered(a1.CustomerId, "cumulative", 350m);
        var result = _labels.CorrectPrices(new[] { a1.Id }, 205m);
        // Düzeltme toplamı türetti; sayaç ondan SONRA bozulur.
        using (var c = _db.Open())
            c.Execute("UPDATE Customer SET TotalAmount = 999 WHERE Id = @id", new { id = a1.CustomerId });

        var plan = Sut().Plan("s1", result.Corrected.Select(c => (c.Before, c.Delta)));

        var row = plan.ToResend.Should().ContainSingle().Subject;
        row.ScopeKey.Should().Be("cumulative");
        row.NewTotal.Should().Be(305m, "bütün yayınların basılmış etiketleri: 205 + 100; kuyruktaki sayılmaz");
    }

    [Fact]
    public void Dekont_difference_of_a_correction_leaves_out_an_earlier_corrections_settled_difference()
    {
        // A1 200 + B2 100 dekontla ödendi (ikisi açık dosyada). A1 200 → 250: "+50"; operatör 50'yi aldı, ikinci
        // dekontu kaydetti. Sonra B2 100 → 90: kişinin bütün açık dosyasından hesaplanınca "+40" çıkıyordu — gerçek −10.
        var a1 = Add("instagram", "@x", 200m, "A1");
        var b2 = Add("instagram", "@x", 100m, "B2");
        _labels.MarkPrintedAndRecord(new[] { a1.Id, b2.Id });
        var shipmentSvc = new ShipmentService(_shipments, _labelRepo, LocalShipping.Off());
        var open = shipmentSvc.GetOrCreateOpenShipment(a1.CustomerId);
        shipmentSvc.AttachLabels(open.Id, new[] { a1.Id, b2.Id });
        PaidOf(Correct(a1.Id, 250m))!.Delta.Should().Be(50m);
        shipmentSvc.AttachLabels(open.Id, new[] { a1.Id, b2.Id });   // ikinci dekont: bağlı etiketler atlanır

        var paid = PaidOf(Correct(b2.Id, 90m))!;

        paid.ByDekont.Should().BeTrue();
        paid.Delta.Should().Be(-10m);
        paid.StepDelta.Should().Be(-10m);
    }

    [Fact]
    public void Balance_difference_of_a_correction_leaves_out_an_earlier_corrections_settled_difference()
    {
        // A1 250 + B2 100 için 350'lik istek tamamen bakiyeden düşüldü. A1 → 200: "−50"; operatör 50'yi bakiyeye iade
        // etti. Sonra B2 → 90: kapsam toplamından hesaplanınca "−60" çıkıyordu (110 iade) — gerçek −10.
        var a1 = Add("instagram", "@x", 250m, "A1");
        var b2 = Add("instagram", "@x", 100m, "B2");
        _labels.MarkPrintedAndRecord(new[] { a1.Id, b2.Id });
        AppliedAndDelivered(a1.CustomerId, "session:s1", 350m, applied: 350m);
        PaidOf(Correct(a1.Id, 200m))!.Delta.Should().Be(-50m);

        var paid = PaidOf(Correct(b2.Id, 90m))!;

        paid.ByBalance.Should().BeTrue();
        paid.Delta.Should().Be(-10m);
        paid.StepDelta.Should().Be(-10m);
    }

    [Fact]
    public void Only_the_labels_a_balance_paid_request_covers_are_paid()
    {
        // Aynı kodun iki etiketi: biri bakiyeden ödenmiş isteğin içinde, öbürü istekten sonra basıldı (ödenmedi).
        // İkisi birlikte 250 → 200: ödenen yalnız ilki.
        var covered = Add("instagram", "@x", 250m, "A1");
        _labels.MarkPrintedAndRecord(new[] { covered.Id });
        AppliedAndDelivered(covered.CustomerId, "session:s1", 250m, applied: 250m);
        PrintClockAfterRequests();
        var later = Add("instagram", "@x", 250m, "A1");
        _labels.MarkPrintedAndRecord(new[] { later.Id });
        var ids = new[] { covered.Id, later.Id };

        var paid = PaidOf(_labels.CorrectPrices(ids, 200m, Sut().BalancePaidLabels("s1", _labelRepo.GetByIds(ids))))!;

        paid.Delta.Should().Be(-50m);
        paid.StepDelta.Should().Be(-50m);
        _labelRepo.GetPaidPrices(ids).Keys.Should().Equal(new[] { covered.Id }, "ödenmemiş etiketin fiyatı saklanmaz");
    }

    [Fact]
    public void Unpaid_label_of_a_balance_paid_person_shows_no_paid_row()
    {
        var a1 = Add("instagram", "@x", 250m, "A1");
        _labels.MarkPrintedAndRecord(new[] { a1.Id });
        AppliedAndDelivered(a1.CustomerId, "session:s1", 250m, applied: 250m);
        PrintClockAfterRequests();
        var b2 = Add("instagram", "@x", 100m, "B2");
        _labels.MarkPrintedAndRecord(new[] { b2.Id });

        var plan = Sut().Plan("s1", Correct(b2.Id, 90m).Corrected.Select(c => (c.Before, c.Delta)));

        plan.Paid.Should().BeEmpty("B2 hiçbir istekte yok — ödenmedi");
        _labelRepo.GetPaidPrices(new[] { b2.Id }).Should().BeEmpty();
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
        var shipmentSvc = new ShipmentService(_shipments, _labelRepo, LocalShipping.Off());
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
            DateTime.Now, inFlight: () => null,
            applied: _ => { }, reprint: _ => Task.FromResult<string?>(null), cloudApiEnabled: true);
        vm.NewPriceText = "205";

        vm.ConfirmCommand.Execute(null);

        vm.ResendRows.Should().HaveCount(2);
        vm.ResendButtonText.Should().Be("Düzeltilmiş isteği gönder (1 kişi, 2 istek)");
    }

    /// <summary>Basılmış, 250 TL istenmiş bir etiketin 205'e düzeltildiği çekmece; "düzeltilmiş isteği gönder"
    /// listesinde tek kişi.</summary>
    private OrderDeck.App.ViewModels.PriceCorrectionViewModel CorrectedWithOneResendRow(
        string user, CorrectedPaymentRequests payments)
    {
        var l = Add("instagram", user, 250m);
        _labels.MarkPrintedAndRecord(new[] { l.Id });
        Delivered(l.CustomerId, "session:s1", 250m);
        var vm = new OrderDeck.App.ViewModels.PriceCorrectionViewModel(_labels, payments,
            "s1", new[] { l.Id }, DateTime.Now,
            inFlight: () => null, applied: _ => { }, reprint: _ => Task.FromResult<string?>(null),
            cloudApiEnabled: true);
        vm.NewPriceText = "205";
        vm.ConfirmCommand.Execute(null);
        vm.SkipReprintCommand.Execute(null);
        vm.ResendRows.Should().ContainSingle();
        return vm;
    }

    [Fact]
    public async Task Close_works_while_corrected_requests_are_sent_and_the_manual_list_is_reported_when_done()
    {
        // "Düzeltilmiş isteği gönder" sürerken çekmece kilitleniyordu (Kapat ve çarpı pasif, ESC etkisiz): yavaş
        // ağda dakikalarca kod/fiyat kutusu ve kuyruk örtülü kalırdı. Kapanır; gönderim arka planda sürer,
        // "elle bakılmalı" adları ve sebepleri bitince kabuğa bildirilir.
        var gate = new TaskCompletionSource<PaymentRequestOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var vm = CorrectedWithOneResendRow("@elle", Sut((_, _, _, _, _) => gate.Task));
        var reports = new List<string>();
        vm.WorkFinished = (report, _) => reports.Add(report);
        var stack = new OrderDeck.App.Services.Drawers.DrawerStack();
        var closed = stack.ShowAsync("Fiyatı Düzelt", d => { vm.Close = () => d.Close(true); return new object(); });
        var sending = vm.ResendCommand.ExecuteAsync(null);

        vm.DoneCommand.Execute(null);                     // "Kapat"

        stack.IsOpen.Should().BeFalse("gönderim sürerken de kapanır");
        (await closed).Should().BeTrue();
        reports.Should().BeEmpty("gönderim henüz bitmedi");

        gate.SetResult(new PaymentRequestOutcome(PaymentRequestResult.PhoneRequired));
        await sending;

        reports.Should().ContainSingle().Which.Should().Contain("elle bakılmalı")
            .And.Contain(vm.ResendRows[0].Candidate.Display)
            .And.Contain("WhatsApp numarası gerekli");
    }

    [Theory]
    [InlineData(PaymentRequestResult.Sent, false)]
    [InlineData(PaymentRequestResult.Opened, true)]
    [InlineData(PaymentRequestResult.SendPending, true)]
    [InlineData(PaymentRequestResult.PhoneRequired, true)]
    public async Task Resend_report_says_whether_it_leaves_work_for_the_operator(
        PaymentRequestResult result, bool leavesWork)
    {
        // Kalıcı sonuç şeridi sınırda yalnız bilgi kayıtlarını düşürür: gönderilmiş sayılmayan istek (elle bakılmalı,
        // WhatsApp'ta doğrulanmalı) operatöre iş bırakır, kaydı düşmemeli.
        var vm = CorrectedWithOneResendRow("@k", Sut((_, _, _, _, _) =>
            Task.FromResult(new PaymentRequestOutcome(result))));
        var flags = new List<bool>();
        vm.WorkFinished = (_, needsAction) => flags.Add(needsAction);

        await vm.ResendCommand.ExecuteAsync(null);

        flags.Should().Equal(leavesWork);
    }

    [Fact]
    public void Late_progress_callback_does_not_overwrite_the_final_resend_summary()
    {
        // Toplu gönderim baştan sona senkron bitebiliyor (her aday beklemeden döner: telefon gerekli, liste bayat,
        // müşteri kaydı yok). İlerleme bildirimi dispatcher'a postalanıyor, özetten SONRA koşup çekmecedeki notu
        // "Gönderiliyor… 1/1" yapıyordu: iş bitmişken "gönderiliyor" görünüyor, özet ("elle bakılmalı") görünmüyordu.
        var error = OrderDeck.Tests.App.ThemeTestHost.RunOnSta(() =>
        {
            var previous = System.Threading.SynchronizationContext.Current;
            System.Threading.SynchronizationContext.SetSynchronizationContext(
                new System.Windows.Threading.DispatcherSynchronizationContext(
                    System.Windows.Threading.Dispatcher.CurrentDispatcher));
            try
            {
                var vm = CorrectedWithOneResendRow("@elle", Sut((_, _, _, _, _) =>
                    Task.FromResult(new PaymentRequestOutcome(PaymentRequestResult.PhoneRequired))));

                var sending = vm.ResendCommand.ExecuteAsync(null);
                sending.IsCompleted.Should().BeTrue("her istek beklemeden döndü: gönderim senkron bitti");
                OrderDeck.Tests.App.ThemeTestHost.Pump();   // postalanmış ilerleme bildirimleri koşar

                vm.ResendNote.Should().Be("1 istek elle bakılmalı.");
            }
            finally
            {
                System.Threading.SynchronizationContext.SetSynchronizationContext(previous);
            }
        });

        error.Should().BeNull();
    }

    [Fact]
    public async Task Outcome_report_names_people_to_check_by_hand_and_to_verify_in_whatsapp()
    {
        // F5: sonuç şeridinde "WhatsApp'ta doğrulanmalı" kişiler de adıyla — pencere açıldı ama gönder tuşuna
        // basıldı mı bilinmiyor (Opened) ya da gönderim sonucu belirsiz (Pending). Yalnız sayı yazıyordu:
        // kime bakılacağı bilinmiyordu.
        var labels = new[] { Add("instagram", "@acildi", 250m), Add("instagram", "@belirsiz", 250m),
            Add("instagram", "@elle", 250m), Add("instagram", "@gitti", 250m) };
        _labels.MarkPrintedAndRecord(labels.Select(l => l.Id).ToList());
        foreach (var l in labels) Delivered(l.CustomerId, "session:s1", 250m);
        var byCustomer = new Dictionary<string, PaymentRequestResult>
        {
            [labels[0].CustomerId] = PaymentRequestResult.Opened,
            [labels[1].CustomerId] = PaymentRequestResult.SendPending,
            [labels[2].CustomerId] = PaymentRequestResult.PhoneRequired,
            [labels[3].CustomerId] = PaymentRequestResult.Sent,
        };
        var payments = Sut((customer, _, _, _, _) =>
            Task.FromResult(new PaymentRequestOutcome(byCustomer[customer.Id])));
        var vm = new OrderDeck.App.ViewModels.PriceCorrectionViewModel(_labels, payments, "s1", new[] { labels[0].Id },
            DateTime.Now, inFlight: () => null,
            applied: _ => { }, reprint: _ => Task.FromResult<string?>(null), cloudApiEnabled: true);
        var reports = new List<string>();
        vm.WorkFinished = (report, _) => reports.Add(report);
        vm.NewPriceText = "205";
        vm.ConfirmCommand.Execute(null);
        vm.ResendRows.Should().HaveCount(4, "kodun bütün etiketleri düzeldi — dört kişi");

        await vm.ResendCommand.ExecuteAsync(null);

        var report = reports.Should().ContainSingle().Subject;
        report.Should().Contain("\nElle bakılmalı:\n• @elle (WhatsApp numarası gerekli)");
        report.Should().Contain("\nWhatsApp'ta doğrulanmalı:")
            .And.Contain("• @acildi (WhatsApp'ta açıldı — gönder tuşuna bas)")
            .And.Contain("• @belirsiz (gönderim işleniyor — WhatsApp'ta ulaştığını doğrula)");
        report.Should().NotContain("• @gitti", "gönderilen kişi listelenmez");
    }

    [Fact]
    public async Task Esc_closes_the_drawer_while_corrected_requests_are_sent()
    {
        var gate = new TaskCompletionSource<PaymentRequestOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var vm = CorrectedWithOneResendRow("@elle", Sut((_, _, _, _, _) => gate.Task));
        var stack = new OrderDeck.App.Services.Drawers.DrawerStack();
        var closed = stack.ShowAsync("Fiyatı Düzelt", d => { vm.Close = () => d.Close(true); return new object(); });
        var sending = vm.ResendCommand.ExecuteAsync(null);

        stack.CloseTop().Should().BeTrue();

        stack.IsOpen.Should().BeFalse();
        (await closed).Should().BeFalse();
        gate.SetResult(new PaymentRequestOutcome(PaymentRequestResult.Sent));
        await sending;
        vm.ResendRows[0].OutcomeText.Should().Be("gönderildi", "gönderim çekmece kapandıktan sonra da sürdü");
    }

    [Fact]
    public async Task Second_resend_is_refused_inline_while_another_corrections_resend_runs()
    {
        // İlk düzeltmenin çekmecesi gönderim sürerken kapatıldı; ikinci düzeltmeden "gönder" aynı anda ikinci bir
        // toplu gönderim başlatmamalı (aynı kişiye iki istek gidebilir) — satır içi reddedilir, düğme yerinde kalır.
        var first = new TaskCompletionSource<PaymentRequestOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var payments = Sut((_, _, _, _, _) =>
            ++calls == 1 ? first.Task : Task.FromResult(new PaymentRequestOutcome(PaymentRequestResult.Sent)));
        var vm1 = CorrectedWithOneResendRow("@bir", payments);
        var vm2 = CorrectedWithOneResendRow("@iki", payments);
        var sending = vm1.ResendCommand.ExecuteAsync(null);
        payments.IsResending.Should().BeTrue();

        await vm2.ResendCommand.ExecuteAsync(null);

        calls.Should().Be(1, "ikinci gönderim başlamadı");
        vm2.ResendNote.Should().Contain("gönderimi sürüyor").And.Contain("bitince tekrar dene");
        vm2.IsResendAvailable.Should().BeTrue("düğme yerinde: bitince tekrar denenir");
        vm2.ResendRows[0].OutcomeText.Should().BeNull();

        first.SetResult(new PaymentRequestOutcome(PaymentRequestResult.Sent));
        await sending;
        payments.IsResending.Should().BeFalse();

        await vm2.ResendCommand.ExecuteAsync(null);
        calls.Should().Be(2);
        vm2.ResendRows[0].OutcomeText.Should().Be("gönderildi");
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
        var sut = new CorrectedPaymentRequests(_customers, _labelRepo, _jobs, LocalShipping.Off(),
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
                _jobs, LocalShipping.Of(store), customers: _customers);
            var sut = new CorrectedPaymentRequests(_customers, _labelRepo, _jobs, LocalShipping.Of(store), payments);

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

    // ── Mod On: başka bilgisayarların kayıtları (W3-10, spec 2026-10-13 §6.4 "Teslim kayıtlarının okunması") ─────

    private OrderDeck.Tests.Sync.FakeSalesServer? _server;
    private RemotePaymentRecords? _remote;

    private sealed class FixedLicense(string key) : ICurrentLicenseProvider
    {
        public string? CurrentLicenseKey => key;
    }

    /// <summary>Uzak kayıtların önbelleği, sahte sunucuya bağlı. <paramref name="modeOn"/> ise yerel kip 1.</summary>
    private RemotePaymentRecords UseRemote(bool modeOn = true)
    {
        if (modeOn)
            using (var c = _db.Open()) c.Execute("UPDATE SalesSyncState SET Mode = 1 WHERE Id = 1");
        _server = new OrderDeck.Tests.Sync.FakeSalesServer { Mode = modeOn ? 1 : 0 };
        var http = new System.Net.Http.HttpClient(_server, disposeHandler: false) { BaseAddress = new Uri("https://test.local") };
        _remote = new RemotePaymentRecords(new LicenseApiClient(http, new LicenseTokenStore()), new FixedLicense(_server.Lisans),
            new SalesSyncStateRepository(_db), new ReplicaIdentityRepository(_db), new PendingPaymentRecordRepository(_db),
            NullLogger<RemotePaymentRecords>.Instance);
        return _remote;
    }

    private CorrectedPaymentRequests SutOn(CorrectedPaymentRequests.RequestPayment? request = null)
        => new(_customers, _labelRepo, _jobs, new ShippingSettingsProvider(new SalesSyncStateRepository(_db), _live),
            request ?? ((_, _, _, _, _) => throw new InvalidOperationException("gönderim beklenmiyordu")),
            remote: _remote);

    /// <summary>Kira kapsamına uyan ("N" biçimli Id'li) yayın: yayın kapsamının uzak kayıtları ancak böyle okunur.</summary>
    private string NewSession()
    {
        var id = Guid.NewGuid().ToString("N");
        new SessionRepository(_db).Insert(new StreamSession(id, null, 1000, null, new[] { "instagram" }, null));
        return id;
    }

    private Label AddIn(string sessionId, string user, decimal price, string code = "A1")
        => _labels.Add(sessionId, new ChatMessage(Guid.NewGuid().ToString("N"), "instagram", null, user, user, null,
            $"{user} aldım", 1000, Array.Empty<string>()), price, code);

    private Guid OwnReplica => new ReplicaIdentityRepository(_db).GetReplicaId()!.Value;

    private static readonly DateTimeOffset T0 = new(2026, 10, 18, 10, 0, 0, TimeSpan.Zero);

    /// <summary>Sunucudaki bir kayıt; <paramref name="minute"/> sunucunun kapanış sırası.</summary>
    private void ServerRecord(string member, string scope, int minute, decimal? net, IEnumerable<string> covers,
        Guid? replica = null, string? machine = "PC-A", bool fullyFromBalance = false, decimal? applied = null,
        decimal? totalBeforeBalance = null, bool unconfirmed = false)
        => _server!.AddPaymentPrior(new OrderDeck.Tests.Sync.FakeSalesServer.PaymentPrior(
            Guid.NewGuid(), machine, new[] { member }, scope, net, totalBeforeBalance, T0.AddMinutes(minute),
            T0.AddMinutes(minute), unconfirmed, ReplicaId: replica, OrderIds: covers.Select(Guid.Parse).ToList(),
            AppliedAmount: applied, FullyFromBalance: fullyFromBalance));

    private int ListCalls => _server!.Calls(OrderDeck.Tests.Sync.FakeSalesServer.PaymentRequestsList).Count;

    [Fact]
    public async Task Mod_On_baska_PCnin_L_yi_iceren_kaydi_L_ye_odeme_istendi_rozeti_verir()
    {
        // PC-B L'yi içeren bir istek yaptı; bu bilgisayarda hiç iş yok. Rozet ancak ön okumadan sonra: hesap zaman uyumlu ve
        // yalnız önbellekten okur.
        UseRemote();
        var sid = NewSession();
        var l = AddIn(sid, "@uzak", 250m);
        var other = AddIn(sid, "@uzak", 100m, "B2");
        _labels.MarkPrintedAndRecord(new[] { l.Id, other.Id });
        ServerRecord(l.CustomerId, CorrectedPaymentRequests.SessionScope(sid), 1, 250m, new[] { l.Id }, machine: "PC-B");
        var sut = SutOn();
        var labels = _labelRepo.GetByIds(new[] { l.Id, other.Id });

        sut.Badges(sid, labels).RequestedLabels.Should().BeEmpty("ön okumadan önce önbellek boş — yalnız yerel");
        (await sut.PrefetchAsync(sid, labels)).Should().BeTrue();

        var badges = sut.Badges(sid, labels);
        badges.RequestedLabels.Should().Equal(new[] { l.Id }, "uzak Covers sipariş kümesiyle: B2 kümede değil");
        badges.BalancePaidLabels.Should().BeEmpty();
        var printed = new OrderDeck.App.ViewModels.PrintedLabelsViewModel(_labelRepo, sut.Badges);
        printed.Load(sid);
        printed.Rows.Single(r => r.Id == l.Id).StatusText.Should().Be("ödeme istendi");
        printed.Rows.Single(r => r.Id == other.Id).StatusText.Should().BeEmpty();
    }

    [Fact]
    public async Task Mod_On_L_yi_icermeyen_daha_yeni_uzak_kayit_rozeti_kaldirir()
    {
        // Bu bilgisayar L'yi içeren isteği yaptı (kaydı sunucuda, 10:01); PC-B sonra (10:02) L'yi içermeyen bir istek yaptı.
        // Son istek PC-B'ninki: L'nin rozeti yok. Saatler karşılaştırılmaz — yerel işin yeri listedeki kendi kaydıdır.
        UseRemote();
        var sid = NewSession();
        var l = AddIn(sid, "@son", 250m);
        _labels.MarkPrintedAndRecord(new[] { l.Id });
        var scope = CorrectedPaymentRequests.SessionScope(sid);
        Delivered(l.CustomerId, scope, 250m);
        ServerRecord(l.CustomerId, scope, 1, 250m, new[] { l.Id }, replica: OwnReplica, machine: "BU-PC");
        ServerRecord(l.CustomerId, scope, 2, 0m, Array.Empty<string>(), machine: "PC-B");
        var sut = SutOn();
        var labels = _labelRepo.GetByIds(new[] { l.Id });
        sut.Badges(sid, labels).RequestedLabels.Should().Equal(new[] { l.Id }, "ön koşul: yerel iş L'yi içeriyor");

        await sut.PrefetchAsync(sid, labels);

        sut.Badges(sid, labels).RequestedLabels.Should().BeEmpty("en yeni istek PC-B'nin ve L'yi içermiyor");
    }

    [Fact]
    public async Task Mod_On_yerel_isi_olmayan_kiside_bu_kopyanin_kaydi_da_son_istek_adayidir()
    {
        // GUID'siz kişide yerel iş açılmaz: bu bilgisayarın kirayla yaptığı isteği yalnız sunucudaki kendi kaydı temsil eder.
        // Kendi kayıtları hep atlanıyordu — PC-A'nın daha eski kaydı (L1) "son istek" seçiliyor, bu bilgisayarın L1+L3'ü
        // içeren isteği rozette ve #508 planında görünmüyordu: L3 düzeltilince "eski tutarla istek gitmiş" çıkmıyordu.
        UseRemote();
        var sid = NewSession();
        var l1 = AddIn(sid, "@isesiz", 250m);
        var l3 = AddIn(sid, "@isesiz", 200m, "B2");
        _labels.MarkPrintedAndRecord(new[] { l1.Id, l3.Id });
        var scope = CorrectedPaymentRequests.SessionScope(sid);
        ServerRecord(l1.CustomerId, scope, 1, 250m, new[] { l1.Id }, machine: "PC-A");
        ServerRecord(l1.CustomerId, scope, 2, 450m, new[] { l1.Id, l3.Id }, replica: OwnReplica, machine: "BU-PC");
        _jobs.GetPersonScopeJobs(new[] { l1.CustomerId }, scope).Jobs.Should().BeEmpty("ön koşul: yerel iş yok");
        var sut = SutOn();
        var labels = _labelRepo.GetByIds(new[] { l1.Id, l3.Id });

        await sut.PrefetchAsync(sid, labels);

        sut.Badges(sid, labels).RequestedLabels.Should().BeEquivalentTo(new[] { l1.Id, l3.Id },
            "son istek bu bilgisayarın kaydı: ikisini de içeriyor");
        var result = _labels.CorrectPrices(new[] { l3.Id }, 150m);
        var row = sut.Plan(sid, result.Corrected.Select(c => (c.Before, c.Delta))).ToResend.Should().ContainSingle().Subject;
        row.PriorAmount.Should().Be(450m, "eski tutar bu bilgisayarın isteği");
    }

    [Fact]
    public async Task Mod_On_en_yeni_uzak_kayit_teslim_ve_tamami_bakiyeden_ise_etiket_odendi()
    {
        UseRemote();
        var sid = NewSession();
        var paid = AddIn(sid, "@bakiye", 250m);
        var unsure = AddIn(sid, "@belirsiz", 300m);
        _labels.MarkPrintedAndRecord(new[] { paid.Id, unsure.Id });
        var scope = CorrectedPaymentRequests.SessionScope(sid);
        ServerRecord(paid.CustomerId, scope, 1, 0m, new[] { paid.Id }, fullyFromBalance: true, applied: 250m);
        // Sonucu bilinmeyen kayıt "ödendi" değildir: teslim edilmiş ∧ tamamı bakiyeden.
        ServerRecord(unsure.CustomerId, scope, 1, 0m, new[] { unsure.Id }, fullyFromBalance: true, applied: 300m,
            unconfirmed: true);
        var sut = SutOn();
        var labels = _labelRepo.GetByIds(new[] { paid.Id, unsure.Id });

        await sut.PrefetchAsync(sid, labels);

        var badges = sut.Badges(sid, labels);
        badges.BalancePaidLabels.Should().Equal(new[] { paid.Id });
        badges.RequestedLabels.Should().Equal(new[] { unsure.Id });
        sut.BalancePaidLabels(sid, labels).Should().Equal(new[] { paid.Id },
            "düzeltmeden önce okunan küme rozetlerle aynı kural — ödendiği fiyat saklansın");
        var printed = new OrderDeck.App.ViewModels.PrintedLabelsViewModel(_labelRepo, sut.Badges);
        printed.Load(sid);
        printed.Rows.Single(r => r.Id == paid.Id).StatusText.Should().Be("ödendi");

        var result = _labels.CorrectPrices(new[] { paid.Id }, 200m, sut.BalancePaidLabels(sid, labels));
        var row = sut.Plan(sid, result.Corrected.Select(c => (c.Before, c.Delta))).Paid.Should().ContainSingle().Subject;
        row.ByBalance.Should().BeTrue();
        row.Delta.Should().Be(-50m);
    }

    [Fact]
    public async Task Mod_On_kendi_kopyasinin_kayitlari_cift_sayilmaz()
    {
        // Bu bilgisayarın (ve geri yüklemeden önceki kimliğinin) kayıtları yerel işle temsil edilir; birleştirmeye yalnız
        // başka kopyaların kayıtları girer. Uzak sayılsalardı: @kendi'nin L'yi içermeyen, tamamı bakiyeden görünen kayıtları
        // yerel isteğin yerine geçerdi; yerel işi olmayan @eski'nin kaydı (geri yüklenen eski yedekte işi yok) L2'yi
        // "ödendi" gösterirdi.
        UseRemote();
        var previous = Guid.NewGuid();
        new ReplicaIdentityRepository(_db).SetPendingPreviousReplicaId(previous);
        var sid = NewSession();
        var l = AddIn(sid, "@kendi", 250m);
        var l2 = AddIn(sid, "@eski", 300m);
        _labels.MarkPrintedAndRecord(new[] { l.Id, l2.Id });
        var scope = CorrectedPaymentRequests.SessionScope(sid);
        Delivered(l.CustomerId, scope, 250m);
        ServerRecord(l.CustomerId, scope, 1, 0m, Array.Empty<string>(), replica: previous, fullyFromBalance: true,
            applied: 250m);
        ServerRecord(l.CustomerId, scope, 2, 0m, Array.Empty<string>(), replica: OwnReplica, fullyFromBalance: true,
            applied: 250m);
        ServerRecord(l2.CustomerId, scope, 3, 0m, new[] { l2.Id }, replica: previous, fullyFromBalance: true,
            applied: 300m);
        var sut = SutOn();
        var labels = _labelRepo.GetByIds(new[] { l.Id, l2.Id });

        (await sut.PrefetchAsync(sid, labels)).Should().BeTrue();

        var badges = sut.Badges(sid, labels);
        badges.RequestedLabels.Should().Equal(new[] { l.Id }, "son istek yerel iş");
        badges.BalancePaidLabels.Should().BeEmpty("bu kopyanın kayıtları uzak kayıt sayılmaz");
        var plan = sut.Plan(sid, labels.Select(x => (x, 0m)));
        plan.RequestedPersons.Should().Be(1, "aynı istek iki kez sayılmaz");
        plan.Paid.Should().BeEmpty();
    }

    [Fact]
    public async Task Mod_On_yerel_kaydi_kuyrukta_bekleyen_is_en_yeni_sayilir()
    {
        // Listede bu bilgisayarın eski kaydı (10:01) ve PC-B'nin sonraki kaydı (10:02) var; bu bilgisayarın SON teslimi henüz
        // gönderilmedi (kuyrukta). Gönderilmemiş yerel kayıt en yenidir: PC-B'nin kaydı son istek sayılmaz.
        UseRemote();
        var sid = NewSession();
        var l = AddIn(sid, "@kuyruk", 250m);
        _labels.MarkPrintedAndRecord(new[] { l.Id });
        var scope = CorrectedPaymentRequests.SessionScope(sid);
        Delivered(l.CustomerId, scope, 250m);
        ServerRecord(l.CustomerId, scope, 1, 250m, new[] { l.Id }, replica: OwnReplica);
        ServerRecord(l.CustomerId, scope, 2, 0m, Array.Empty<string>(), machine: "PC-B");
        var sut = SutOn();
        var labels = _labelRepo.GetByIds(new[] { l.Id });
        await sut.PrefetchAsync(sid, labels);
        sut.Badges(sid, labels).RequestedLabels.Should().BeEmpty("ön koşul: kuyruk boşken PC-B'nin kaydı en yeni");

        var item = new OrderDeck.Licensing.Api.Models.PaymentRequestBackfillItem(Guid.NewGuid(), l.CustomerId,
            new[] { l.CustomerId }, scope, new[] { Guid.Parse(l.Id) }, "delivered", 250m, null, false, T0);
        new PendingPaymentRecordRepository(_db).Enqueue(PendingPaymentRecordCodec.Key(item.RecordId),
            PendingPaymentRecordKinds.Record, PendingPaymentRecordCodec.EncodeRecord(item), 1000).Should().BeTrue();

        sut.Badges(sid, labels).RequestedLabels.Should().Equal(new[] { l.Id }, "yerel kaydı kuyrukta: yerel iş en yeni");
    }

    [Fact]
    public async Task Mod_On_PC_A_isteginden_sonra_duzeltme_adayi_PC_A_netini_gosterir_ve_soru_kendiliginden_onaylanir()
    {
        // Bu bilgisayar 350 istedi (kaydı 10:01); PC-A sonra 400 istedi (10:02; kargo eklenmiş tutar). Fiyat düzeltmesinin
        // planı "son istenen"i PC-A'nın netiyle göstermeli; toplu gönderimde begin'in sorusunun son öncülü de PC-A'nın kaydı:
        // tutar eşit olduğu için operatörün listedeki onayı sorunun onayıdır — ikinci soru yok, istek bir kez gider.
        UseRemote();
        var sid = NewSession();
        var a = AddIn(sid, "@pca", 250m);
        var b = AddIn(sid, "@pca", 100m, "B2");
        _labels.MarkPrintedAndRecord(new[] { a.Id, b.Id });
        var scope = CorrectedPaymentRequests.SessionScope(sid);
        Delivered(a.CustomerId, scope, 350m);
        ServerRecord(a.CustomerId, scope, 1, 350m, new[] { a.Id, b.Id }, replica: OwnReplica, machine: "BU-PC");
        ServerRecord(a.CustomerId, scope, 2, 400m, new[] { a.Id, b.Id }, machine: "PC-A", totalBeforeBalance: 400m);
        var calls = new List<IReadOnlySet<string>?>();
        var sut = SutOn((_, _, _, s, tokens) =>
        {
            calls.Add(tokens);
            return Task.FromResult(tokens is null
                ? new PaymentRequestOutcome(PaymentRequestResult.AlreadyRequested,
                    new PaymentPrompt(PersonScopePromptKind.Resend, s,
                        new[]
                        {
                            new PriorPaymentRequest(350m, T0.AddMinutes(1), Machine: "BU-PC"),
                            new PriorPaymentRequest(400m, T0.AddMinutes(2), Machine: "PC-A"),
                        }, "uzak-onay"))
                : new PaymentRequestOutcome(PaymentRequestResult.Sent));
        });
        await sut.PrefetchAsync(sid, _labelRepo.GetByIds(new[] { a.Id, b.Id }));

        var result = _labels.CorrectPrices(new[] { a.Id }, 205m);
        var plan = sut.Plan(sid, result.Corrected.Select(c => (c.Before, c.Delta)));

        var row = plan.ToResend.Should().ContainSingle().Subject;
        row.PriorAmount.Should().Be(400m, "son istek PC-A'nın — yerel işin 350'si değil");
        row.PriorAt.Should().Be(T0.AddMinutes(2));
        row.PriorUnconfirmed.Should().BeFalse();
        row.NewTotal.Should().Be(305m);

        var outcomes = await sut.ResendAsync(sid, DateTime.Now, plan.ToResend);

        outcomes.Should().ContainSingle().Which.Status.Should().Be(ResendStatus.Sent);
        calls.Should().HaveCount(2, "soru bir kez, onaylı çağrı bir kez");
        calls[1].Should().BeEquivalentTo(new[] { "uzak-onay" });
    }

    [Fact]
    public async Task Mod_On_fiyat_duzeltme_ozeti_ve_sonucu_uzak_istegi_on_okumadan_sonra_sayar()
    {
        // Çekmece beklemez: özet önce yerel işlerle kurulur, uzak kayıtlar arka planda okununca yeniden hesaplanır. Sonucun
        // planı da önce ön okur (arada senkron önbelleği boşalttıysa yeniden) sonra kurulur.
        UseRemote();
        var sid = NewSession();
        var l = AddIn(sid, "@cekmece", 250m);
        _labels.MarkPrintedAndRecord(new[] { l.Id });
        ServerRecord(l.CustomerId, CorrectedPaymentRequests.SessionScope(sid), 1, 250m, new[] { l.Id }, machine: "PC-B");
        var vm = new OrderDeck.App.ViewModels.PriceCorrectionViewModel(_labels, SutOn(), sid, new[] { l.Id },
            DateTime.Now, inFlight: () => null, applied: _ => { }, reprint: _ => Task.FromResult<string?>(null),
            cloudApiEnabled: true);

        await vm.SummaryRefresh;

        vm.Summary.Should().Contain("1 kişiye eski tutarla ödeme isteği gitmiş", "PC-B'nin isteği etiketi içeriyor");
        _remote!.Invalidate();                               // arada satış akışı değişiklik indirdi
        vm.NewPriceText = "205";
        await vm.ConfirmCommand.ExecuteAsync(null);
        vm.SkipReprintCommand.Execute(null);
        var row = vm.ResendRows.Should().ContainSingle().Subject.Candidate;
        row.PriorAmount.Should().Be(250m);
        row.NewTotal.Should().Be(205m);
    }

    [Fact]
    public async Task Mod_On_fiyat_duzeltme_onayi_bakiyeden_odenmis_uzak_istegi_yazimdan_once_on_okur()
    {
        // PC-B L'nin isteğini tamamı bakiyeden düşerek teslim etti. Çekmece açılınca ön okuma onu getirdi; operatör onaylamadan
        // satış akışı önbelleği boşalttı. Yazımdan önceki "bakiyeden ödenmiş" kümesi yalnız önbellekten okunsaydı L'nin
        // ödendiği fiyat (PaidPrice) saklanmaz, ikinci düzeltmenin "ödendi, fark"ı yanlış çıkardı (spec §6.4).
        UseRemote();
        var sid = NewSession();
        var l = AddIn(sid, "@bakiye_onay", 250m);
        _labels.MarkPrintedAndRecord(new[] { l.Id });
        ServerRecord(l.CustomerId, CorrectedPaymentRequests.SessionScope(sid), 1, 0m, new[] { l.Id }, machine: "PC-B",
            fullyFromBalance: true, applied: 250m);
        var vm = new OrderDeck.App.ViewModels.PriceCorrectionViewModel(_labels, SutOn(), sid, new[] { l.Id },
            DateTime.Now, inFlight: () => null, applied: _ => { }, reprint: _ => Task.FromResult<string?>(null),
            cloudApiEnabled: true);
        await vm.SummaryRefresh;
        _remote!.Invalidate();                               // arada satış akışı değişiklik indirdi

        vm.NewPriceText = "200";
        await vm.ConfirmCommand.ExecuteAsync(null);

        _labelRepo.GetPaidPrices(new[] { l.Id }).Should().ContainKey(l.Id)
            .WhoseValue.Should().Be(250m, "ödendiği fiyat ilk düzeltmede saklandı");
    }

    [Fact]
    public async Task Mod_On_liste_cevrimdisiyken_sonuc_yerel_istisna_yok()
    {
        UseRemote();
        var sid = NewSession();
        var l = AddIn(sid, "@cevrimdisi", 250m);
        _labels.MarkPrintedAndRecord(new[] { l.Id });
        var scope = CorrectedPaymentRequests.SessionScope(sid);
        Delivered(l.CustomerId, scope, 250m);
        ServerRecord(l.CustomerId, scope, 1, 0m, Array.Empty<string>(), machine: "PC-B");
        _server!.Offline = true;
        var sut = SutOn();
        var labels = _labelRepo.GetByIds(new[] { l.Id });

        var prefetch = () => sut.PrefetchAsync(sid, labels);

        (await prefetch.Should().NotThrowAsync()).Subject.Should().BeFalse();
        sut.Badges(sid, labels).RequestedLabels.Should().Equal(new[] { l.Id }, "okunamayan kişi yalnız yerel");
        sut.Plan(sid, new[] { (_labelRepo.GetById(l.Id)!, 0m) }).RequestedPersons.Should().Be(1);

        _server.Offline = false;
        _server.EnqueueStatus(OrderDeck.Tests.Sync.FakeSalesServer.PaymentRequestsList, 500);
        (await sut.PrefetchAsync(sid, labels)).Should().BeFalse("sunucu hatası: yalnız yerel");
        sut.Badges(sid, labels).RequestedLabels.Should().Equal(new[] { l.Id });
    }

    [Fact]
    public async Task Mod_Offta_uzak_kayit_okunmaz_sifir_istek()
    {
        UseRemote(modeOn: false);
        var sid = NewSession();
        var l = AddIn(sid, "@off", 250m);
        _labels.MarkPrintedAndRecord(new[] { l.Id });
        ServerRecord(l.CustomerId, CorrectedPaymentRequests.SessionScope(sid), 1, 250m, new[] { l.Id }, machine: "PC-B");
        var sut = SutOn();
        var labels = _labelRepo.GetByIds(new[] { l.Id });

        (await sut.PrefetchAsync(sid, labels, force: true)).Should().BeFalse();

        sut.Badges(sid, labels).RequestedLabels.Should().BeEmpty("mod Off: bugünkü davranış, yalnız yerel");
        sut.Plan(sid, new[] { (_labelRepo.GetById(l.Id)!, 0m) }).RequestedPersons.Should().Be(0);
        _server!.RequestLog.Should().BeEmpty("mod Off'ta lisans çözümü de liste de yok");
    }

    [Fact]
    public async Task Mod_On_uc_yuz_kisilik_yukleme_istek_butcesi_kadar_istek_atar()
    {
        // Hız sınırı (100 istek/dk/IP) müşteri ve satış akışıyla paylaşılıyor: bir yükleme lisans çözümü dahil en çok
        // RequestBudget istek atar; kalanlar sonraki yüklemede (okunmuşlar yeniden istenmez).
        UseRemote();
        var sid = NewSession();
        var labels = Enumerable.Range(0, 300).Select(i => AddIn(sid, $"@alici{i}", 100m)).ToList();
        _labels.MarkPrintedAndRecord(labels.Select(l => l.Id).ToArray());
        var sut = SutOn();
        var printed = _labelRepo.GetByIds(labels.Select(l => l.Id).ToList());

        await sut.PrefetchAsync(sid, printed);

        _server!.RequestLog.Should().HaveCount(RemotePaymentRecords.RequestBudget, "lisans çözümü + liste istekleri");
        ListCalls.Should().Be(RemotePaymentRecords.RequestBudget - 1);

        await sut.PrefetchAsync(sid, printed);

        ListCalls.Should().Be(2 * RemotePaymentRecords.RequestBudget - 1,
            "ikinci yükleme sıradakileri okur (lisans önbellekte); okunmuşlar yeniden istenmez");
        _server.Calls(OrderDeck.Tests.Sync.FakeSalesServer.PaymentRequestsList)
            .Select(c => c.RawJson).Should().OnlyHaveUniqueItems();
    }
}
