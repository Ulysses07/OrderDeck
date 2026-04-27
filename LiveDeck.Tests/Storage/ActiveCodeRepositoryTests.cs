using System.Collections.Generic;
using FluentAssertions;
using LiveDeck.Core.Sales;
using LiveDeck.Core.Sessions;
using LiveDeck.Core.Storage;
using LiveDeck.Core.Storage.Repositories;
using LiveDeck.Tests.TestHelpers;
using Xunit;

namespace LiveDeck.Tests.Storage;

public class ActiveCodeRepositoryTests
{
    private static (InMemorySqlite Db, ActiveCodeRepository Repo, string SessionId) NewFixture()
    {
        var db = new InMemorySqlite();
        new MigrationRunner(db).Run();

        var sessionRepo = new SessionRepository(db);
        var sessionId = "session-1";
        sessionRepo.Insert(new StreamSession(sessionId, null, 100, null, new[] { "instagram" }, null));

        return (db, new ActiveCodeRepository(db), sessionId);
    }

    [Fact]
    public void Insert_then_GetActiveBySession_returns_inserted_codes()
    {
        var (db, repo, sessionId) = NewFixture();
        using var _ = db;

        var code = new ActiveCode(
            Id: "code-1",
            SessionId: sessionId,
            Code: "MAVI",
            Sizes: new List<string> { "S", "M", "XL" },
            Price: 199m,
            ImageUrl: null,
            Aliases: new List<string>(),
            StartedAt: 200,
            EndedAt: null);

        repo.Insert(code);

        var active = repo.GetActiveBySession(sessionId);
        active.Should().HaveCount(1);
        active[0].Code.Should().Be("MAVI");
        active[0].Sizes.Should().BeEquivalentTo(new[] { "S", "M", "XL" });
        active[0].Price.Should().Be(199m);
    }

    [Fact]
    public void Update_changes_price_and_sizes()
    {
        var (db, repo, sessionId) = NewFixture();
        using var _ = db;
        var code = new ActiveCode("c1", sessionId, "MAVI",
            new List<string> { "M" }, 100m, null, new List<string>(), 200, null);
        repo.Insert(code);

        var updated = code with { Price = 150m, Sizes = new List<string> { "M", "L" } };
        repo.Update(updated);

        var fresh = repo.GetActiveBySession(sessionId)[0];
        fresh.Price.Should().Be(150m);
        fresh.Sizes.Should().BeEquivalentTo(new[] { "M", "L" });
    }

    [Fact]
    public void End_sets_EndedAt_and_excludes_from_active_list()
    {
        var (db, repo, sessionId) = NewFixture();
        using var _ = db;
        var code = new ActiveCode("c1", sessionId, "MAVI",
            new List<string> { "M" }, 100m, null, new List<string>(), 200, null);
        repo.Insert(code);

        repo.End("c1", endedAt: 300);

        repo.GetActiveBySession(sessionId).Should().BeEmpty();
    }
}
