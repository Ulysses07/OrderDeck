using System;
using FluentAssertions;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.Tests.Storage;

/// <summary>R6-04: imleçler veriyle aynı SQLite dosyasında (göç 038).
/// Bu küme yalnız saklama sözleşmesini sabitler; tohumlama/temizlik
/// davranışı servis testlerinde.</summary>
public sealed class SyncCursorRepositoryTests : IDisposable
{
    private readonly InMemorySqlite _db;
    private readonly SyncCursorRepository _repo;

    public SyncCursorRepositoryTests()
    {
        _db = new InMemorySqlite();
        new MigrationRunner(_db).Run();
        _repo = new SyncCursorRepository(_db);
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public void Get_satir_yoksa_null()
    {
        _repo.Get("customer-projection-out", "KEY-A").Should().BeNull();
    }

    [Fact]
    public void Upsert_seq_imleci_gidip_gelir()
    {
        _repo.Upsert("customer-projection-out", "KEY-A", seq: 42);

        var row = _repo.Get("customer-projection-out", "KEY-A");
        row.Should().NotBeNull();
        row!.Seq.Should().Be(42);
        row.UpdatedAt.Should().BeNull();
        row.LastId.Should().BeNull();
    }

    [Fact]
    public void Upsert_zaman_id_imleci_hassasiyet_kaybetmeden_gidip_gelir()
    {
        // Milisaniye hassasiyeti kritik: yuvarlama, aynı saniyeyi paylaşan
        // satırlarla sayfa dolduğunda imleci başa döndürüyordu (R3-01 dersi).
        var at = new DateTimeOffset(2026, 9, 14, 10, 30, 15, 750, TimeSpan.Zero);
        var id = Guid.NewGuid();

        _repo.Upsert("shopper-ingest-in", "KEY-A", updatedAt: at, lastId: id);

        var row = _repo.Get("shopper-ingest-in", "KEY-A");
        row.Should().NotBeNull();
        row!.UpdatedAt.Should().Be(at);
        row.LastId.Should().Be(id);
        row.Seq.Should().BeNull();
    }

    [Fact]
    public void Upsert_ayni_anahtara_ikinci_yazma_gunceller()
    {
        _repo.Upsert("customer-projection-out", "KEY-A", seq: 1);
        _repo.Upsert("customer-projection-out", "KEY-A", seq: 2);

        _repo.Get("customer-projection-out", "KEY-A")!.Seq.Should().Be(2);
    }

    [Fact]
    public void Imlecler_ad_ve_lisansla_yalitilir()
    {
        // R6-04 hedef ekseni: lisans A'nın imleci B'ninkine dokunamaz,
        // aynı lisansın iki ailesi de birbirine dokunamaz.
        _repo.Upsert("customer-projection-out", "KEY-A", seq: 10);
        _repo.Upsert("customer-projection-out", "KEY-B", seq: 20);
        _repo.Upsert("shopper-ingest-in", "KEY-A", lastId: Guid.NewGuid());

        _repo.Get("customer-projection-out", "KEY-A")!.Seq.Should().Be(10);
        _repo.Get("customer-projection-out", "KEY-B")!.Seq.Should().Be(20);
        _repo.Get("shopper-ingest-in", "KEY-B").Should().BeNull();
    }
}
