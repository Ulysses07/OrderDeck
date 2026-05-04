using System.Net;
using FluentAssertions;
using Google;
using OrderDeck.Chat.YouTube;
using Xunit;

namespace OrderDeck.Tests.Chat.YouTube;

/// <summary>
/// Error-mapping coverage for <see cref="YouTubeModerationService.MapException"/>.
/// We exercise the function directly (made <c>internal</c> + <c>InternalsVisibleTo</c>)
/// instead of standing up the full Google.Apis client — the goal is to lock
/// down the user-facing Turkish messages so a refactor of the helper
/// can't silently change what the operator sees.
/// </summary>
public class YouTubeModerationServiceTests
{
    private static GoogleApiException FakeApiException(HttpStatusCode status, string message)
    {
        // GoogleApiException's public constructor takes (string serviceName, string message);
        // the HTTP status is set via property assignment.
        return new GoogleApiException("youtube", message)
        {
            HttpStatusCode = status,
        };
    }

    [Fact]
    public void MapException_401_returns_session_expired_message()
    {
        var ex = FakeApiException(HttpStatusCode.Unauthorized, "auth bad");
        var mapped = YouTubeModerationService.MapException(ex);

        mapped.Should().BeOfType<ModerationException>();
        mapped.Message.Should().Contain("oturum");
        mapped.Message.Should().Contain("tekrar bağlan");
        mapped.InnerException.Should().Be(ex);
    }

    [Fact]
    public void MapException_403_returns_permission_message()
    {
        var ex = FakeApiException(HttpStatusCode.Forbidden, "no scope");
        var mapped = YouTubeModerationService.MapException(ex);

        mapped.Message.Should().Contain("yetki");
        mapped.Message.Should().Contain("moderatör");
    }

    [Fact]
    public void MapException_404_returns_not_found_message()
    {
        var ex = FakeApiException(HttpStatusCode.NotFound, "no such msg");
        var mapped = YouTubeModerationService.MapException(ex);

        mapped.Message.Should().Contain("bulunamadı");
    }

    [Fact]
    public void MapException_429_returns_quota_message()
    {
        var ex = FakeApiException((HttpStatusCode)429, "rate limited");
        var mapped = YouTubeModerationService.MapException(ex);

        mapped.Message.Should().Contain("kota");
        mapped.Message.Should().Contain("Yarın");
    }

    [Fact]
    public void MapException_unknown_status_includes_underlying_message()
    {
        var ex = FakeApiException(HttpStatusCode.InternalServerError, "upstream timeout");
        var mapped = YouTubeModerationService.MapException(ex);

        mapped.Message.Should().Contain("YouTube API hatası");
        mapped.Message.Should().Contain("upstream timeout");
    }

    [Fact]
    public void ModerationException_constructor_with_message_only_has_no_inner()
    {
        var ex = new ModerationException("custom failure");

        ex.Message.Should().Be("custom failure");
        ex.InnerException.Should().BeNull();
    }
}
