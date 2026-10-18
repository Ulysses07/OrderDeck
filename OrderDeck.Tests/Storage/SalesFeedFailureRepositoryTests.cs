using System;
using Dapper;
using FluentAssertions;
using OrderDeck.Core.Sales;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.Tests.Storage;

/// <summary>
/// <see cref="SalesFeedFailureRepository"/> (spec 2026-10-13 §3.2, U10; W2-01): uygulanamayan akış öğesinin kalıcı
/// deneme sayacı — <c>CustomerFeedFailure</c>'ın ikizi; öğe değişince (yeni ChangeSeq) sayaç sıfırdan başlar, tür ve
/// Id birlikte anahtardır.
/// </summary>
public sealed class SalesFeedFailureRepositoryTests : IDisposable
{
    private const long Now = 1_791_000_000L;

    private readonly InMemorySqlite _db = new();
    private readonly SalesFeedFailureRepository _failures;

    public SalesFeedFailureRepositoryTests()
    {
        new MigrationRunner(_db).Run();
        _failures = new SalesFeedFailureRepository(_db);
    }

    public void Dispose() => _db.Dispose();

    private T Scalar<T>(string sql, object? param = null)
    {
        using var c = _db.Open();
        return c.ExecuteScalar<T>(sql, param)!;
    }

    [Fact]
    public void Deneme_sayaci_ayni_ChangeSeqte_artar_yeni_ChangeSeqte_sifirdan_baslar()
    {
        var id = Guid.NewGuid().ToString("N");

        _failures.Record(SalesFeedItemKind.Order, id, 10, "hata", Now).Should().Be(1);
        _failures.Record(SalesFeedItemKind.Order, id, 10, "hata", Now + 1).Should().Be(2);
        _failures.Record(SalesFeedItemKind.Order, id, 11, "yeni hata", Now + 2).Should().Be(1, "öğe değişti");

        Scalar<long>("SELECT FirstFailedAt FROM SalesFeedFailure WHERE ItemId = @id", new { id })
            .Should().Be(Now + 2, "ilk başarısızlık anı da yeni değişikliğinki");
        Scalar<long>("SELECT ChangeSeq FROM SalesFeedFailure WHERE ItemId = @id", new { id }).Should().Be(11);
        _failures.Record(SalesFeedItemKind.Session, id, 10, "hata", Now).Should().Be(1, "aynı Id başka türde ayrı sayılır");
    }

    [Fact]
    public void Atlama_yeniden_kayitla_kalkar_temizlik_ve_hata_metni_siniri()
    {
        var id = Guid.NewGuid().ToString("N");
        _failures.Record(SalesFeedItemKind.Order, id, 10, "hata", Now);
        _failures.Record(SalesFeedItemKind.Session, id, 10, "hata", Now);

        _failures.MarkSkipped(SalesFeedItemKind.Order, id, Now + 3);
        _failures.CountSkipped().Should().Be(1);
        _failures.GetIds().Should().BeEquivalentTo(new[]
        {
            new SalesFeedItemKey(SalesFeedItemKind.Order, id), new SalesFeedItemKey(SalesFeedItemKind.Session, id),
        });
        _failures.Record(SalesFeedItemKind.Order, id, 10, "hata", Now + 4).Should().Be(2);
        _failures.CountSkipped().Should().Be(0, "yeniden kayıt atlanmış işaretini kaldırır");

        _failures.Record(SalesFeedItemKind.Shipment, id, 1, new string('x', 900), Now);
        Scalar<int>("SELECT LENGTH(LastError) FROM SalesFeedFailure WHERE Kind = 'shipment'").Should().Be(500);

        _failures.Clear(SalesFeedItemKind.Order, id);
        _failures.GetIds().Should().NotContain(new SalesFeedItemKey(SalesFeedItemKind.Order, id));
        _failures.ClearAll().Should().Be(2);
        _failures.GetIds().Should().BeEmpty();
    }
}
