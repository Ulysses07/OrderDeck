using System;
using FluentAssertions;
using OrderDeck.Chat.Ingestors.Facebook;
using Xunit;

namespace OrderDeck.Tests.Chat.Facebook;

/// <summary>
/// Locks down the bootstrap-crash exponential backoff. Same shape as
/// <see cref="OrderDeck.Chat.Ingestors.YouTube.YouTubeChatHostedService"/>'s
/// — keeps reconnect cadence consistent across platforms so operators
/// don't see one ingestor recover much faster than the other.
/// </summary>
public class FacebookChatHostedServiceTests
{
    [Fact]
    public void ComputeBackoff_zero_or_one_crash_returns_short_idle()
    {
        FacebookChatHostedService.ComputeBackoff(0)
            .Should().Be(TimeSpan.FromSeconds(30));
        FacebookChatHostedService.ComputeBackoff(1)
            .Should().Be(TimeSpan.FromSeconds(30));
    }

    [Theory]
    [InlineData(2,  60)]    // 30s × 2^1
    [InlineData(3, 120)]    // 30s × 2^2
    [InlineData(4, 240)]    // 30s × 2^3
    public void ComputeBackoff_doubles_until_cap(int crashes, int expectedSeconds)
    {
        FacebookChatHostedService.ComputeBackoff(crashes)
            .Should().Be(TimeSpan.FromSeconds(expectedSeconds));
    }

    [Theory]
    [InlineData(5)]
    [InlineData(8)]
    [InlineData(20)]
    public void ComputeBackoff_caps_at_five_minutes(int crashes)
    {
        FacebookChatHostedService.ComputeBackoff(crashes)
            .Should().Be(TimeSpan.FromMinutes(5));
    }
}
