using Dapper;
using FluentAssertions;
using LiveDeck.Core.Storage;
using LiveDeck.Tests.TestHelpers;
using Xunit;

namespace LiveDeck.Tests.Storage;

public class MigrationRunnerTests
{
    [Fact]
    public void Run_creates_label_and_customer_aggregates_at_version_2()
    {
        using var db = new InMemorySqlite();
        var runner = new MigrationRunner(db);

        runner.Run();

        using var conn = db.Open();
        var tables = conn.Query<string>(
            "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name").AsList();

        tables.Should().Contain(new[]
        {
            "Customer", "Label", "Settings", "StreamSession", "_meta"
        });
        tables.Should().NotContain(new[]
        {
            "ActiveCode", "OrderItem", "Giveaway", "GiveawayParticipant"
        });

        var version = conn.ExecuteScalar<int>("SELECT SchemaVersion FROM _meta WHERE Id = 1");
        version.Should().Be(2);

        var customerColumns = conn.Query<string>(
            "SELECT name FROM pragma_table_info('Customer')").AsList();
        customerColumns.Should().Contain(new[] { "TotalLabelsPrinted", "TotalAmount" });
    }

    [Fact]
    public void Run_is_idempotent()
    {
        using var db = new InMemorySqlite();
        var runner = new MigrationRunner(db);

        runner.Run();
        runner.Run();   // second call must not throw or duplicate columns

        using var conn = db.Open();
        var version = conn.ExecuteScalar<int>("SELECT SchemaVersion FROM _meta WHERE Id = 1");
        version.Should().Be(2);
    }
}
