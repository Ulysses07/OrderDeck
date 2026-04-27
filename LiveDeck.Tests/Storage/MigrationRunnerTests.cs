using Dapper;
using FluentAssertions;
using LiveDeck.Core.Storage;
using LiveDeck.Tests.TestHelpers;
using Xunit;

namespace LiveDeck.Tests.Storage;

public class MigrationRunnerTests
{
    [Fact]
    public void Run_creates_all_six_tables_and_meta()
    {
        using var db = new InMemorySqlite();
        var runner = new MigrationRunner(db);

        runner.Run();

        using var conn = db.Open();
        var tables = conn.Query<string>(
            "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name").AsList();

        tables.Should().Contain(new[]
        {
            "ActiveCode", "Customer", "Giveaway", "GiveawayParticipant",
            "OrderItem", "Settings", "StreamSession", "_meta"
        });

        var version = conn.ExecuteScalar<int>("SELECT SchemaVersion FROM _meta WHERE Id = 1");
        version.Should().Be(1);
    }

    [Fact]
    public void Run_is_idempotent()
    {
        using var db = new InMemorySqlite();
        var runner = new MigrationRunner(db);

        runner.Run();
        runner.Run();   // second call must not throw

        using var conn = db.Open();
        var version = conn.ExecuteScalar<int>("SELECT SchemaVersion FROM _meta WHERE Id = 1");
        version.Should().Be(1);
    }
}
