using System;
using FluentAssertions;
using OrderDeck.App.Services.Sync;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.Tests.Services.Sync;

/// <summary>Faz 0 (D1): sayaç müşteri imlecini lisansa ve biçim-2 imleç adına göre çözer (U15).</summary>
public sealed class SyncPendingCounterTests : IDisposable
{
    private sealed class FakeLicenseProvider : ICurrentLicenseProvider
    {
        public string? CurrentLicenseKey { get; set; }
    }

    private readonly InMemorySqlite _db = new();
    private readonly CustomerRepository _customers;
    private readonly SyncCursorRepository _cursors;
    private readonly FakeLicenseProvider _license = new() { CurrentLicenseKey = $"lisans-{Guid.NewGuid():N}" };
    private readonly SyncPendingCounter _counter;

    public SyncPendingCounterTests()
    {
        new MigrationRunner(_db).Run();
        _customers = new CustomerRepository(_db);
        _cursors = new SyncCursorRepository(_db);
        _counter = new SyncPendingCounter(new SyncOutboxRepository(_db), _cursors, _license);
    }

    public void Dispose() => _db.Dispose();

    private string Local(string username)
    {
        var id = Guid.NewGuid().ToString("N");
        _customers.Insert(new Customer(id, "tiktok", username, "Örnek Müşteri", null, 1, 1, false, null, null, 0, 0m, null, null, null));
        return id;
    }

    [Fact]
    public void Bicim_2_imleci_okunur_onceki_surumun_imleci_degil()
    {
        var id = Local("ornek_musteri_1");
        var seq = _customers.GetById(id)!.SyncSeq;

        // Önceki sürümün (biçim 1) imleci bu satırı geçmiş — biçim 2 ile henüz gönderilmedi.
        _cursors.Upsert("customer-projection-out", _license.CurrentLicenseKey!, seq: seq);
        _counter.Count().Should().Be(1, "satır biçim 2 ile gönderilene dek bekleyen sayılır (U3, U15)");

        _cursors.Upsert(WpfCustomerProjectionSyncService.CursorName, _license.CurrentLicenseKey!, seq: seq);
        _counter.Count().Should().Be(0);
    }

    [Fact]
    public void Imlec_lisansa_baglidir_lisans_yoksa_hepsi_bekler()
    {
        var id = Local("ornek_musteri_1");
        _cursors.Upsert(WpfCustomerProjectionSyncService.CursorName, $"lisans-{Guid.NewGuid():N}",
            seq: _customers.GetById(id)!.SyncSeq);

        _counter.Count().Should().Be(1, "başka lisansın imleci bu lisansın gönderimini anlatmaz");

        _license.CurrentLicenseKey = null;
        _counter.Count().Should().Be(1);
    }

    [Fact]
    public void Dikkat_sayaci_depodan_gecer()
        => _counter.Attention().Should().Be(default(SyncAttention));
}
