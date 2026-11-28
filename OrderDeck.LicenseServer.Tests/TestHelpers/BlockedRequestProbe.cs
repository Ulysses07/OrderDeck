using Microsoft.Data.SqlClient;

namespace OrderDeck.LicenseServer.Tests.TestHelpers;

/// <summary>
/// Bir isteğin başka bir oturumun kilidinde beklediğini SQL Server'ın kendisinden okur (<c>sys.dm_exec_requests</c>,
/// <c>blocking_session_id</c>).
///
/// <para>Neden var: RCSI yarış testlerinde kesen işlem açık tutulur, rakip iş onun kilidinde bekleyene ya da bitene dek
/// commit edilmez. Sabit bir süre beklemek yük altında kırılır (<see cref="AppLockWaiterProbe"/>'un gerekçesi): geç başlayan
/// rakip commit'ten sonra koşup kesmeyi anlık görüntüden görür ve test boşuna geçer. Bekleme tek başına kanıt değildir:
/// yabancı anahtar denetimi RCSI'da da kilitli okur, kararını bayat okumayla vermiş bir yazım da sonunda bekleyebilir. Test
/// sonucu da sınamalı.</para>
///
/// <para>Her test kendi veritabanını açtığı için (<see cref="SqlServerContainerFixture.CreateDatabaseAsync"/>) sorgu
/// <c>DB_ID()</c> ile o veritabanına daraltılır.</para>
/// </summary>
public static class BlockedRequestProbe
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(25);

    /// <summary>
    /// <paramref name="work"/> bitmeden bu veritabanında <paramref name="blockerSession"/> oturumunun kilidinde bekleyen bir
    /// istek görülürse true; iş beklemeden biterse ya da <paramref name="max"/> dolarsa false.
    /// </summary>
    public static async Task<bool> WaitsOnAsync(string connectionString, int blockerSession, Task work, TimeSpan max)
    {
        var deadline = DateTimeOffset.UtcNow + max;
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*) FROM sys.dm_exec_requests
            WHERE blocking_session_id = @blocker AND database_id = DB_ID()
            """;
        cmd.Parameters.AddWithValue("@blocker", blockerSession);
        while (!work.IsCompleted && DateTimeOffset.UtcNow < deadline)
        {
            if ((int)(await cmd.ExecuteScalarAsync())! > 0) return true;
            await Task.Delay(PollInterval);
        }
        return false;
    }
}
