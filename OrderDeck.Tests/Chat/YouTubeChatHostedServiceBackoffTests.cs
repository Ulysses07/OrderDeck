using FluentAssertions;
using OrderDeck.Chat.Ingestors.YouTube;
using Xunit;

namespace OrderDeck.Tests.Chat;

/// <summary>
/// UI freeze fix (2026-05-13): YouTubeChatHostedService.ComputeBackoff
/// exponential backoff doğrulama. Yayın canlı değilken sürekli crash
/// rejimi UI thread bombardımanı yapıyordu; backoff buna karşı korur.
/// </summary>
public class YouTubeChatHostedServiceBackoffTests
{
    [Fact]
    public void ComputeBackoff_first_crash_returns_baseline_30s()
    {
        var result = YouTubeChatHostedService.ComputeBackoff(1);
        result.Should().Be(System.TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void ComputeBackoff_second_crash_doubles_to_1m()
    {
        var result = YouTubeChatHostedService.ComputeBackoff(2);
        result.Should().Be(System.TimeSpan.FromSeconds(60));
    }

    [Fact]
    public void ComputeBackoff_third_crash_2m()
    {
        var result = YouTubeChatHostedService.ComputeBackoff(3);
        result.Should().Be(System.TimeSpan.FromSeconds(120));
    }

    [Fact]
    public void ComputeBackoff_fourth_crash_4m()
    {
        var result = YouTubeChatHostedService.ComputeBackoff(4);
        result.Should().Be(System.TimeSpan.FromSeconds(240));
    }

    [Fact]
    public void ComputeBackoff_caps_at_5m()
    {
        // 5+ crashes → cap at 5 minutes
        YouTubeChatHostedService.ComputeBackoff(5).Should().Be(System.TimeSpan.FromMinutes(5));
        YouTubeChatHostedService.ComputeBackoff(10).Should().Be(System.TimeSpan.FromMinutes(5));
        YouTubeChatHostedService.ComputeBackoff(100).Should().Be(System.TimeSpan.FromMinutes(5));
    }

    [Fact]
    public void ComputeBackoff_zero_crashes_uses_baseline()
    {
        // Defensive: counter sıfırlanmışsa baseline
        var result = YouTubeChatHostedService.ComputeBackoff(0);
        result.Should().Be(System.TimeSpan.FromSeconds(30));
    }
}
