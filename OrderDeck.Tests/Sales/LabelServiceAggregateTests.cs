using System;
using System.Linq;
using Dapper;
using FluentAssertions;
using Moq;
using OrderDeck.Core.Chat;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Sales;
using OrderDeck.Core.Sessions;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Core.Time;
using OrderDeck.Shared.Sales;
using OrderDeck.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.Tests.Sales;

/// <summary>
/// W1-03 (spec 2026-10-13 §6.1): <see cref="LabelService"/>'in beş toplam yazan olayı (baskı, iptal, iptali geri
/// alma, fiyat düzeltme, yedek onayı) müşteri toplamını fark sayacıyla değil etiketlerden türeterek yazar. Her
/// olaydan önce sayaç bilerek kaydırılır: türetilmiş değer kaymayı da düzeltir. <c>LastSeenAt</c> bugünkü
/// anlamını korur — yalnız bugün sayaca dokunan olaylar ilerletir (bu testler bugünkü kodda da yeşildi).
/// </summary>
public sealed class LabelServiceAggregateTests : IDisposable
{
    private const long SetupNow = 1000;
    private const long EventNow = 2000;

    private readonly InMemorySqlite _db = new();
    private readonly LabelService _svc;
    private readonly LabelRepository _labels;
    private readonly CustomerRepository _customers;
    private long _now = SetupNow;

    public LabelServiceAggregateTests()
    {
        new MigrationRunner(_db).Run();
        var clock = new Mock<IClock>();
        clock.Setup(c => c.UnixNow()).Returns(() => _now);
        new SessionRepository(_db).Insert(new StreamSession("s1", null, 1000, null, new[] { "instagram" }, null));
        _customers = new CustomerRepository(_db);
        _labels = new LabelRepository(_db);
        var customerSvc = new CustomerService(_customers, new SessionRepository(_db), _labels, clock.Object);
        _svc = new LabelService(_labels, customerSvc, _db, clock.Object, new ShipmentRepository(_db));
    }

    public void Dispose() => _db.Dispose();

    private static ChatMessage Msg(string user) => new(
        Guid.NewGuid().ToString("N"), "instagram", null, user, user, null, $"{user} aldım", 1000,
        Array.Empty<string>());

    private Label Add(string user, decimal price, string code = "A1") => _svc.Add("s1", Msg(user), price, code);

    private Label AddPrinted(string user, decimal price, string code = "A1")
    {
        var l = Add(user, price, code);
        _svc.MarkPrintedAndRecord(new[] { l.Id });
        return l;
    }

    private Customer Customer(Label anyLabelOfCustomer) => _customers.GetById(anyLabelOfCustomer.CustomerId)!;

    private (int Count, decimal Amount) Totals(Label l)
    {
        var c = Customer(l);
        return (c.TotalLabelsPrinted, c.TotalAmount);
    }

    /// <summary>Beklenen değer, türetmenin kendisinden bağımsız: müşterinin etiketleri üzerinde
    /// <see cref="LabelRules.Contribution"/>.</summary>
    private (int Count, decimal Amount) Derived(Label anyLabelOfCustomer)
    {
        using var c = _db.Open();
        var rows = c.Query<(string Id, double Price, long? PrintedAt, long? CancelledAt, long Tentative)>(
            "SELECT Id, Price, PrintedAt, CancelledAt, IsTentativeBackup FROM Label WHERE CustomerId = @id",
            new { id = anyLabelOfCustomer.CustomerId });
        return rows
            .Select(r => LabelRules.Contribution(new LabelFacts(r.Id, null, (decimal)r.Price, r.PrintedAt.HasValue,
                r.CancelledAt.HasValue, r.Tentative == 1, ShippingFee: false, ShipmentId: null)))
            .Aggregate((Count: 0, Amount: 0m), (s, x) => (s.Count + x.Count, s.Amount + x.Amount));
    }

    /// <summary>Sayacı bilerek kaydırır (eski fark sayacının birikmiş hatası) ve olayın saatine geçer.</summary>
    private void DriftThenAdvance(params Label[] ofCustomers)
    {
        using (var c = _db.Open())
            foreach (var id in ofCustomers.Select(l => l.CustomerId).Distinct())
                c.Execute("UPDATE Customer SET TotalLabelsPrinted = TotalLabelsPrinted + 5, TotalAmount = TotalAmount + 777 WHERE Id = @id",
                    new { id });
        _now = EventNow;
    }

    private void AssertDerived(Label l, int count, decimal amount)
    {
        Derived(l).Should().Be((count, amount), "senaryonun kendisi");
        Totals(l).Should().Be((count, amount), "toplam etiketlerden türetilir, kaymış sayaç üstüne eklenmez");
    }

    // ── olaydan sonra toplam = türetilmiş değer (acceptance 2) ─────────

    [Fact]
    public void Baski_kaymis_sayaci_turetilmis_degerle_ezer()
    {
        var a = Add("@a", 100m);
        var b = Add("@a", 150m);
        var other = Add("@b", 40m);
        DriftThenAdvance(a, other);

        _svc.MarkPrintedAndRecord(new[] { a.Id, b.Id, other.Id });

        AssertDerived(a, 2, 250m);
        AssertDerived(other, 1, 40m);
    }

    [Fact]
    public void Iptal_kaymis_sayaci_turetilmis_degerle_ezer()
    {
        var a = AddPrinted("@a", 100m);
        AddPrinted("@a", 60m);
        DriftThenAdvance(a);

        _svc.Cancel(new[] { a.Id }, CancelReasonCodes.Customer);

        AssertDerived(a, 1, 60m);
    }

    [Fact]
    public void Iptali_geri_alma_kaymis_sayaci_turetilmis_degerle_ezer()
    {
        var a = AddPrinted("@a", 100m);
        AddPrinted("@a", 60m);
        _svc.Cancel(new[] { a.Id }, CancelReasonCodes.Customer);
        DriftThenAdvance(a);

        _svc.Uncancel(new[] { a.Id });

        AssertDerived(a, 2, 160m);
    }

    [Fact]
    public void Basilmis_etiketin_fiyat_duzeltmesi_kaymis_sayaci_turetilmis_degerle_ezer()
    {
        var a = AddPrinted("@a", 100m);
        DriftThenAdvance(a);

        var r = _svc.CorrectPrices(new[] { a.Id }, 80m);

        r.CustomerDeltas.Should().Equal(new System.Collections.Generic.Dictionary<string, decimal> { [a.CustomerId] = -20m },
            "bilgi için dönen fark aynen kalır");
        AssertDerived(a, 1, 80m);
    }

    [Fact]
    public void Basilmamis_etiketin_fiyat_duzeltmesi_kaymis_sayaci_turetilmis_degerle_ezer()
    {
        var printed = AddPrinted("@a", 100m);
        var queued = Add("@a", 50m, code: "B2");
        DriftThenAdvance(printed);

        var r = _svc.CorrectPrices(new[] { queued.Id }, 70m);

        r.CustomerDeltas.Should().BeEmpty("basılmamış satır toplamı değiştirmez");
        AssertDerived(printed, 1, 100m);
    }

    [Fact]
    public void Basilmis_yedegin_onayi_kaymis_sayaci_turetilmis_degerle_ezer()
    {
        var parent = AddPrinted("@a", 100m);
        var backup = _svc.AddBackup(parent.Id, "instagram", "@yedek", "@yedek", "yedek");
        _svc.MarkPrintedAndRecord(new[] { backup.Id });
        DriftThenAdvance(backup);

        _svc.ConfirmBackup(backup.Id, newPrice: 90m);

        AssertDerived(backup, 1, 90m);
        AssertDerived(parent, 1, 100m);
    }

    [Fact]
    public void Basilmamis_yedegin_onayi_kaymis_sayaci_turetilmis_degerle_ezer()
    {
        var parent = AddPrinted("@a", 100m);
        var printedOfBackupBuyer = AddPrinted("@yedek", 30m, code: "C3");
        var backup = _svc.AddBackup(parent.Id, "instagram", "@yedek", "@yedek", "yedek");
        DriftThenAdvance(backup);

        _svc.ConfirmBackup(backup.Id);

        backup.CustomerId.Should().Be(printedOfBackupBuyer.CustomerId);
        AssertDerived(backup, 1, 30m);
    }

    [Fact]
    public void Zaten_iptal_edilmis_etiketi_iptal_etmek_hicbir_sey_degistirmez()
    {
        var a = AddPrinted("@a", 100m);
        _svc.Cancel(new[] { a.Id }, CancelReasonCodes.Customer);
        DriftThenAdvance(a);
        var before = Customer(a);

        _svc.Cancel(new[] { a.Id }, CancelReasonCodes.Customer);

        Customer(a).Should().Be(before, "iptal edilecek satır yok: yazım da yok");
    }

    // ── LastSeenAt bugünkü gibi (acceptance 3) ─────────────────────────

    private bool Touched(Label l)
    {
        var at = Customer(l).LastSeenAt;
        at.Should().BeOneOf(SetupNow, EventNow);
        return at == EventNow;
    }

    [Fact]
    public void LastSeen_baskida_yalniz_onayli_satirin_musterisinde_ilerler()
    {
        var normal = Add("@a", 100m);
        var parent = AddPrinted("@p", 100m);
        var tentative = _svc.AddBackup(parent.Id, "instagram", "@y", "@y", "yedek");
        _now = EventNow;

        _svc.MarkPrintedAndRecord(new[] { normal.Id, tentative.Id });

        Touched(normal).Should().BeTrue();
        Touched(tentative).Should().BeFalse("geçici yedek basılır ama toplamın dışında");
    }

    [Fact]
    public void LastSeen_iptalde_yalniz_basilmis_onayli_satirin_musterisinde_ilerler()
    {
        var printed = AddPrinted("@a", 100m);
        var queued = Add("@b", 100m);
        var parent = AddPrinted("@p", 100m);
        var tentative = _svc.AddBackup(parent.Id, "instagram", "@y", "@y", "yedek");
        _svc.MarkPrintedAndRecord(new[] { tentative.Id });
        _now = EventNow;

        _svc.Cancel(new[] { printed.Id, queued.Id, tentative.Id }, CancelReasonCodes.Customer);

        Touched(printed).Should().BeTrue();
        Touched(queued).Should().BeFalse();
        Touched(tentative).Should().BeFalse();
    }

    [Fact]
    public void LastSeen_iptali_geri_almada_yalniz_basilmis_satirin_musterisinde_ilerler()
    {
        var printed = AddPrinted("@a", 100m);
        var queued = Add("@b", 100m);
        _svc.Cancel(new[] { printed.Id, queued.Id }, CancelReasonCodes.Customer);
        _now = EventNow;

        _svc.Uncancel(new[] { printed.Id, queued.Id });

        Touched(printed).Should().BeTrue();
        Touched(queued).Should().BeFalse();
    }

    [Fact]
    public void LastSeen_zaten_iptalli_satirda_ilerlemez()
    {
        var a = AddPrinted("@a", 100m);
        _svc.Cancel(new[] { a.Id }, CancelReasonCodes.Customer);
        _now = EventNow;

        _svc.Cancel(new[] { a.Id }, CancelReasonCodes.Customer);

        Touched(a).Should().BeFalse();
    }

    [Fact]
    public void LastSeen_fiyat_duzeltmesinde_yalniz_sifir_olmayan_farkta_ilerler()
    {
        var printed = AddPrinted("@a", 100m, code: "K1");
        var queued = Add("@b", 100m, code: "K1");
        var parent = AddPrinted("@p", 100m, code: "K2");
        var tentative = _svc.AddBackup(parent.Id, "instagram", "@y", "@y", "yedek", codeOverride: "K1");
        _svc.MarkPrintedAndRecord(new[] { tentative.Id });
        var low = AddPrinted("@z", 90m, code: "K3");
        AddPrinted("@z", 110m, code: "K3");                // aynı müşteride +10 ve −10: net fark 0
        _now = EventNow;

        _svc.CorrectPrices(new[] { printed.Id, queued.Id, tentative.Id }, 120m);
        _svc.CorrectPrices(_labels.GetActiveBySessionAndCode("s1", "K3").Select(l => l.Id).ToList(), 100m);

        Touched(printed).Should().BeTrue();
        Touched(queued).Should().BeFalse("basılmamış");
        Touched(tentative).Should().BeFalse("geçici yedek");
        Touched(low).Should().BeFalse("net fark sıfır");
        Totals(low).Should().Be((2, 200m));
    }

    [Fact]
    public void LastSeen_yedek_onayinda_yalniz_basilmis_yedekte_ilerler()
    {
        var parent = AddPrinted("@p", 100m);
        var printedBackup = _svc.AddBackup(parent.Id, "instagram", "@y1", "@y1", "yedek");
        _svc.MarkPrintedAndRecord(new[] { printedBackup.Id });
        var queuedBackup = _svc.AddBackup(parent.Id, "instagram", "@y2", "@y2", "yedek");
        _now = EventNow;

        _svc.ConfirmBackup(printedBackup.Id);
        _svc.ConfirmBackup(queuedBackup.Id);

        Touched(printedBackup).Should().BeTrue();
        Touched(queuedBackup).Should().BeFalse();
    }
}
