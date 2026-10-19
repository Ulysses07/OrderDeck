using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.TestHelpers;

/// <summary>
/// Testler için tek bir SQL Server container'ı ayağa kaldırır.
///
/// Neden var: suite'in geri kalanı InMemory üzerinde koşuyor ve bu bilinçli
/// bir tercih — hızlı, Docker istemiyor. Ama InMemory'de <b>eşzamanlılık yok</b>:
/// aynı satıra iki eşzamanlı yazma yarışmıyor, <c>ExecuteUpdate/Delete</c> hiç
/// çalışmıyor. Bu ikisine dayanan düzeltmeler InMemory'de "test edilirse" CI
/// yeşil yanar ama düzeltmenin çalıştığına dair hiçbir kanıt üretmez. Bu
/// fixture o boşluğu kapatır.
///
/// Container koleksiyon başına <b>bir kez</b> başlar (başlangıç ~30-60 sn);
/// izolasyon container değil <b>veritabanı</b> düzeyinde sağlanır:
/// <see cref="CreateDatabaseAsync"/> her test için ayrı bir DB açar.
///
/// Motor bilerek SQL Server: prod bugün bu. Postgres göçünde bu dosyada
/// <c>MsSqlBuilder</c> yerine <c>PostgreSqlBuilder</c> kullanılacak ve testler
/// göçün güvenlik ağı olacak.
/// </summary>
public class SqlServerContainerFixture : IAsyncLifetime
{
    // İmaj sabitleniyor: parametresiz kurucu artık kullanımdan kalktı ve
    // "latest" etiketi CI'ı bizden habersiz kırabilir.
    private const string Image = "mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04";

    private readonly MsSqlContainer _container;

    public SqlServerContainerFixture() : this(builder => builder) { }

    /// <summary>Aynı imajın başka bir yapılandırması için (bkz.
    /// <see cref="SqlServerExpressContainerFixture"/>).</summary>
    protected SqlServerContainerFixture(Func<MsSqlBuilder, MsSqlBuilder> configure) =>
        _container = configure(new MsSqlBuilder(Image)).Build();

    public async Task InitializeAsync() => await _container.StartAsync();

    public async Task DisposeAsync() => await _container.DisposeAsync();

    /// <summary>
    /// Test'e özel boş bir veritabanı açar ve ona bağlanan connection string'i
    /// döner. Testler arası izolasyon buradan gelir — aynı container'ı paylaşan
    /// iki test birbirinin satırlarını görmez.
    /// </summary>
    public async Task<string> CreateDatabaseAsync()
    {
        var dbName = "t" + Guid.NewGuid().ToString("N");

        await using (var conn = new SqlConnection(_container.GetConnectionString()))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            // dbName tamamen bizim ürettiğimiz sabit alfabe (harf + hex), dışarıdan
            // veri almıyor; yine de köşeli parantezle sarılıyor.
            cmd.CommandText = $"CREATE DATABASE [{dbName}]";
            await cmd.ExecuteNonQueryAsync();
        }

        return new SqlConnectionStringBuilder(_container.GetConnectionString())
        {
            InitialCatalog = dbName,
        }.ConnectionString;
    }
}

/// <summary>
/// Prod'un sürümü SQL Server Express: ONLINE indeks işlemi yok, tampon havuzu
/// sınırlı. Yalnız prod sürümüne özgü bir davranışı kanıtlayan testler kullanır
/// (göçlerin Express'te koşması: satış senkronu S1, spec 2026-10-13 §10.1-12;
/// ortak bakiye S1, spec 2026-10-11 §8.2-11). Ayrı bir container olduğu için
/// genel koleksiyona girmez; kendi koleksiyonunda
/// (<see cref="SqlServerExpressCollection"/>) bir kez başlar. Sınıf başına
/// <c>IClassFixture</c> her sınıfa ayrı bir Express container'ı açardı.
/// </summary>
public sealed class SqlServerExpressContainerFixture : SqlServerContainerFixture
{
    public SqlServerExpressContainerFixture()
        : base(builder => builder.WithEnvironment("MSSQL_PID", "Express")) { }
}

/// <summary>
/// Container'ı paylaşan testler bu koleksiyona girer; böylece container test
/// sınıfı başına değil, koleksiyon başına bir kez ayağa kalkar.
/// </summary>
[CollectionDefinition(Name)]
public sealed class SqlServerCollection : ICollectionFixture<SqlServerContainerFixture>
{
    public const string Name = "sqlserver";
}

/// <summary>
/// Express container'ını paylaşan testler (<see cref="SqlServerExpressContainerFixture"/>):
/// container koleksiyon başına bir kez kalkar, izolasyon yine veritabanı düzeyinde.
/// </summary>
[CollectionDefinition(Name)]
public sealed class SqlServerExpressCollection : ICollectionFixture<SqlServerExpressContainerFixture>
{
    public const string Name = "sqlserver-express";
}
