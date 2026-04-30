using System.Data;
using OrderDeck.Core.Storage;
using Microsoft.Data.Sqlite;

namespace OrderDeck.Tests.TestHelpers;

/// <summary>
/// Shared in-memory SQLite. Each instance owns one connection that stays open for the
/// life of the test (in-memory DBs disappear when the last connection closes).
/// </summary>
public sealed class InMemorySqlite : IDbConnectionFactory, System.IDisposable
{
    private readonly SqliteConnection _keepAlive;
    private readonly string _connectionString;

    public InMemorySqlite()
    {
        var name = $"orderdeck-test-{System.Guid.NewGuid():N}";
        _connectionString = $"Data Source={name};Mode=Memory;Cache=Shared;Foreign Keys=true";
        _keepAlive = new SqliteConnection(_connectionString);
        _keepAlive.Open();
    }

    public IDbConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    public void Dispose() => _keepAlive.Dispose();
}
