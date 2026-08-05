using System;
using FluentAssertions;
using OrderDeck.Chat.Ingestors.Instagram;
using Xunit;

namespace OrderDeck.Tests.Chat.Instagram;

public class InstagramGraphErrorTests
{
    private static string Body(int code, int? subcode = null) =>
        subcode is null
            ? $"{{\"error\":{{\"message\":\"x\",\"type\":\"OAuthException\",\"code\":{code}}}}}"
            : $"{{\"error\":{{\"message\":\"x\",\"type\":\"OAuthException\",\"code\":{code},\"error_subcode\":{subcode}}}}}";

    [Theory]
    [InlineData(190)]
    [InlineData(463)]
    public void Token_errors_are_fatal_token_expired(int code)
    {
        InstagramGraphError.Classify(400, Body(code))
            .Should().Be(InstagramErrorKind.TokenExpired);
    }

    [Theory]
    [InlineData(200)]
    [InlineData(10)]
    public void Permission_errors_are_fatal_permission_denied(int code)
    {
        InstagramGraphError.Classify(403, Body(code))
            .Should().Be(InstagramErrorKind.PermissionDenied);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(17)]
    [InlineData(32)]
    [InlineData(613)]
    [InlineData(80002)]
    public void Throttling_codes_are_rate_limited(int code)
    {
        InstagramGraphError.Classify(400, Body(code))
            .Should().Be(InstagramErrorKind.RateLimited);
    }

    [Fact]
    public void Subcode_2446079_is_rate_limited()
    {
        InstagramGraphError.Classify(400, Body(1, 2446079))
            .Should().Be(InstagramErrorKind.RateLimited);
    }

    [Fact]
    public void Code_100_means_broadcast_ended()
    {
        InstagramGraphError.Classify(400, Body(100))
            .Should().Be(InstagramErrorKind.BroadcastEnded);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Generic_api_errors_are_transient(int code)
    {
        InstagramGraphError.Classify(500, Body(code))
            .Should().Be(InstagramErrorKind.Transient);
    }

    [Fact]
    public void Server_error_without_parsable_body_is_transient()
    {
        InstagramGraphError.Classify(502, "<html>bad gateway</html>")
            .Should().Be(InstagramErrorKind.Transient);
    }

    [Fact]
    public void Unknown_code_is_transient()
    {
        // Bilinmeyen kodda oturumu öldürmüyoruz — geri çekilip tekrar deniyoruz.
        InstagramGraphError.Classify(400, Body(999999))
            .Should().Be(InstagramErrorKind.Transient);
    }

    // ── Kota başlığı ─────────────────────────────────────────────────────────

    [Fact]
    public void Parses_estimated_time_to_regain_access_in_minutes()
    {
        const string header =
            "{\"3939617702835404\":[{\"type\":\"instagram\",\"call_count\":100," +
            "\"total_cputime\":25,\"total_time\":25," +
            "\"estimated_time_to_regain_access\":12}]}";

        InstagramGraphError.TryGetRetryAfter(header, out var wait).Should().BeTrue();
        wait.Should().Be(TimeSpan.FromMinutes(12));
    }

    [Fact]
    public void Fractional_estimated_time_is_rounded_up()
    {
        // Meta bu alanı kesirli de yazabiliyor; atlanırsa kota aşımında
        // hiç beklemeden tekrar denenir.
        const string header =
            "{\"app\":[{\"type\":\"instagram\",\"estimated_time_to_regain_access\":12.4}]}";

        InstagramGraphError.TryGetRetryAfter(header, out var wait).Should().BeTrue();
        wait.Should().Be(TimeSpan.FromMinutes(13));
    }

    [Fact]
    public void Zero_estimated_time_means_no_wait_required()
    {
        const string header =
            "{\"app\":[{\"type\":\"instagram\",\"estimated_time_to_regain_access\":0}]}";

        InstagramGraphError.TryGetRetryAfter(header, out _).Should().BeFalse();
    }

    [Fact]
    public void Missing_or_garbage_header_returns_false()
    {
        InstagramGraphError.TryGetRetryAfter(null, out _).Should().BeFalse();
        InstagramGraphError.TryGetRetryAfter("", out _).Should().BeFalse();
        InstagramGraphError.TryGetRetryAfter("not json", out _).Should().BeFalse();
    }

    [Fact]
    public void Picks_the_largest_wait_across_buckets()
    {
        // Yanıtta birden çok kova olabilir; en uzun beklemeye uyarız.
        const string header =
            "{\"app\":[{\"type\":\"pages\",\"estimated_time_to_regain_access\":3}," +
            "{\"type\":\"instagram\",\"estimated_time_to_regain_access\":9}]}";

        InstagramGraphError.TryGetRetryAfter(header, out var wait).Should().BeTrue();
        wait.Should().Be(TimeSpan.FromMinutes(9));
    }
}
