using System.Net;
using FluentAssertions;
using OrderDeck.Chat.Facebook;
using OrderDeck.Chat.YouTube; // ModerationException lives here, shared across platforms
using Xunit;

namespace OrderDeck.Tests.Chat.Facebook;

/// <summary>
/// Error-mapping coverage for <see cref="FacebookModerationService.MapException"/>.
/// Direct unit tests (no real HTTP) to lock down the user-facing Turkish
/// messages — same approach as <see cref="YouTubeModerationService"/>.
/// </summary>
public class FacebookModerationServiceTests
{
    private static FacebookModerationService.GraphError Error(int code, int? subcode = null, string message = "boom")
        => new() { Code = code, ErrorSubcode = subcode, Message = message };

    [Fact]
    public void MapException_code190_returns_session_expired_message()
    {
        var mapped = FacebookModerationService.MapException(HttpStatusCode.OK, Error(190));

        mapped.Should().BeOfType<ModerationException>();
        mapped.Message.Should().Contain("oturum");
        mapped.Message.Should().Contain("tekrar bağlan");
    }

    [Fact]
    public void MapException_code200_returns_permission_message()
    {
        var mapped = FacebookModerationService.MapException(HttpStatusCode.OK, Error(200));

        mapped.Message.Should().Contain("yetki");
        mapped.Message.Should().Contain("admin");
    }

    [Theory]
    [InlineData(33)]
    [InlineData(1357040)]
    public void MapException_code100_with_known_subcode_returns_not_found_message(int subcode)
    {
        var mapped = FacebookModerationService.MapException(
            HttpStatusCode.OK, Error(100, subcode));

        mapped.Message.Should().Contain("bulunamadı");
    }

    [Theory]
    [InlineData(4)]
    [InlineData(17)]
    [InlineData(32)]
    [InlineData(613)]
    public void MapException_rate_limit_codes_return_throttle_message(int code)
    {
        var mapped = FacebookModerationService.MapException(HttpStatusCode.OK, Error(code));

        mapped.Message.Should().Contain("limit");
        mapped.Message.Should().Contain("tekrar dene");
    }

    [Fact]
    public void MapException_unknown_code_includes_underlying_message()
    {
        var mapped = FacebookModerationService.MapException(
            HttpStatusCode.OK, Error(99999, message: "upstream gibberish"));

        mapped.Message.Should().Contain("Facebook API hatası");
        mapped.Message.Should().Contain("upstream gibberish");
    }

    [Fact]
    public void MapException_no_graph_error_falls_back_to_http_status_401()
    {
        var mapped = FacebookModerationService.MapException(HttpStatusCode.Unauthorized, error: null);

        mapped.Message.Should().Contain("oturum");
    }

    [Fact]
    public void MapException_no_graph_error_falls_back_to_http_status_403()
    {
        var mapped = FacebookModerationService.MapException(HttpStatusCode.Forbidden, error: null);

        mapped.Message.Should().Contain("yetki");
    }

    [Fact]
    public void MapException_no_graph_error_falls_back_to_http_status_404()
    {
        var mapped = FacebookModerationService.MapException(HttpStatusCode.NotFound, error: null);

        mapped.Message.Should().Contain("bulunamadı");
    }

    [Fact]
    public void MapException_no_graph_error_falls_back_to_http_status_429()
    {
        var mapped = FacebookModerationService.MapException((HttpStatusCode)429, error: null);

        mapped.Message.Should().Contain("limit");
    }

    [Fact]
    public void MapException_no_graph_error_unknown_status_includes_http_code()
    {
        var mapped = FacebookModerationService.MapException(HttpStatusCode.InternalServerError, error: null);

        mapped.Message.Should().Contain("Facebook API hatası");
        mapped.Message.Should().Contain("500");
    }
}
