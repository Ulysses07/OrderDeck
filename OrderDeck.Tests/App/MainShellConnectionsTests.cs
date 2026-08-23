using System.Linq;
using FluentAssertions;
using Xunit;

namespace OrderDeck.Tests.App;

public class MainShellConnectionsTests
{
    [Fact]
    public void Four_platforms_are_always_listed()
    {
        using var h = MainShellTestHarness.Build();

        h.Vm.RefreshConnections();

        // Bağlı olmayan platform listeden düşerse operatör "bağlanmadı mı,
        // yoksa hiç mi yok?" ayrımını yapamaz — dördü de hep durur.
        h.Vm.Connections.Select(c => c.Platform)
            .Should().Equal("youtube", "instagram", "tiktok", "facebook");
    }

    [Fact]
    public void Platforms_without_a_tracker_are_all_disconnected()
    {
        using var h = MainShellTestHarness.Build();   // ViewerCountTracker verilmedi

        h.Vm.RefreshConnections();

        h.Vm.Connections.Should().OnlyContain(c => !c.IsConnected);
    }

    [Fact]
    public void Printer_line_shows_the_configured_printer()
    {
        using var h = MainShellTestHarness.Build();

        h.Vm.RefreshPrinterStatus("Zebra ZD420");

        h.Vm.PrinterStatusText.Should().Be("Zebra ZD420");
        h.Vm.IsPrinterConfigured.Should().BeTrue();
    }

    [Fact]
    public void Printer_line_warns_when_no_printer_is_configured()
    {
        using var h = MainShellTestHarness.Build();

        h.Vm.RefreshPrinterStatus(null);

        h.Vm.PrinterStatusText.Should().Be("Yazıcı seçilmedi");
        h.Vm.IsPrinterConfigured.Should().BeFalse();
    }

    [Fact]
    public void Connection_view_model_exposes_a_turkish_display_name()
    {
        new OrderDeck.App.ViewModels.PlatformConnectionViewModel("youtube")
            .DisplayName.Should().Be("YouTube");
        new OrderDeck.App.ViewModels.PlatformConnectionViewModel("tiktok")
            .DisplayName.Should().Be("TikTok");
    }
}
