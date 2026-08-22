using System.Net.WebSockets;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using OrderDeck.Core.Chat;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Sales;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Core.Time;
using OrderDeck.Overlay;
using OrderDeck.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.Tests.Overlay;

/// <summary>
/// Y-04: overlay WebSocket'i el sıkışmada hiçbir denetim yapmıyordu. Kestrel
/// loopback'e bağlı olsa da WebSocket CORS'a tabi değil — yayıncının
/// tarayıcısında açık herhangi bir sayfa <c>ws://localhost:4747/ws/chat</c>'e
/// bağlanıp canlı sohbetin tamamını okuyabiliyordu. Bağlantı anında gönderilen
/// <c>chat.snapshot</c> geçmişi de veriyor.
///
/// El sıkışma gerçek bir <see cref="ClientWebSocket"/> ile kuruluyor: ölçülmek
/// istenen şey sunucunun HTTP yükseltmesine verdiği yanıt.
///
/// Port aralığı (43000+) üretimden (4747, 4757-4760) uzak seçildi.
/// </summary>
public sealed class OverlayOriginTests : IAsyncLifetime
{
    private readonly List<OverlayHost> _hosts = new();
    private readonly List<ClientWebSocket> _clients = new();

    private OverlayHost CreateHost(int port)
    {
        var db = new InMemorySqlite();
        new MigrationRunner(db).Run();

        var clock = new Mock<IClock>();
        clock.Setup(c => c.UnixNow()).Returns(1714521600L);

        var customerSvc = new CustomerService(
            new CustomerRepository(db), new SessionRepository(db),
            new LabelRepository(db), clock.Object);
        var giveaway = new GiveawayService(
            new GiveawayRepository(db), customerSvc, new GiveawayDrawer(), clock.Object);

        var host = new OverlayHost(new ChatBus(), giveaway, port: port,
            log: NullLogger<OverlayHost>.Instance, fallbackPorts: Array.Empty<int>());
        _hosts.Add(host);
        return host;
    }

    private async Task<Exception?> TryConnectAsync(int port, string path, string? origin)
    {
        var ws = new ClientWebSocket();
        if (origin is not null) ws.Options.SetRequestHeader("Origin", origin);
        _clients.Add(ws);

        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            await ws.ConnectAsync(new Uri($"ws://localhost:{port}{path}"), ct.Token);
            return null;
        }
        catch (Exception ex) { return ex; }
    }

    // Not: her olgu kendi port'unu alıyor — host'lar sınıfın sonunda (DisposeAsync)
    // kapatıldığı için aynı port'u paylaşan iki olgudan ikincisi bağlanamazdı.
    [Theory]
    [InlineData("/ws/chat", 43000)]
    [InlineData("/ws/giveaway", 43001)]
    public async Task Foreign_origin_is_rejected(string path, int port)
    {
        await CreateHost(port).StartAsync();

        var error = await TryConnectAsync(port, path, "https://kotu-site.example");

        error.Should().BeOfType<WebSocketException>(
            "yayıncının açtığı rastgele bir sayfa canlı sohbeti dinleyememeli");
        error!.Message.Should().Contain("403");
    }

    /// <summary>
    /// Tarayıcılar <c>Origin</c>'i her zaman gönderir; göndermeyen istemci
    /// tarayıcı değildir — overlay'in tek meşru istemcisi ise bir tarayıcı
    /// (OBS Browser Source / WebView2).
    /// </summary>
    [Fact]
    public async Task Missing_origin_is_rejected()
    {
        const int port = 43100;
        await CreateHost(port).StartAsync();

        var error = await TryConnectAsync(port, "/ws/chat", origin: null);

        error.Should().BeOfType<WebSocketException>();
        error!.Message.Should().Contain("403");
    }

    /// <summary>
    /// DNS yeniden bağlama: <c>evil.com</c> 127.0.0.1'e çözülse bile tarayıcı
    /// kaynağı konak adıyla gönderir. Loopback denetimi tek başına yetmez,
    /// port + konak birlikte sabitlenmeli.
    /// </summary>
    [Fact]
    public async Task Rebound_hostname_on_the_same_port_is_rejected()
    {
        const int port = 43200;
        await CreateHost(port).StartAsync();

        var error = await TryConnectAsync(port, "/ws/chat", $"http://kotu-site.example:{port}");

        error.Should().BeOfType<WebSocketException>();
        error!.Message.Should().Contain("403");
    }

    /// <summary>Başka bir yerel servisin sayfası da geçememeli.</summary>
    [Fact]
    public async Task Loopback_origin_on_a_different_port_is_rejected()
    {
        const int port = 43300;
        await CreateHost(port).StartAsync();

        var error = await TryConnectAsync(port, "/ws/chat", $"http://localhost:{port + 1}");

        error.Should().BeOfType<WebSocketException>();
        error!.Message.Should().Contain("403");
    }

    /// <summary>
    /// Operatör OBS'e hangi loopback adını yazarsa yazsın overlay sayfası aynı
    /// kaynaklı olur; üçü de çalışmalı.
    /// </summary>
    [Theory]
    [InlineData("http://localhost", 43400)]
    [InlineData("http://127.0.0.1", 43401)]
    [InlineData("http://[::1]", 43402)]
    public async Task Overlay_page_origin_is_accepted(string originHost, int port)
    {
        await CreateHost(port).StartAsync();

        var error = await TryConnectAsync(port, "/ws/chat", $"{originHost}:{port}");

        error.Should().BeNull("overlay sayfasının kendi kaynağı reddedilmemeli");
    }

    [Fact]
    public async Task Giveaway_socket_accepts_the_overlay_page_origin()
    {
        const int port = 43500;
        await CreateHost(port).StartAsync();

        var error = await TryConnectAsync(port, "/ws/giveaway", $"http://localhost:{port}");

        error.Should().BeNull();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var c in _clients)
        {
            try { c.Abort(); c.Dispose(); } catch { }
        }
        foreach (var h in _hosts)
        {
            try { await h.StopAsync(); } catch { }
        }
    }
}
