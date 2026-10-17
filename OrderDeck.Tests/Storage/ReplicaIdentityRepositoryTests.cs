using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Tests.TestHelpers;

namespace OrderDeck.Tests.Storage;

/// <summary>
/// Kopya kimliği (spec 2026-10-13 §2, §3.2, §8.7): <c>_meta.ReplicaId</c> veritabanı dosyasına bağlı, depo
/// örnekleri ve yeniden açılış arasında aynı; eksik/bozuk değer <c>null</c>; bekleyen önceki kimlik
/// karşılaştır-ve-sil ile silinir; döndürme yeni kimlik üretir ve öncekini bekleyen yapar.
/// </summary>
public sealed class ReplicaIdentityRepositoryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"orderdeck-replica-{Guid.NewGuid():N}");
    private readonly string _dbPath;

    public ReplicaIdentityRepositoryTests()
    {
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "orderdeck.db");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* en iyi çaba */ }
    }

    private SqliteConnectionFactory Boot()
    {
        var factory = new SqliteConnectionFactory(_dbPath);
        new MigrationRunner(factory).Run();
        return factory;
    }

    private static string? Raw(IDbConnectionFactory f, string column)
    {
        using var c = f.Open();
        return c.ExecuteScalar<string?>($"SELECT {column} FROM _meta WHERE Id = 1");
    }

    private static void SetRaw(IDbConnectionFactory f, string column, string? value)
    {
        using var c = f.Open();
        c.Execute($"UPDATE _meta SET {column} = @value WHERE Id = 1", new { value });
    }

    [Fact]
    public void Kimlik_depo_orneklerinde_ve_yeniden_acilista_ayni_kalir()
    {
        var factory = Boot();
        var first = new ReplicaIdentityRepository(factory).GetReplicaId();

        first.Should().NotBeNull();
        first!.Value.ToString("N").Should().Be(Raw(factory, "ReplicaId"), "049 tohumu Guid 'N' biçimindedir");
        new ReplicaIdentityRepository(factory).GetReplicaId().Should().Be(first);

        // Yeniden başlatma: havuz boşalır, yeni fabrika, açılış göçü yeniden koşar (049 bir daha koşmaz).
        SqliteConnection.ClearAllPools();
        var reopened = Boot();
        new ReplicaIdentityRepository(reopened).GetReplicaId().Should().Be(first);
        new ReplicaIdentityRepository(reopened).HasIdentityColumns().Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("bozuk-kimlik")]
    [InlineData("00000000000000000000000000000000")]
    public void Eksik_bozuk_ya_da_bos_kimlik_null_doner(string? raw)
    {
        var factory = Boot();
        SetRaw(factory, "ReplicaId", raw);
        SetRaw(factory, "PreviousReplicaId", raw);

        var repo = new ReplicaIdentityRepository(factory);
        repo.GetReplicaId().Should().BeNull();
        repo.GetPendingPreviousReplicaId().Should().BeNull();
    }

    [Fact]
    public void Bekleyen_onceki_kimlik_yalniz_gonderilen_degerse_silinir()
    {
        var factory = Boot();
        var repo = new ReplicaIdentityRepository(factory);
        repo.GetPendingPreviousReplicaId().Should().BeNull("taze veritabanında bekleyen önceki kimlik yok");

        var previous = Guid.NewGuid();
        repo.SetPendingPreviousReplicaId(previous);
        repo.GetPendingPreviousReplicaId().Should().Be(previous);
        Raw(factory, "PreviousReplicaId").Should().Be(previous.ToString("N"));

        repo.ClearPendingPreviousReplicaId(Guid.NewGuid()).Should().BeFalse("hello uçarken yazılan başka değer kalır");
        repo.GetPendingPreviousReplicaId().Should().Be(previous);

        repo.ClearPendingPreviousReplicaId(previous).Should().BeTrue();
        repo.GetPendingPreviousReplicaId().Should().BeNull();

        // Biçimden bağımsız: elle "D" biçiminde (büyük harf) yazılmış değer de eşleşir.
        SetRaw(factory, "PreviousReplicaId", previous.ToString("D").ToUpperInvariant());
        repo.GetPendingPreviousReplicaId().Should().Be(previous);
        repo.ClearPendingPreviousReplicaId(previous).Should().BeTrue();
        Raw(factory, "PreviousReplicaId").Should().BeNull();
    }

    [Fact]
    public void Dondurme_yeni_kimlik_uretir_ve_oncekini_bekleyen_yapar()
    {
        var factory = Boot();
        var repo = new ReplicaIdentityRepository(factory);
        var original = repo.GetReplicaId()!.Value;

        var rotated = repo.Rotate();
        rotated.Should().NotBe(original).And.NotBe(Guid.Empty);
        repo.GetReplicaId().Should().Be(rotated);
        repo.GetPendingPreviousReplicaId().Should().Be(original);
        Raw(factory, "ReplicaId").Should().Be(rotated.ToString("N"));

        var explicitPrevious = Guid.NewGuid();
        var again = repo.Rotate(explicitPrevious);
        again.Should().NotBe(rotated);
        repo.GetPendingPreviousReplicaId().Should().Be(explicitPrevious);

        // Önceki kimlik bilinmiyorsa bekleyen değer korunur.
        var third = repo.Rotate(null);
        repo.GetReplicaId().Should().Be(third);
        repo.GetPendingPreviousReplicaId().Should().Be(explicitPrevious);
    }

    [Fact]
    public void Sema_48de_kimlik_kolonlari_yok()
    {
        var factory = new SqliteConnectionFactory(_dbPath);
        new MigrationRunner(factory, EmbeddedMigrationScripts.UpTo(48)).Run();

        new ReplicaIdentityRepository(factory).HasIdentityColumns().Should().BeFalse();
    }
}
