using Dapper;
using FluentAssertions;
using OrderDeck.Core.Sales;
using OrderDeck.Core.Sessions;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.Tests.Storage;

public class GiveawayRepositoryAnimationTests
{
    [Fact]
    public void Insert_then_GetById_round_trips_AnimationId()
    {
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();

        new SessionRepository(db).Insert(
            new StreamSession("s1", "Live", 100, null, new[] { "instagram" }, null));

        var repo = new GiveawayRepository(db);
        var g = new Giveaway(
            Id: "g1", SessionId: "s1", Keyword: "kazan",
            DurationSeconds: 60, WinnerCount: 1, PlatformFilter: null,
            PreventRewinning: true, RandomSeed: "seed",
            StartedAt: 100, EndedAt: null, CancelledAt: null,
            AnimationId: "slot-machine");

        repo.Insert(g);
        var loaded = repo.GetById("g1");

        loaded.Should().NotBeNull();
        loaded!.AnimationId.Should().Be("slot-machine");
    }

    [Fact]
    public void Migration_backfills_existing_rows_with_wheel_default()
    {
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();

        new SessionRepository(db).Insert(
            new StreamSession("s1", "Live", 100, null, new[] { "instagram" }, null));

        // Insert via raw SQL omitting AnimationId, simulating a row written
        // by code older than migration 013. The DEFAULT 'wheel' must apply.
        using (var conn = db.Open())
        {
            conn.Execute(
                @"INSERT INTO Giveaway (Id,SessionId,Keyword,DurationSeconds,WinnerCount,
                                        PreventRewinning,RandomSeed,StartedAt)
                  VALUES ('g-old','s1','kazan',60,1,1,'seed',100)");
        }

        var loaded = new GiveawayRepository(db).GetById("g-old");
        loaded!.AnimationId.Should().Be("wheel");
    }
}
