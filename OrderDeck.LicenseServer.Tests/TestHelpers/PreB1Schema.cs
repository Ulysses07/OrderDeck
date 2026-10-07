using Microsoft.Data.SqlClient;
using OrderDeck.LicenseServer.Services.CustomerSync;

namespace OrderDeck.LicenseServer.Tests.TestHelpers;

/// <summary>
/// B1 ÖNCESİ şema: kişi başına tek asıl kayıt indeksi
/// (<see cref="CustomerIdentityIndex"/>) yok. Tek seferlik birleştirme (E2,
/// <c>merge-customer-identities</c>) prod'da bu şemada koşar — aynı kişinin
/// birden çok asıl kaydı yalnız burada var olabilir. Göç zinciri test
/// veritabanına B1'i de kurduğu için, kopyalı asıl kayıt tohumlayan
/// (birleştirme işi, CLI, birleştirici) testler indeksi düşürür. İndeksli
/// davranış (onarımın çakışma politikası, yarışlar) gerçek indeksle ayrı sınanır.
/// </summary>
internal static class PreB1Schema
{
    /// <summary>Fabrikanın şemayı kurmasını (açılıştaki Migrate) bekler, sonra
    /// indeksi düşürür.</summary>
    public static async Task ApplyAsync(RelationalApiFactory factory, string connectionString)
    {
        _ = factory.Services; // host'u (ve göçleri) şimdi kur
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"DROP INDEX [{CustomerIdentityIndex.Name}] ON WpfCustomerProjections";
        await cmd.ExecuteNonQueryAsync();
    }
}
