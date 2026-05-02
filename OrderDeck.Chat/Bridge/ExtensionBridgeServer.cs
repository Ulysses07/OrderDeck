using System;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OrderDeck.Core.Chat;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace OrderDeck.Chat.Bridge;

/// <summary>
/// Hosts a localhost WebSocket endpoint (`ws://localhost:{port}/extension`) that the browser
/// extension content scripts connect to. Each incoming JSON payload is parsed as an
/// <see cref="ExtensionMessage"/>; "chat" messages are forwarded to the supplied
/// <see cref="IChatBus"/>.
/// </summary>
public sealed class ExtensionBridgeServer : IAsyncDisposable
{
    // Static so we don't recreate the options per-message — used to be inside
    // Handle()'s parse loop, allocating on every chat row.
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly IChatBus _bus;
    private readonly ITrialModeProbe? _trialProbe;
    private readonly ILogger<ExtensionBridgeServer> _log;
    private readonly HttpListener _listener = new();
    private CancellationTokenSource? _cts;
    private Task? _runner;

    public int Port { get; private set; }

    public ExtensionBridgeServer(IChatBus bus, int port = 4748,
        ILogger<ExtensionBridgeServer>? log = null,
        ITrialModeProbe? trialProbe = null)
    {
        _bus = bus;
        _trialProbe = trialProbe;
        _log = log ?? NullLogger<ExtensionBridgeServer>.Instance;
        Port = port == 0 ? FindFreePort() : port;
        _listener.Prefixes.Add($"http://localhost:{Port}/");
    }

    public Task StartAsync(CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _listener.Start();
        _runner = Task.Run(() => AcceptLoop(_cts.Token), _cts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        _cts?.Cancel();
        try { _listener.Stop(); } catch { /* ignore */ }
        if (_runner is not null)
            try { await _runner; } catch { /* ignore */ }
    }

    private async Task AcceptLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext context;
            try { context = await _listener.GetContextAsync(); }
            catch { return; }

            if (!context.Request.IsWebSocketRequest)
            {
                context.Response.StatusCode = 400;
                context.Response.Close();
                continue;
            }

            var wsContext = await context.AcceptWebSocketAsync(subProtocol: null);
            // Fire-and-forget per-connection handler. ContinueWith logs any
            // unobserved exception so we don't lose runtime errors silently —
            // Handle has its own per-frame catch but scheduling/setup errors
            // would otherwise vanish into the unobserved-task stream.
            _ = Task.Run(() => Handle(wsContext.WebSocket, ct), ct)
                .ContinueWith(t => _log.LogError(t.Exception, "Extension WS handler crashed"),
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
        }
    }

    private async Task Handle(WebSocket ws, CancellationToken ct)
    {
        var buf = new byte[8192];
        var ms = new System.IO.MemoryStream();
        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            ms.SetLength(0);
            WebSocketReceiveResult res;
            try
            {
                do
                {
                    res = await ws.ReceiveAsync(buf, ct);
                    ms.Write(buf, 0, res.Count);
                } while (!res.EndOfMessage);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Extension WS receive failed");
                break;
            }

            if (res.MessageType == WebSocketMessageType.Close)
            {
                if (ws.State == WebSocketState.CloseReceived)
                    await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", ct);
                break;
            }

            try
            {
                var json = Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
                var msg = JsonSerializer.Deserialize<ExtensionMessage>(json, JsonOpts);

                if (msg is { Type: "chat", Platform: not null, Username: not null, Text: not null })
                {
                    if (_trialProbe?.IsTrialMode == true &&
                        !string.Equals(msg.Platform, "instagram", StringComparison.OrdinalIgnoreCase))
                    {
                        _log.LogDebug(
                            "Trial mode: dropping non-Instagram message from platform '{Platform}' by {Username}",
                            msg.Platform, msg.Username);
                        continue;
                    }

                    _bus.Publish(new ChatMessage(
                        Id: Guid.NewGuid().ToString("N"),
                        Platform: msg.Platform,
                        ExternalId: msg.ExternalId,
                        Username: msg.Username,
                        DisplayName: msg.DisplayName,
                        AvatarUrl: msg.AvatarUrl,
                        Text: msg.Text,
                        ReceivedAt: msg.Timestamp ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                        Badges: Array.Empty<string>()));
                }
            }
            catch (JsonException ex)
            {
                _log.LogWarning(ex, "Bad extension payload");
            }
        }
    }

    private static int FindFreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None);
        if (_listener.IsListening) _listener.Close();
    }
}
