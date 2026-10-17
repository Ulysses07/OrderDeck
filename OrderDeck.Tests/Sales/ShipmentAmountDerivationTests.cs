using System;
using System.Collections.Generic;
using System.Linq;
using Dapper;
using FluentAssertions;
using Moq;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Sales;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Core.Time;
using OrderDeck.Shared.Sales;
using OrderDeck.Tests.Sales.Rules;
using OrderDeck.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.Tests.Sales;

/// <summary>
/// Y5'in fark tabanı (<see cref="ShipmentAmountDerivation"/>, spec 2026-10-13 §7.4, §8.2-8, M12, §15-46): Ws'nin ilk
/// açılışında kargoya verilmemiş her yerel dosyanın tutarı bağlı etiketlerden bir kez türetilir, değişen dosya
/// kuyruğa girer. Durum ≤0.9.10'un bıraktığı gibi SQL'le kurulur: o sürümün iptali dosyaya dokunmuyordu.
/// </summary>
public sealed class ShipmentAmountDerivationTests : IDisposable
{
    private const string SessionId = "s1";
    private const string CustomerId = "c1";

    private readonly InMemorySqlite _db = new();
    private readonly ShipmentRepository _shipments;
    private int _seq;

    public ShipmentAmountDerivationTests()
    {
        new MigrationRunner(_db).Run();
        using var c = _db.Open();
        c.Execute("INSERT INTO StreamSession (Id, StartedAt) VALUES (@SessionId, 1)", new { SessionId });
        new CustomerRepository(_db).Insert(new Customer(CustomerId, "instagram", "@a", null, null, 100, 100,
            false, null, null, 0, 0m, BlacklistedAt: null, Address: null, Phone: null));
        _shipments = new ShipmentRepository(_db);
    }

    public void Dispose() => _db.Dispose();

    /// <summary>Gönderilmiş (SyncedAt dolu) dosya: kuyruğa alma gözle görülsün.</summary>
    private Shipment File(string id, ShipmentStatus status, decimal amount)
    {
        _shipments.Insert(new Shipment(id, CustomerId, status, 1000, null,
            status == ShipmentStatus.Shipped ? 1500 : null, amount));
        _shipments.MarkSynced(id, 2000, _shipments.GetById(id)!.Revision);
        return _shipments.GetById(id)!;
    }

    /// <summary>Etiket, durumu doğrudan SQL'le (amaç durumu kurmak, onu üreten akışı sınamak değil).</summary>
    private string Label(decimal price, string? shipmentId, bool printed = true, bool cancelled = false,
        bool tentative = false, bool shippingFee = false, string? code = "A1")
    {
        var id = $"l{++_seq}";
        using var c = _db.Open();
        c.Execute(
            @"INSERT INTO Label (Id, SessionId, CustomerId, Platform, Username, MessageText, Code, Price, AddedAt,
                                 PrintedAt, CancelledAt, CancelReason, IsTentativeBackup, IsShippingFee, ShipmentId)
              VALUES (@id, @SessionId, @CustomerId, 'instagram', '@a', 'aldım', @code, @price, @seq,
                      @printedAt, @cancelledAt, @reason, @tentative, @fee, @shipmentId)",
            new
            {
                id, SessionId, CustomerId, code, price = (double)price, seq = _seq,
                printedAt = printed ? 1500L : (long?)null,
                cancelledAt = cancelled ? 1600L : (long?)null,
                reason = cancelled ? CancelReasonCodes.Customer : null,
                tentative = tentative ? 1 : 0,
                fee = shippingFee ? 1 : 0,
                shipmentId,
            });
        return id;
    }

    private long? DerivedAt() => new SalesSyncStateRepository(_db).Get().ShipmentsDerivedAt;

    private LabelService NewLabelService()
    {
        var clock = new Mock<IClock>();
        clock.Setup(c => c.UnixNow()).Returns(1000L);
        var labels = new LabelRepository(_db);
        return new LabelService(labels,
            new CustomerService(new CustomerRepository(_db), new SessionRepository(_db), labels, clock.Object),
            _db, clock.Object, _shipments);
    }

    [Fact]
    public void Ws_ilk_acilisi_Ws_oncesi_iptali_tabandan_cikarir_geri_alma_tutari_bir_kez_ekler()
    {
        // §10.3: F = 400, içinde Ws öncesinde iptal edilmiş K5 150 (o sürüm dosyaya dokunmadı, F 400'de kaldı).
        var f = File("F", ShipmentStatus.Held, 400m);
        Label(250m, "F");
        var k5 = Label(150m, "F", cancelled: true);
        // Kargoya verilmiş dosya, kargodan sonra iptal edilmiş bağlı etiketiyle: tutarı donmuştur.
        var g = File("G", ShipmentStatus.Shipped, 300m);
        Label(300m, "G", cancelled: true);
        DerivedAt().Should().BeNull("ön koşul: göç 049 damgayı boş bırakır");

        var result = ShipmentAmountDerivation.DeriveAllOnce(_db, nowUnix: 9000);

        result.Should().Be(new ShipmentDerivationResult(1, 150m));
        var derived = _shipments.GetById("F")!;
        derived.CumulativeAmount.Should().Be(250m, "türetilmiş taban: iptal edilmiş bağlı etiket düşer");
        derived.SyncedAt.Should().BeNull("TR_Shipment_Enqueue değişen dosyayı kuyruğa alır");
        derived.Revision.Should().Be(f.Revision + 1, "tam bir kez");
        var shipped = _shipments.GetById("G")!;
        shipped.CumulativeAmount.Should().Be(300m, "kargoya verilmiş dosyanın tutarı donmuştur");
        shipped.Revision.Should().Be(g.Revision);
        shipped.SyncedAt.Should().Be(2000);
        DerivedAt().Should().Be(9000);

        NewLabelService().Uncancel(new[] { k5 });
        _shipments.GetById("F")!.CumulativeAmount.Should().Be(400m, "geri alma fiyatı bir kez ekler — 550 değil");

        var afterUncancel = _shipments.GetById("F")!;
        ShipmentAmountDerivation.DeriveAllOnce(_db, nowUnix: 9500).Should().BeNull("ikinci açılış türetmez");
        var second = _shipments.GetById("F")!;
        second.CumulativeAmount.Should().Be(400m);
        second.Revision.Should().Be(afterUncancel.Revision, "ikinci açılış hiçbir şeyi kuyruğa almaz");
        DerivedAt().Should().Be(9000);
    }

    [Fact]
    public void Yalniz_degeri_degisen_dosya_yazilir_alici_odemeli_de_turetilir()
    {
        var right = File("dogru", ShipmentStatus.Pending, 100m);
        Label(100m, "dogru");
        var wrong = File("kaymis", ShipmentStatus.RecipientPays, 999m);
        Label(40m, "kaymis");
        Label(60m, "kaymis", printed: false, shippingFee: true, code: null);
        Label(90m, "kaymis", tentative: true);

        var result = ShipmentAmountDerivation.DeriveAllOnce(_db, nowUnix: 9000);

        result.Should().Be(new ShipmentDerivationResult(1, 899m));
        var untouched = _shipments.GetById("dogru")!;
        untouched.Revision.Should().Be(right.Revision, "değeri aynı dosya yazılmaz, yeniden gönderilmez");
        untouched.SyncedAt.Should().Be(2000);
        var fixedFile = _shipments.GetById("kaymis")!;
        fixedFile.CumulativeAmount.Should().Be(100m, "40 + kargo ücreti 60; geçici yedek sayılmaz");
        fixedFile.Revision.Should().Be(wrong.Revision + 1);
    }

    [Fact]
    public void Kayan_nokta_artigi_degisiklik_sayilmaz()
    {
        // AttachLabels tutarı ondalık aritmetikle kurar (0,1 + 0,2 = 0,3); türetme aynı aritmetikle karşılaştırır.
        var before = File("f", ShipmentStatus.Pending, 0.3m);
        Label(0.1m, "f");
        Label(0.2m, "f");

        ShipmentAmountDerivation.DeriveAllOnce(_db, nowUnix: 9000).Should().Be(ShipmentDerivationResult.None);

        _shipments.GetById("f")!.Revision.Should().Be(before.Revision);
    }

    [Fact]
    public void Mod_1de_dosyaya_dokunmaz_damgayi_basar()
    {
        // Mod On'da dosya sunucunundur (yazım ABORT ederdi); türetmeyi sunucu yapar.
        var before = File("F", ShipmentStatus.Held, 400m);
        Label(250m, "F");
        Label(150m, "F", cancelled: true);
        using (var c = _db.Open()) c.Execute("UPDATE SalesSyncState SET Mode = 1 WHERE Id = 1");

        var act = () => ShipmentAmountDerivation.DeriveAllOnce(_db, nowUnix: 9000);

        act.Should().NotThrow().Which.Should().Be(ShipmentDerivationResult.None);
        var after = _shipments.GetById("F")!;
        after.CumulativeAmount.Should().Be(400m);
        after.Revision.Should().Be(before.Revision);
        DerivedAt().Should().Be(9000);
    }

    public static IEnumerable<object[]> ShipmentAmountNames()
        => LabelRulesVectors.Names("shipmentAmount").Select(n => new object[] { n });

    [Theory]
    [MemberData(nameof(ShipmentAmountNames))]
    public void Turetme_ortak_kural_vektorleriyle_ayni(string name)
    {
        var c = LabelRulesVectors.ShipmentAmount().Single(v => v.Name == name);
        // Vektördeki her dosya açık bir yerel dosya; saklanan tutar bilerek yanlış (türetme düzeltmeli).
        foreach (var sid in c.Labels.Select(l => l.ShipmentId).OfType<string>().Append(c.ShipmentId)
                     .Distinct(StringComparer.Ordinal))
            File(sid, ShipmentStatus.Pending, -1m);
        foreach (var l in c.Labels)
            Label(l.Price, l.ShipmentId, l.Printed, l.Cancelled, l.Tentative, l.ShippingFee, l.Code);

        ShipmentAmountDerivation.DeriveAllOnce(_db, nowUnix: 9000);

        _shipments.GetById(c.ShipmentId)!.CumulativeAmount.Should().Be(c.Expected, $"vektör {name}");
        LabelRules.DerivedShipmentAmount(c.ShipmentId, c.Labels).Should().Be(c.Expected, "ön koşul: ortak kural");
    }
}
