using System;
using FluentAssertions;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.Tests.Storage;

/// <summary>
/// N02 (2026-09-10 denetimi): kalıcı ödeme-işi kimliği deposunun SQLite
/// gerçeklemesi. Davranış sözleşmesi PaymentRequestServiceTests'te bellek içi
/// fake ile sınanıyor; burada asıl doğrulanan diskteki temsil — özellikle
/// ondalık tutarın TEXT round-trip'i (REAL'e çevrilseydi eşitlik
/// karşılaştırması, yani "aynı satış mı?" sorusu bozulurdu).
/// </summary>
public class PendingBalanceApplyRepositoryTests
{
    private static (InMemorySqlite Db, PendingBalanceApplyRepository Repo) Fx()
    {
        var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        return (db, new PendingBalanceApplyRepository(db));
    }

    [Fact]
    public void Create_sonrasi_GetUnresolved_tum_alanlari_geri_verir()
    {
        var (db, repo) = Fx();
        using var _d = db;
        var key = Guid.NewGuid();

        repo.Create("c1", key, 1234.56m);

        var found = repo.GetUnresolved("c1");
        found.Should().NotBeNull();
        found!.IdempotencyKey.Should().Be(key);
        found.CustomerId.Should().Be("c1");
        found.ProductTotal.Should().Be(1234.56m, "tutar TEXT olarak birebir round-trip etmeli");
    }

    [Fact]
    public void MarkResolved_kaydi_gizler()
    {
        var (db, repo) = Fx();
        using var _d = db;
        var key = Guid.NewGuid();
        repo.Create("c1", key, 100m);

        repo.MarkResolved(key);

        repo.GetUnresolved("c1").Should().BeNull();
    }

    [Fact]
    public void GetUnresolved_baska_musterinin_isini_gormez()
    {
        var (db, repo) = Fx();
        using var _d = db;
        repo.Create("c1", Guid.NewGuid(), 100m);

        repo.GetUnresolved("c2").Should().BeNull();
    }

    [Fact]
    public void Ayni_anahtar_ikinci_kez_yazilamaz()
    {
        // PK = anahtar: aynı anahtarla ikinci Create bir programlama hatasıdır
        // ve sessizce üstüne yazmak yerine patlamalı.
        var (db, repo) = Fx();
        using var _d = db;
        var key = Guid.NewGuid();
        repo.Create("c1", key, 100m);

        var act = () => repo.Create("c1", key, 200m);

        act.Should().Throw<Exception>();
    }
}
