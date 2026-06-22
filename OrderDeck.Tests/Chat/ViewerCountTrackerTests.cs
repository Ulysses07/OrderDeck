using System;
using System.Linq;
using System.Threading;
using FluentAssertions;
using OrderDeck.Core.Chat;
using Xunit;

namespace OrderDeck.Tests.Chat;

public class ViewerCountTrackerTests
{
    private static readonly TimeSpan Wide = TimeSpan.FromMinutes(5);

    [Fact]
    public void Sums_fresh_platforms()
    {
        var t = new ViewerCountTracker();
        t.Report("instagram", 412);
        t.Report("tiktok", 388);
        t.Report("youtube", 210);

        var snap = t.GetSnapshot(Wide);
        snap.Total.Should().Be(1010);
        snap.PerPlatform.Should().HaveCount(3);
        snap.PerPlatform.Single(p => p.Platform == "tiktok").Count.Should().Be(388);
    }

    [Fact]
    public void Latest_report_overwrites_previous()
    {
        var t = new ViewerCountTracker();
        t.Report("instagram", 100);
        t.Report("instagram", 250);

        t.GetSnapshot(Wide).Total.Should().Be(250);
    }

    [Fact]
    public void Stale_entries_drop_out()
    {
        var t = new ViewerCountTracker();
        t.Report("facebook", 500);
        Thread.Sleep(40);

        // 10ms penceresi: 40ms önce raporlanan kayıt eskidir → düşer.
        t.GetSnapshot(TimeSpan.FromMilliseconds(10)).Total.Should().Be(0);
        // Geniş pencerede hâlâ var.
        t.GetSnapshot(Wide).Total.Should().Be(500);
    }

    [Fact]
    public void Clear_resets_everything()
    {
        var t = new ViewerCountTracker();
        t.Report("youtube", 999);
        t.Clear();

        var snap = t.GetSnapshot(Wide);
        snap.Total.Should().Be(0);
        snap.PerPlatform.Should().BeEmpty();
    }

    [Theory]
    [InlineData(-5)]
    public void Ignores_negative_counts(int bad)
    {
        var t = new ViewerCountTracker();
        t.Report("instagram", bad);
        t.GetSnapshot(Wide).Total.Should().Be(0);
    }

    [Fact]
    public void Ignores_blank_platform()
    {
        var t = new ViewerCountTracker();
        t.Report("  ", 10);
        t.GetSnapshot(Wide).Total.Should().Be(0);
    }
}
