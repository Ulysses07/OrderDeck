using FluentAssertions;
using Microsoft.Data.SqlClient;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Data;

/// <summary>
/// Fikstürün açtığı test veritabanı prod'un okuma yalıtımında: <c>READ_COMMITTED_SNAPSHOT</c> açık
/// (<see cref="SqlServerContainerFixture.CreateDatabaseAsync"/>; prod'da açık olduğu 27.11.2026'da sorguyla doğrulandı).
/// Kapanırsa ilişkisel yarış testlerinin hepsi sessizce kilitli READ COMMITTED'ı, yani prod'da olmayan bir okumayı sınamaya
/// döner. İkinci test bayrağın yetmediği durumu da kapatır: okuma gerçekten commit olmamış yazımı beklemiyor olmalı.
/// </summary>
[Collection(SqlServerCollection.Name)]
[Trait("Category", "Testcontainers")]
public sealed class TestDatabaseIsolationTests
{
    private readonly SqlServerContainerFixture _sql;

    public TestDatabaseIsolationTests(SqlServerContainerFixture sql) => _sql = sql;

    [Fact]
    public async Task Test_veritabani_RCSI_acik_acilir()
    {
        var cs = await _sql.CreateDatabaseAsync();

        await using var conn = new SqlConnection(cs);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT is_read_committed_snapshot_on FROM sys.databases WHERE database_id = DB_ID()";

        ((bool)(await cmd.ExecuteScalarAsync())!).Should().BeTrue();
    }

    /// <summary>Commit olmamış bir güncelleme varken READ COMMITTED okuma beklemeden son commit'i döner. RCSI kapalı olsaydı
    /// okuma yazarın X kilidinde bekler ve komut süresi dolardı.</summary>
    [Fact]
    public async Task Okuma_commit_olmamis_yazimi_beklemez_son_commiti_okur()
    {
        var cs = await _sql.CreateDatabaseAsync();
        await using var writer = new SqlConnection(cs);
        await writer.OpenAsync();
        await using (var setup = writer.CreateCommand())
        {
            setup.CommandText = "CREATE TABLE dbo.Probe (Id int NOT NULL PRIMARY KEY, V int NOT NULL); INSERT dbo.Probe VALUES (1, 1);";
            await setup.ExecuteNonQueryAsync();
        }
        await using var tx = writer.BeginTransaction();
        await using (var update = writer.CreateCommand())
        {
            update.Transaction = tx;
            update.CommandText = "UPDATE dbo.Probe SET V = 2 WHERE Id = 1";
            await update.ExecuteNonQueryAsync();
        }

        await using var reader = new SqlConnection(cs);
        await reader.OpenAsync();
        await using var read = reader.CreateCommand();
        read.CommandText = "SELECT V FROM dbo.Probe WHERE Id = 1";
        read.CommandTimeout = 10;

        ((int)(await read.ExecuteScalarAsync())!).Should().Be(1, "satır sürümlü okuma commit olmamış 2'yi görmez");
        await tx.RollbackAsync();
    }
}
