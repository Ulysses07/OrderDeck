using System;
using System.Linq;
using Dapper;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using OrderDeck.App;
using OrderDeck.App.Services;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.Tests.Services;

/// <summary>
/// Açılıştaki tek seferlik müşteri toplamı türetmesi (spec 2026-10-13 §6.1, §8.2-7; W1-03): <c>AppHost</c> göçten ve
/// <see cref="SyncApplyScope.ClearStale"/>'den sonra <see cref="CustomerAggregatesBoot.Run"/>'ı çağırır. Günlükte
/// yalnız sayılar — müşteri adı, kullanıcı adı ya da Id yok (KVKK, CLAUDE.md günlük kuralı).
/// </summary>
public sealed class CustomerAggregatesBootTests : IDisposable
{
    private readonly InMemorySqlite _db = new();
    private readonly CapturingLogger<AppHost> _log = new();
    private readonly string _customerId = Guid.NewGuid().ToString("N");
    private readonly string _username = $"alici-{Guid.NewGuid():N}";
    private readonly string _displayName = $"Takma {Guid.NewGuid():N}";

    public CustomerAggregatesBootTests()
    {
        new MigrationRunner(_db).Run();
        new CustomerRepository(_db).Insert(new Customer(_customerId, "tiktok", _username, _displayName, null,
            FirstSeenAt: 100, LastSeenAt: 200, IsBlacklisted: false, BlacklistReason: null, Notes: null,
            TotalLabelsPrinted: 4, TotalAmount: 410m, BlacklistedAt: null, Address: null, Phone: null));
        using var c = _db.Open();
        c.Execute("INSERT INTO StreamSession (Id, StartedAt) VALUES ('s1', 1)");
        c.Execute(@"INSERT INTO Label (Id, SessionId, CustomerId, Platform, Username, MessageText, Price, AddedAt, PrintedAt)
                    VALUES ('l1', 's1', @id, 'tiktok', @username, 'aldım', 110, 1, 2)", new { id = _customerId, username = _username });
    }

    public void Dispose() => _db.Dispose();

    private (int, decimal) Totals()
    {
        var c = new CustomerRepository(_db).GetById(_customerId)!;
        return (c.TotalLabelsPrinted, c.TotalAmount);
    }

    [Fact]
    public void Ilk_acilista_kaymayi_duzeltir_gunlukte_yalniz_sayilar()
    {
        var r = CustomerAggregatesBoot.Run(_db, nowUnix: 9000, _log);

        r.Should().Be(new AggregateRecomputeResult(1, 300m));
        Totals().Should().Be((1, 110m));
        new SalesSyncStateRepository(_db).Get().AggregatesDerivedAt.Should().Be(9000);
        var entry = _log.Entries.Should().ContainSingle().Subject;
        entry.Level.Should().Be(LogLevel.Information);
        entry.Message.Should().Contain("1").And.Contain("300");
        foreach (var personal in new[] { _customerId, _username, _displayName })
            entry.Full.Should().NotContain(personal, "günlükte kişisel veri ya da müşteri Id'si yok");
    }

    [Fact]
    public void Ikinci_acilis_hicbir_sey_yapmaz_gunluge_yazmaz()
    {
        CustomerAggregatesBoot.Run(_db, nowUnix: 9000, _log);
        using (var c = _db.Open())
            c.Execute("UPDATE Customer SET TotalAmount = 1 WHERE Id = @id", new { id = _customerId });

        CustomerAggregatesBoot.Run(_db, nowUnix: 9500, _log).Should().BeNull();

        Totals().Should().Be((1, 1m), "ikinci açılış yeniden hesaplamaz");
        _log.Entries.Should().ContainSingle("yalnız ilk açılışın satırı");
        new SalesSyncStateRepository(_db).Get().AggregatesDerivedAt.Should().Be(9000);
    }

    [Fact]
    public void Hata_acilisi_durdurmaz_damga_yazilmaz_sonraki_acilista_yeniden_denenir()
    {
        // Damga yazımı (hesaptan SONRAKİ adım) düşer: hesap da geri sarılmalı.
        using (var c = _db.Open())
            c.Execute(@"CREATE TRIGGER TestFailDerivedStamp BEFORE UPDATE OF AggregatesDerivedAt ON SalesSyncState
                        BEGIN SELECT RAISE(ABORT, 'test'); END");

        var act = () => CustomerAggregatesBoot.Run(_db, nowUnix: 9000, _log);

        act.Should().NotThrow("toplam türetmesi açılışı durdurmaz: canlı yayın her şeyden önce");
        _log.Warnings.Should().ContainSingle().Which.Full.Should().NotContain(_username);
        Totals().Should().Be((4, 410m), "tek işlem: damga yazılamadıysa hesap da geri sarılır");
        new SalesSyncStateRepository(_db).Get().AggregatesDerivedAt.Should().BeNull();

        using (var c = _db.Open())
            c.Execute("DROP TRIGGER TestFailDerivedStamp");
        CustomerAggregatesBoot.Run(_db, nowUnix: 9100, _log).Should().NotBeNull();
        new SalesSyncStateRepository(_db).Get().AggregatesDerivedAt.Should().Be(9100);
    }
}
