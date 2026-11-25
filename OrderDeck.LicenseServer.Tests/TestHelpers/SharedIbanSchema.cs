using Microsoft.Data.SqlClient;

namespace OrderDeck.LicenseServer.Tests.TestHelpers;

/// <summary>
/// S3b SONRASI şema: dar IBAN tekili (LicenseId, IbanHash) yok, geniş olan (LicenseId, IbanHash, WpfCustomerId) duruyor.
/// Aynı özetin iki müşterideki bağı yalnız burada var olabilir; dar indeks S3b'ye kadar prod'da kalır (banka havalesi
/// spec'i 2026-10-10 §2.5). Çift bağ tohumlayan testler indeksi düşürür.
/// </summary>
internal static class SharedIbanSchema
{
    /// <summary>Dar indeksi düşürür. Fabrikayı BAŞLATMAZ: göçler kurulmuş olmalı (ör. <see cref="PreB1Schema"/>'dan ya da
    /// fabrikanın ilk kullanımından sonra).</summary>
    public static async Task ApplyAsync(string connectionString)
    {
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DROP INDEX [IX_CustomerIbanMemories_LicenseId_IbanHash] ON CustomerIbanMemories";
        await cmd.ExecuteNonQueryAsync();
    }
}
