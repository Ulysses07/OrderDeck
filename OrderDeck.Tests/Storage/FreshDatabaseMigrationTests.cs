using System;
using System.IO;
using System.Linq;
using Dapper;
using FluentAssertions;
using OrderDeck.Core.Storage;
using Xunit;

namespace OrderDeck.Tests.Storage;

/// <summary>
/// Sıfırdan kurulan bir veritabanında gömülü göç script'lerinin tamamı
/// koşmalı. Geliştirici makinesinde veritabanı zaten göç etmiş olduğu için
/// bu yol yerelde hiç sınanmıyordu; kırık bir göç ancak CI'da ya da sahada
/// yeni kurulumda ortaya çıkıyordu.
/// </summary>
public sealed class FreshDatabaseMigrationTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), $"orderdeck-fresh-{Guid.NewGuid():N}");

    public FreshDatabaseMigrationTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void All_embedded_scripts_apply_to_an_empty_file()
    {
        var factory = new SqliteConnectionFactory(Path.Combine(_dir, "orderdeck.db"));

        new MigrationRunner(factory).Run();

        using var conn = factory.Open();
        var tables = conn.Query<string>(
            "SELECT name FROM sqlite_master WHERE type='table'").ToHashSet();

        tables.Should().Contain(new[] { "Customer", "Label", "Giveaway", "Payment" });
    }
}
