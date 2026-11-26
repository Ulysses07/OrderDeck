using Microsoft.Data.SqlClient;

namespace OrderDeck.LicenseServer.Tests.TestHelpers;

/// <summary>
/// Test veritabanında RCSI (<c>READ_COMMITTED_SNAPSHOT</c>). Fikstür veritabanını düz <c>CREATE DATABASE</c> ile açar
/// (<see cref="SqlServerContainerFixture.CreateDatabaseAsync"/>): RCSI kapalıdır, okumalar kilitli READ COMMITTED'dır. Prod
/// veritabanı ise ilk kurulumda <c>EnsureCreated()</c> ile oluşturuldu (<c>deploy/bootstrap-migration-history.sql</c>); EF
/// Core SQL Server'da veritabanını oluştururken RCSI'yı açar (Azure SQL dışında). Satır sürümlü okumada değişen bir
/// davranışı sınayan test, fikstürün veritabanında bunu açar.
/// </summary>
public static class ReadCommittedSnapshot
{
    /// <summary>
    /// RCSI'yı açar. Veritabanında bu bağlantıdan başka oturum varken ALTER bekler: fikstürün açtığı veritabanında,
    /// fabrika (göç) ve başka bağlantı açılmadan önce çağrılır.
    /// </summary>
    public static async Task EnableAsync(string connectionString)
    {
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "ALTER DATABASE CURRENT SET READ_COMMITTED_SNAPSHOT ON";
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Bağlantının veritabanında RCSI açık mı (<c>sys.databases.is_read_committed_snapshot_on</c>).</summary>
    public static async Task<bool> IsOnAsync(string connectionString)
    {
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT is_read_committed_snapshot_on FROM sys.databases WHERE database_id = DB_ID()";
        return (bool)(await cmd.ExecuteScalarAsync())!;
    }
}
