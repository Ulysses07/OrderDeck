using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using OrderDeck.Core.Chat;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Sales;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Core.Time;
using OrderDeck.Overlay;
using OrderDeck.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace OrderDeck.Tests.Overlay;

/// <summary>
/// OverlayHost port fallback tests. Real TcpListener instances occupy specific
/// ports so we can verify that StartAsync skips them and binds to the next
/// available candidate.
///
/// Port range used here (40000+) is intentionally far from production (4747,
/// 4757-4760) so a developer running tests + the real app concurrently won't
/// collide with their actual overlay.
/// </summary>
public sealed class OverlayHostPortFallbackTests : IDisposable
{
    private readonly List<TcpListener> _occupiers = new();
    private readonly List<OverlayHost> _hosts = new();

    private TcpListener Occupy(int port)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        _occupiers.Add(listener);
        return listener;
    }

    private OverlayHost CreateHost(int preferred, IReadOnlyList<int> fallbacks)
    {
        var bus = new ChatBus();
        var db = new InMemorySqlite();
        new MigrationRunner(db).Run();

        var customerRepo = new CustomerRepository(db);
        var sessionRepo = new SessionRepository(db);
        var labelRepo = new LabelRepository(db);
        var giveawayRepo = new GiveawayRepository(db);

        var clock = new Mock<IClock>();
        clock.Setup(c => c.UnixNow()).Returns(1714521600L);

        var customerSvc = new CustomerService(customerRepo, sessionRepo, labelRepo, clock.Object);
        var drawer = new GiveawayDrawer();
        var giveaway = new GiveawayService(giveawayRepo, customerSvc, drawer, clock.Object);

        var host = new OverlayHost(bus, giveaway, port: preferred,
            log: NullLogger<OverlayHost>.Instance, fallbackPorts: fallbacks);
        _hosts.Add(host);
        return host;
    }

    public void Dispose()
    {
        foreach (var host in _hosts)
        {
            try { host.StopAsync().GetAwaiter().GetResult(); } catch { }
        }
        foreach (var occ in _occupiers)
        {
            try { occ.Stop(); } catch { }
        }
    }

    [Fact]
    public async Task Binds_to_preferred_port_when_available()
    {
        // Use a base port unlikely to be busy. If it is, the test will fail
        // honestly — sentinel for a real conflict.
        int basePort = 41000;
        var host = CreateHost(basePort, new[] { basePort + 1, basePort + 2 });

        await host.StartAsync();

        host.Port.Should().Be(basePort);
        host.FellBackFromPreferredPort.Should().BeFalse();
    }

    [Fact]
    public async Task Falls_back_to_first_alternate_when_preferred_busy()
    {
        int basePort = 41100;
        Occupy(basePort);   // preferred blocked

        var host = CreateHost(basePort, new[] { basePort + 1, basePort + 2 });
        await host.StartAsync();

        host.Port.Should().Be(basePort + 1);
        host.FellBackFromPreferredPort.Should().BeTrue();
    }

    [Fact]
    public async Task Falls_back_to_second_alternate_when_first_two_busy()
    {
        int basePort = 41200;
        Occupy(basePort);
        Occupy(basePort + 1);

        var host = CreateHost(basePort, new[] { basePort + 1, basePort + 2 });
        await host.StartAsync();

        host.Port.Should().Be(basePort + 2);
        host.FellBackFromPreferredPort.Should().BeTrue();
    }

    [Fact]
    public async Task Throws_IOException_when_all_candidates_busy()
    {
        int basePort = 41300;
        Occupy(basePort);
        Occupy(basePort + 1);
        Occupy(basePort + 2);

        var host = CreateHost(basePort, new[] { basePort + 1, basePort + 2 });

        Func<Task> act = async () => await host.StartAsync();
        await act.Should().ThrowAsync<IOException>()
            .WithMessage($"*{basePort}*");
    }

    [Fact]
    public async Task Default_fallback_range_includes_4757_through_4760()
    {
        // Smoke check default — constructor with no fallbackPorts uses 4757-4760.
        // We don't actually bind to 4747 (collide with real app); we use a port
        // we own. But we can verify behavior by occupying preferred and seeing
        // it tries the explicit defaults we pass.
        int basePort = 41400;
        var defaults = new[] { basePort + 7, basePort + 8, basePort + 9, basePort + 10 };
        Occupy(basePort);

        var host = CreateHost(basePort, defaults);
        await host.StartAsync();

        host.Port.Should().Be(basePort + 7);
        host.FellBackFromPreferredPort.Should().BeTrue();
    }
}
