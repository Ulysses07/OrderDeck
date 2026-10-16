using Microsoft.Data.SqlClient;

namespace OrderDeck.LicenseServer.Tests.TestHelpers;

/// <summary>
/// Bir isteğin uygulama kilidinde (<c>sp_getapplock</c>) beklediğini SQL Server'ın kendisinden okur
/// (<c>sys.dm_tran_locks</c>, <c>request_status = 'WAIT'</c>).
///
/// <para>Neden var: yarış testlerinin kancası rakip isteği başlatıp onun kilitte beklemesini ister. Bunu sabit bir
/// süreyle varsaymak tam paketin yükü altında kırılır: ilk isteğin commit'i gecikirse rakip kilit bekleme sınırını
/// (<c>SalesCommandLock.DefaultTimeout</c>) aşar ve testin sınadığı yol yerine zaman aşımı yolu koşar (2026-10-17,
/// <c>PaymentRequestBeginRelationalTests</c>). Bekleyen kilit görüldüğü an kanca ilk isteği bırakır; rakibin kilitte
/// geçirdiği süre yalnız ilk isteğin commit süresi olur.</para>
///
/// <para>Her test kendi veritabanını açtığı için (<see cref="SqlServerContainerFixture.CreateDatabaseAsync"/>) sorgu
/// <c>DB_ID()</c> ile o veritabanına daraltılır: başka testin bekleyen kilidi sayılmaz.</para>
/// </summary>
public static class AppLockWaiterProbe
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(25);

    /// <summary>
    /// Bu veritabanında bekleyen bir uygulama kilidi isteği görülene ya da <paramref name="max"/> dolana kadar bekler.
    /// </summary>
    /// <returns>Bekleyen kilit görüldüyse true; süre dolduysa false.</returns>
    public static async Task<bool> WaitForWaiterAsync(string connectionString, TimeSpan max, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + max;
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*) FROM sys.dm_tran_locks
            WHERE resource_type = 'APPLICATION' AND request_status = 'WAIT' AND resource_database_id = DB_ID()
            """;
        while (true)
        {
            if ((int)(await cmd.ExecuteScalarAsync(ct))! > 0) return true;
            if (DateTimeOffset.UtcNow >= deadline) return false;
            await Task.Delay(PollInterval, ct);
        }
    }
}
