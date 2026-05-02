using Dapper;
using FluentAssertions;
using OrderDeck.Core.Storage;
using OrderDeck.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.Tests.Storage;

public class MigrationRunnerTests
{
    [Fact]
    public void Run_creates_all_tables_at_version_5_with_dropped_legacy_columns()
    {
        using var db = new InMemorySqlite();
        var runner = new MigrationRunner(db);

        runner.Run();

        using var conn = db.Open();
        var tables = conn.Query<string>(
            "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name").AsList();
        tables.Should().Contain(new[]
        {
            "Customer", "Giveaway", "GiveawayParticipant", "Label",
            "Settings", "StreamSession", "_meta"
        });

        var version = conn.ExecuteScalar<int>("SELECT SchemaVersion FROM _meta WHERE Id = 1");
        version.Should().Be(8);

        var customerColumns = conn.Query<string>(
            "SELECT name FROM pragma_table_info('Customer')").AsList();
        customerColumns.Should().Contain(new[]
            { "TotalLabelsPrinted", "TotalAmount", "BlacklistedAt", "Notes", "IsBlacklisted" });
        customerColumns.Should().NotContain(new[]
            { "TrustScore", "TotalOrders", "CompletedOrders", "CancelledOrders" });
    }

    [Fact]
    public void Run_is_idempotent()
    {
        using var db = new InMemorySqlite();
        var runner = new MigrationRunner(db);

        runner.Run();
        runner.Run();

        using var conn = db.Open();
        var version = conn.ExecuteScalar<int>("SELECT SchemaVersion FROM _meta WHERE Id = 1");
        version.Should().Be(8);
    }

    [Fact]
    public void Migration_006_adds_Address_column_to_Customer()
    {
        using var db = new InMemorySqlite();
        var runner = new MigrationRunner(db);
        runner.Run();

        using var conn = db.Open();
        var hasAddress = conn.ExecuteScalar<int>(@"
            SELECT COUNT(*) FROM pragma_table_info('Customer') WHERE name = 'Address'");
        hasAddress.Should().Be(1);
    }

    [Fact]
    public void Run_AppliesMigration007_AddsPhoneColumn()
    {
        using var db = new InMemorySqlite();
        var runner = new MigrationRunner(db);
        runner.Run();

        using var conn = db.Open();
        var hasPhone = conn.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM pragma_table_info('Customer') WHERE name = 'Phone'");
        hasPhone.Should().Be(1);

        var version = conn.ExecuteScalar<int>("SELECT SchemaVersion FROM _meta WHERE Id = 1");
        version.Should().BeGreaterThanOrEqualTo(8);
    }
}
