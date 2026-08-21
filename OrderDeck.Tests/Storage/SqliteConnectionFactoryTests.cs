using System;
using System.IO;
using Dapper;
using FluentAssertions;
using OrderDeck.Core.Storage;
using Xunit;

namespace OrderDeck.Tests.Storage;

/// <summary>
/// WAL kipi gerçek dosya üzerinde sınanmalı: bellek-içi veritabanlarında journal
/// kipi değiştirilemez, PRAGMA "memory" döner ve test hiçbir şey kanıtlamaz.
/// </summary>
public sealed class SqliteConnectionFactoryTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), $"orderdeck-wal-{Guid.NewGuid():N}");

    private string DbPath => Path.Combine(_dir, "orderdeck.db");

    public SqliteConnectionFactoryTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Open_puts_the_database_in_WAL_mode()
    {
        var factory = new SqliteConnectionFactory(DbPath);

        using var conn = factory.Open();

        conn.ExecuteScalar<string>("PRAGMA journal_mode;")
            .Should().Be("wal");
    }

    [Fact]
    public void WAL_mode_persists_for_later_connections()
    {
        var factory = new SqliteConnectionFactory(DbPath);
        using (var first = factory.Open())
            first.Execute("CREATE TABLE T (Id INTEGER PRIMARY KEY);");

        // Kip dosya başlığında saklanıyor, yani yeni bir fabrika (yeni süreç
        // gibi) PRAGMA'yı hiç çalıştırmasa bile WAL bulmalı.
        using var conn = new SqliteConnectionFactory(DbPath).Open();

        conn.ExecuteScalar<string>("PRAGMA journal_mode;").Should().Be("wal");
    }

    [Fact]
    public void Synchronous_stays_FULL()
    {
        // WAL ile yaygın öneri NORMAL'dir ve daha hızlıdır, ama elektrik
        // kesintisinde son işlemleri kaybettirebilir. Mezat modelinde son işlem
        // çoğu zaman yeni bir sipariş; hızı bunun için takas etmiyoruz.
        using var conn = new SqliteConnectionFactory(DbPath).Open();

        conn.ExecuteScalar<int>("PRAGMA synchronous;").Should().Be(2);
    }
}
