using FluentAssertions;
using LiveDeck.Core.Sessions;
using LiveDeck.Core.Storage;
using LiveDeck.Core.Storage.Repositories;
using LiveDeck.Tests.TestHelpers;
using Xunit;

namespace LiveDeck.Tests.Storage;

public class SessionRepositoryTests
{
    [Fact]
    public void Insert_then_GetActive_returns_session_with_null_EndedAt()
    {
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var repo = new SessionRepository(db);

        var session = new StreamSession("s1", "Akşam Yayını", 1000, null,
            new[] { "instagram" }, null);
        repo.Insert(session);

        repo.GetActive().Should().NotBeNull();
        repo.GetActive()!.Id.Should().Be("s1");
    }

    [Fact]
    public void End_sets_EndedAt_and_GetActive_returns_null()
    {
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var repo = new SessionRepository(db);
        repo.Insert(new StreamSession("s1", null, 1000, null, new[] { "instagram" }, null));

        repo.End("s1", endedAt: 2000);

        repo.GetActive().Should().BeNull();
    }

    [Fact]
    public void GetAllEnded_returns_only_ended_sessions_newest_first()
    {
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var repo = new SessionRepository(db);

        repo.Insert(new StreamSession("s1", null, 1000, EndedAt: 1500, new[] { "instagram" }, null));
        repo.Insert(new StreamSession("s2", null, 2000, EndedAt: null,  new[] { "instagram" }, null));
        repo.Insert(new StreamSession("s3", null, 3000, EndedAt: 3500, new[] { "tiktok" },    null));

        var ended = repo.GetAllEnded(limit: 10);

        ended.Should().HaveCount(2);
        ended[0].Id.Should().Be("s3");
        ended[1].Id.Should().Be("s1");
    }
}
