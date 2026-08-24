using Microsoft.EntityFrameworkCore;

namespace OrderDeck.LicenseServer.Tests.TestHelpers;

/// <summary>
/// <see cref="ApiFactory"/>'nin gerçek SQL Server'a bağlanan hâli. Bağlantı
/// dizesi <see cref="SqlServerContainerFixture"/>'dan gelir.
///
/// Yalnız InMemory'nin taklit edemediği şeyler için kullanılır: eşzamanlı
/// yazma yarışları, <c>ExecuteUpdate/Delete</c>, sağlayıcıya özgü SQL. Geri
/// kalan her test InMemory'de kalmalı — bu fabrika container başlangıcı
/// yüzünden kat kat yavaş.
/// </summary>
public class RelationalApiFactory : ApiFactory
{
    private readonly string _connectionString;

    public RelationalApiFactory(string connectionString)
        => _connectionString = connectionString;

    protected override void ConfigureDatabase(DbContextOptionsBuilder opt)
        => opt.UseSqlServer(_connectionString);
}
