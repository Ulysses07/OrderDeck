using System;
using System.Collections.Generic;
using Dapper;
using FluentAssertions;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Sales;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.Tests.Sales;

/// <summary>
/// "Y5 dosya bloğu" (<see cref="ShipmentAmountAdjuster"/>, spec 2026-10-13 §7.4): kargo dosyası tutarının mod Off'taki
/// tek yerel yazıcısı. Etiket akışlarından bağımsız sınanır: W1-07'nin push yanıtı uygulaması da aynı bloğu
/// <see cref="SyncApplyScope"/> içinde çağıracak.
/// </summary>
public sealed class ShipmentAmountAdjusterTests : IDisposable
{
    private readonly InMemorySqlite _db = new();
    private readonly ShipmentRepository _shipments;

    public ShipmentAmountAdjusterTests()
    {
        new MigrationRunner(_db).Run();
        new CustomerRepository(_db).Insert(
            new Customer("c1", "instagram", "@a", null, null, 100, 100,
                false, null, null, 0, 0m, BlacklistedAt: null, Address: null, Phone: null));
        _shipments = new ShipmentRepository(_db);
    }

    public void Dispose() => _db.Dispose();

    private Shipment Insert(string id, ShipmentStatus status, decimal amount)
    {
        var s = new Shipment(id, "c1", status, 1000, null, status == ShipmentStatus.Shipped ? 1500 : null, amount);
        _shipments.Insert(s);
        // Gönderilmiş say: kuyruğa alma gözle görülsün.
        _shipments.MarkSynced(id, 2000, _shipments.GetById(id)!.Revision);
        return _shipments.GetById(id)!;
    }

    private static Dictionary<string, decimal> Deltas(params (string Id, decimal Delta)[] items)
    {
        var d = new Dictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var (id, delta) in items) d[id] = delta;
        return d;
    }

    private ShipmentAdjustResult ApplyInWrite(Dictionary<string, decimal> deltas)
    {
        using var write = DbWrite.Begin(_db);
        var result = ShipmentAmountAdjuster.Apply(deltas, write);
        write.Commit();
        return result;
    }

    [Fact]
    public void Acik_dosyaya_farki_yazar_ve_tam_bir_kez_kuyruga_alir()
    {
        var pending = Insert("f-pending", ShipmentStatus.Pending, 550m);
        var held = Insert("f-held", ShipmentStatus.Held, 400m);
        var recipient = Insert("f-recipient", ShipmentStatus.RecipientPays, 100m);

        var result = ApplyInWrite(Deltas(("f-pending", -250m), ("f-held", 150m), ("f-recipient", -100m)));

        result.Applied.Should().HaveCount(3);
        result.Missing.Should().Be(0);
        result.Shipped.Should().BeEmpty();
        result.ServerOwned.Should().BeFalse();
        _shipments.GetById("f-pending")!.CumulativeAmount.Should().Be(300m);
        _shipments.GetById("f-held")!.CumulativeAmount.Should().Be(550m);
        _shipments.GetById("f-recipient")!.CumulativeAmount.Should().Be(0m);
        foreach (var before in new[] { pending, held, recipient })
        {
            var after = _shipments.GetById(before.Id)!;
            after.SyncedAt.Should().BeNull($"{before.Id}: dosya değişikliği shipments/sync ile gitmeli");
            after.Revision.Should().Be(before.Revision + 1,
                $"{before.Id}: Revision'ı yazım kendisi artırır, kuyruğa alma tetikleyicisi etkisiz — çift artış yok");
        }
    }

    [Fact]
    public void Kilit_altinda_da_kuyruga_alir()
    {
        // Push yanıtının current'ı SyncApplyScope içinde uygulanır: kilit TR_Shipment_Enqueue'yu susturur. Mod
        // Off'ta dosyanın tek yazıcısı bu kopya; değişen tutar yine de gönderilmeli (§7.4, §5.2).
        var before = Insert("f1", ShipmentStatus.Held, 550m);

        using (var scope = SyncApplyScope.Begin(_db))
        {
            ShipmentAmountAdjuster.Apply(Deltas(("f1", -250m)), scope);
            scope.Commit();
        }

        var after = _shipments.GetById("f1")!;
        after.CumulativeAmount.Should().Be(300m);
        after.SyncedAt.Should().BeNull("kilit tetikleyiciyi susturur; yazım SyncedAt'i kendisi düşürmeli");
        after.Revision.Should().Be(before.Revision + 1);
    }

    [Fact]
    public void Kargoya_verilmis_dosyanin_tutari_donmustur()
    {
        var before = Insert("f-gitti", ShipmentStatus.Shipped, 550m);

        var result = ApplyInWrite(Deltas(("f-gitti", -250m)));

        result.Shipped.Should().Equal("f-gitti");
        result.Applied.Should().BeEmpty();
        var after = _shipments.GetById("f-gitti")!;
        after.CumulativeAmount.Should().Be(550m);
        after.Revision.Should().Be(before.Revision);
        after.SyncedAt.Should().Be(2000);
    }

    [Fact]
    public void Yerelde_olmayan_dosya_atlanir_ve_sayilir_istisna_yok()
    {
        Insert("f1", ShipmentStatus.Pending, 100m);

        var result = ApplyInWrite(Deltas(("baska-pc-dosyasi", -50m), ("f1", 20m)));

        result.Missing.Should().Be(1);
        result.Applied.Keys.Should().Equal("f1");
        _shipments.GetById("f1")!.CumulativeAmount.Should().Be(120m);
    }

    [Fact]
    public void Mod_1de_hicbir_dosyaya_dokunmaz()
    {
        // Mod On'da dosya sunucunundur; yazım TR_Shipment_ServerOwned_U'ya çarpardı (§5.1-a, M6).
        var before = Insert("f1", ShipmentStatus.Held, 550m);
        using (var c = _db.Open()) c.Execute("UPDATE SalesSyncState SET Mode = 1 WHERE Id = 1");

        var result = ApplyInWrite(Deltas(("f1", -250m), ("baska-pc-dosyasi", 10m)));

        result.ServerOwned.Should().BeTrue();
        result.Applied.Should().BeEmpty();
        result.Missing.Should().Be(0, "mod 1'de dosyalara hiç bakılmaz");
        var after = _shipments.GetById("f1")!;
        after.CumulativeAmount.Should().Be(550m);
        after.Revision.Should().Be(before.Revision);
    }

    [Fact]
    public void Sifir_fark_yazmaz_ama_uygulanmis_sayilir()
    {
        var before = Insert("f1", ShipmentStatus.Pending, 100m);

        var result = ApplyInWrite(Deltas(("f1", 0m)));

        result.Applied.Should().ContainKey("f1").WhoseValue.Should().Be(0m);
        _shipments.GetById("f1")!.Revision.Should().Be(before.Revision, "değişmeyen dosya yeniden gönderilmez");
    }

    [Fact]
    public void Pay_farki_bagli_ve_gecici_olmayan_etiketlerden()
    {
        static Label L(string id, decimal price, string? shipmentId, bool tentative = false)
            => new(id, "s1", "c1", "instagram", "@a", "m", "A1", price, 1000, null,
                IsTentativeBackup: tentative, ShipmentId: shipmentId);

        var deltas = ShipmentAmountAdjuster.ShareDeltas(new[]
        {
            L("a", 100m, "f1"), L("b", 50m, "f1"), L("c", 70m, "f2"), L("bagsiz", 40m, null),
            L("yedek", 90m, "f1", tentative: true),
        }, sign: -1);

        deltas.Should().BeEquivalentTo(new Dictionary<string, decimal> { ["f1"] = -150m, ["f2"] = -70m });
    }
}
