using System;
using System.IO;
using Dapper;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using OrderDeck.App;
using OrderDeck.App.Services;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Sales;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.Tests.Services;

/// <summary>
/// Açık kargo dosyalarının açılıştaki tek seferlik türetmesi (spec 2026-10-13 §7.4, §8.2-8; W1-04):
/// <c>AppHost</c> <see cref="ShipmentAmountsBoot.Run"/>'ı kilit temizliğinden ve müşteri toplamlarından sonra çağırır.
/// Günlükte yalnız sayılar — müşteri adı, kullanıcı adı ya da Id yok (KVKK, CLAUDE.md günlük kuralı).
/// </summary>
public sealed class ShipmentAmountsBootTests : IDisposable
{
    private readonly InMemorySqlite _db = new();
    private readonly CapturingLogger<AppHost> _log = new();
    private readonly string _customerId = Guid.NewGuid().ToString("N");
    private readonly string _shipmentId = Guid.NewGuid().ToString("N");
    private readonly string _username = $"alici-{Guid.NewGuid():N}";
    private readonly string _displayName = $"Takma {Guid.NewGuid():N}";

    public ShipmentAmountsBootTests()
    {
        new MigrationRunner(_db).Run();
        new CustomerRepository(_db).Insert(new Customer(_customerId, "tiktok", _username, _displayName, null,
            FirstSeenAt: 100, LastSeenAt: 200, IsBlacklisted: false, BlacklistReason: null, Notes: null,
            TotalLabelsPrinted: 0, TotalAmount: 0m, BlacklistedAt: null, Address: null, Phone: null));
        new ShipmentRepository(_db).Insert(new Shipment(_shipmentId, _customerId, ShipmentStatus.Held, 1, 2, null, 400m));
        using var c = _db.Open();
        c.Execute("INSERT INTO StreamSession (Id, StartedAt) VALUES ('s1', 1)");
        // Ws öncesi iptal edilmiş bağlı etiket: dosyada hâlâ duruyor (≤0.9.10 dosyaya dokunmuyordu).
        c.Execute(@"INSERT INTO Label (Id, SessionId, CustomerId, Platform, Username, MessageText, Price, AddedAt, PrintedAt,
                                       CancelledAt, ShipmentId)
                    VALUES ('l1', 's1', @id, 'tiktok', @username, 'aldım', 250, 1, 2, NULL, @sid),
                           ('l2', 's1', @id, 'tiktok', @username, 'aldım', 150, 1, 2, 3, @sid)",
            new { id = _customerId, username = _username, sid = _shipmentId });
    }

    public void Dispose() => _db.Dispose();

    private decimal Amount() => new ShipmentRepository(_db).GetById(_shipmentId)!.CumulativeAmount;

    [Fact]
    public void Ilk_acilista_tabani_turetir_gunlukte_yalniz_sayilar()
    {
        var r = ShipmentAmountsBoot.Run(_db, nowUnix: 9000, _log);

        r.Should().Be(new ShipmentDerivationResult(1, 150m));
        Amount().Should().Be(250m);
        new SalesSyncStateRepository(_db).Get().ShipmentsDerivedAt.Should().Be(9000);
        var entry = _log.Entries.Should().ContainSingle().Subject;
        entry.Level.Should().Be(LogLevel.Information);
        entry.Message.Should().Contain("1").And.Contain("150");
        foreach (var personal in new[] { _customerId, _username, _displayName, _shipmentId })
            entry.Full.Should().NotContain(personal, "günlükte kişisel veri ya da Id yok");
    }

    [Fact]
    public void Ikinci_acilis_hicbir_sey_yapmaz_gunluge_yazmaz()
    {
        ShipmentAmountsBoot.Run(_db, nowUnix: 9000, _log);
        using (var c = _db.Open())
            c.Execute("UPDATE Shipment SET CumulativeAmount = 1 WHERE Id = @id", new { id = _shipmentId });

        ShipmentAmountsBoot.Run(_db, nowUnix: 9500, _log).Should().BeNull();

        Amount().Should().Be(1m, "ikinci açılış yeniden türetmez");
        _log.Entries.Should().ContainSingle("yalnız ilk açılışın satırı");
    }

    [Fact]
    public void Hata_acilisi_durdurmaz_damga_yazilmaz_sonraki_acilista_yeniden_denenir()
    {
        // Damga yazımı (türetmeden SONRAKİ adım) düşer: türetme de geri sarılmalı.
        using (var c = _db.Open())
            c.Execute(@"CREATE TRIGGER TestFailShipmentStamp BEFORE UPDATE OF ShipmentsDerivedAt ON SalesSyncState
                        BEGIN SELECT RAISE(ABORT, 'test'); END");

        var act = () => ShipmentAmountsBoot.Run(_db, nowUnix: 9000, _log);

        act.Should().NotThrow("dosya türetmesi açılışı durdurmaz: canlı yayın her şeyden önce");
        _log.Warnings.Should().ContainSingle().Which.Full.Should().NotContain(_username);
        Amount().Should().Be(400m, "tek işlem: damga yazılamadıysa türetme de geri sarılır");
        new SalesSyncStateRepository(_db).Get().ShipmentsDerivedAt.Should().BeNull();

        using (var c = _db.Open())
            c.Execute("DROP TRIGGER TestFailShipmentStamp");
        ShipmentAmountsBoot.Run(_db, nowUnix: 9100, _log).Should().NotBeNull();
        Amount().Should().Be(250m);
    }

    [Fact]
    public void AppHost_turetmeyi_kilit_temizliginden_ve_musteri_toplamlarindan_sonra_cagirir()
    {
        // Kalmış SyncApplyGuard satırı TR_Shipment_Enqueue'yu susturur: türetme ondan önce koşsaydı değişen dosya
        // hiç gönderilmezdi. Müşteri toplamlarıyla aynı açılış bloğunda, arayüzden önce.
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "OrderDeck.App", "AppHost.cs"));
        var clear = source.IndexOf("SyncApplyScope.ClearStale(", StringComparison.Ordinal);
        var customers = source.IndexOf("CustomerAggregatesBoot.Run(", StringComparison.Ordinal);
        var shipments = source.IndexOf("ShipmentAmountsBoot.Run(", StringComparison.Ordinal);

        clear.Should().BePositive();
        customers.Should().BePositive();
        shipments.Should().BeGreaterThan(clear).And.BeGreaterThan(customers);
        source.IndexOf("ShipmentAmountsBoot.Run(", shipments + 1, StringComparison.Ordinal).Should().Be(-1,
            "tek çağrı yeri");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "OrderDeck.sln")))
            dir = dir.Parent;
        Assert.True(dir is not null, "OrderDeck.sln bulunamadı — depo kökü tespit edilemedi.");
        return dir!.FullName;
    }
}
