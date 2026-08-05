using System;
using FluentAssertions;
using OrderDeck.Chat.Ingestors.Instagram;
using Xunit;

namespace OrderDeck.Tests.Chat.Instagram;

/// <summary>
/// Backoff eğrisini kilitler. Facebook ve YouTube ile aynı şekil (30s × 2ⁿ,
/// 5dk tavan) olmalı ki operatör bir platformun diğerinden çok daha hızlı
/// toparladığını görmesin.
/// </summary>
public class InstagramChatHostedServiceTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void ComputeBackoff_zero_or_one_crash_returns_short_idle(int crashes)
    {
        InstagramChatHostedService.ComputeBackoff(crashes)
            .Should().Be(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void ComputeBackoff_doubles_per_crash()
    {
        InstagramChatHostedService.ComputeBackoff(2).Should().Be(TimeSpan.FromSeconds(60));
        InstagramChatHostedService.ComputeBackoff(3).Should().Be(TimeSpan.FromSeconds(120));
        InstagramChatHostedService.ComputeBackoff(4).Should().Be(TimeSpan.FromSeconds(240));
    }

    [Fact]
    public void ComputeBackoff_caps_at_five_minutes()
    {
        InstagramChatHostedService.ComputeBackoff(5).Should().Be(TimeSpan.FromMinutes(5));
        InstagramChatHostedService.ComputeBackoff(50).Should().Be(TimeSpan.FromMinutes(5));
    }
}
