using Dapper;
using FluentAssertions;
using OrderDeck.Core.Sessions;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.Tests.Storage;

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

    // §5.9 (W1): GetActive yalnız bu bilgisayarın yazdığı oturumu (Authority = 'local') görür. Savunma
    // katmanı: uzak oturum EndedAt NULL olarak inmez, başka bilgisayarın canlı yayını RemoteLiveSession'da
    // durur (M5). Yine de 'server' bir satır "aktif" sayılsaydı sohbet etiketi o oturuma yazılmaya
    // çalışılır, 049'un ABORT tetikleyicisi canlı yolu kırardı. Satır SQL ile kurulur: bu hâli yazan
    // devir yolu W2'de.
    [Fact]
    public void GetActive_ignores_an_open_session_with_server_authority()
    {
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var repo = new SessionRepository(db);
        repo.Insert(new StreamSession("yerel", null, 1000, null, new[] { "instagram" }, null));
        repo.Insert(new StreamSession("sunucu", null, 2000, null, new[] { "instagram" }, null));
        using (var c = db.Open())
            c.Execute("UPDATE StreamSession SET Authority = 'server' WHERE Id = 'sunucu'");

        repo.GetActive()!.Id.Should().Be("yerel",
            "daha yeni olsa da sunucunun yazdığı oturum bu bilgisayarın canlı yayını değil");

        repo.End("yerel", endedAt: 3000);

        repo.GetActive().Should().BeNull("açık kalan tek satır Authority = 'server'");
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

    [Fact]
    public void GetLatestEnded_NoSessions_ReturnsNull()
    {
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var repo = new SessionRepository(db);
        repo.GetLatestEnded().Should().BeNull();
    }

    [Fact]
    public void GetLatestEnded_OnlyActiveSession_ReturnsNull()
    {
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var repo = new SessionRepository(db);
        repo.Insert(new StreamSession("s1", "Live", 1000, null, System.Array.Empty<string>(), null));
        repo.GetLatestEnded().Should().BeNull();
    }

    [Fact]
    public void GetLatestEnded_ReturnsMostRecentlyEndedByEndedAt()
    {
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var repo = new SessionRepository(db);
        repo.Insert(new StreamSession("s1", "Old", 100, null, System.Array.Empty<string>(), null));
        repo.End("s1", 200);
        repo.Insert(new StreamSession("s2", "New", 300, null, System.Array.Empty<string>(), null));
        repo.End("s2", 400);

        var latest = repo.GetLatestEnded();
        latest!.Id.Should().Be("s2");
        latest.EndedAt.Should().Be(400);
    }
}
