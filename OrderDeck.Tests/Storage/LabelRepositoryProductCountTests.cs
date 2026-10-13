using System.Linq;
using FluentAssertions;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Sales;
using OrderDeck.Core.Sessions;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.Tests.Storage;

/// <summary>
/// Hero'daki "BU ÜRÜNDEN N sipariş" sayacı. GetSessionTotals ile AYNI dışlama
/// kurallarını uygulamalı: iptal edilmiş satır ve onaylanmamış yedek satılmış
/// sayılmaz — yoksa iki sayaç birbirini tutmaz ve operatör hangisine
/// inanacağını bilemez.
/// </summary>
public class LabelRepositoryProductCountTests
{
    private static (InMemorySqlite Db, LabelRepository Repo) Fx()
    {
        var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        new SessionRepository(db).Insert(
            new StreamSession("s1", null, 100, null, new[] { "instagram" }, null));
        new CustomerRepository(db).Insert(
            new Customer("c1", "instagram", "@a", null, null, 100, 100,
                false, null, null, 0, 0m, BlacklistedAt: null, Address: null, Phone: null));
        return (db, new LabelRepository(db));
    }

    private static Label Row(string id, string? code, long? printedAt = 200,
                             long? cancelledAt = null, bool tentative = false) =>
        new(id, "s1", "c1", "instagram", "@a", "mesaj", code, 100m, 150,
            printedAt, cancelledAt, null, false, null, tentative);

    [Fact]
    public void Counts_only_rows_with_matching_code()
    {
        var (db, repo) = Fx();
        using var _ = db;

        repo.Insert(Row("l1", "A12"));
        repo.Insert(Row("l2", "A12"));
        repo.Insert(Row("l3", "B7"));

        repo.CountSessionLabelsByCode("s1", "A12").Should().Be(2);
    }

    [Fact]
    public void Ignores_cancelled_and_tentative_backup_rows()
    {
        var (db, repo) = Fx();
        using var _ = db;

        repo.Insert(Row("l1", "A12"));
        repo.Insert(Row("l2", "A12"));
        repo.Insert(Row("l3", "A12", tentative: true));

        // Insert CancelledAt via MarkCancelled — normal iş akışı (Insert kasıtlı
        // olarak CancelledAt içermiyor; iptal sonradan oluyor).
        repo.MarkCancelled(new[] { "l2" }, 300, "test");

        repo.CountSessionLabelsByCode("s1", "A12").Should().Be(1);
    }

    [Fact]
    public void Counts_queued_rows_too_not_just_printed()
    {
        var (db, repo) = Fx();
        using var _ = db;

        // Hero sayacı "sipariş" diyor, "yazdırıldı" demiyor: operatör kuyruğa
        // düşen siparişi anında görmeli, yazdırmayı beklememeli.
        repo.Insert(Row("l1", "A12", printedAt: null));

        repo.CountSessionLabelsByCode("s1", "A12").Should().Be(1);
    }

    [Fact]
    public void Code_match_is_case_insensitive()
    {
        var (db, repo) = Fx();
        using var _ = db;

        repo.Insert(Row("l1", "A12"));

        repo.CountSessionLabelsByCode("s1", "a12").Should().Be(1);
    }

    [Fact]
    public void Turkish_letters_match_case_insensitively_like_the_price_box()
    {
        // NOCASE yalnız ASCII katlıyordu: kabuk "çanta1" ile "Çanta1"i aynı kod sayarken sayaç ve FİYAT kutusunun
        // kaynağı (GetActiveBySessionAndCode) ayrı sayıyordu.
        var (db, repo) = Fx();
        using var _ = db;

        repo.Insert(Row("l1", "çanta1"));
        repo.Insert(Row("l2", "ÇANTA1"));
        repo.Insert(Row("l3", "şal"));
        repo.Insert(Row("l4", "Çanta2"));

        repo.CountSessionLabelsByCode("s1", "Çanta1").Should().Be(2);
        repo.GetActiveBySessionAndCode("s1", "Çanta1").Select(l => l.Id).Should().Equal("l1", "l2");
        repo.CountSessionLabelsByCode("s1", "ŞAL").Should().Be(1);
        repo.CountSessionLabelsByCode("s1", "çanta").Should().Be(0, "ön ek aynı kod değil");
    }

    [Fact]
    public void Returns_zero_for_unknown_code()
    {
        var (db, repo) = Fx();
        using var _ = db;

        repo.CountSessionLabelsByCode("s1", "ZZZ").Should().Be(0);
    }
}
