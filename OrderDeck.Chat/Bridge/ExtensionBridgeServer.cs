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
    private readonly SpamFilter? _spamFilter;
    private readonly ILogger<ExtensionBridgeServer> _log;
    private readonly HttpListener _listener = new();
    private CancellationTokenSource? _cts;
    private Task? _runner;

    // Active WebSocket connection counter — read by GET /_health so the
    // first-run wizard can confirm the operator's Chrome extension is
    // installed AND connected. Interlocked so the AcceptLoop publisher
    // and the /_health request thread agree without a lock.
    private int _activeWebSocketCount;
    public int ActiveWebSocketCount => Volatile.Read(ref _activeWebSocketCount);

    public int Port { get; private set; }

    public ExtensionBridgeServer(IChatBus bus, int port = 4748,
        ILogger<ExtensionBridgeServer>? log = null,
        ITrialModeProbe? trialProbe = null,
        SpamFilter? spamFilter = null)
    {
        _bus = bus;
        _trialProbe = trialProbe;
        _spamFilter = spamFilter;
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
                // GET /_health → JSON status. Used by the first-run wizard's
                // "Doğrula" button to confirm the Chrome extension is loaded
                // AND connected to the bridge (the WS handshake is what
                // actually proves it; HTTP just exposes the counter).
                if (context.Request.HttpMethod == "GET" &&
                    context.Request.Url?.AbsolutePath == "/_health")
                {
                    HandleHealthRequest(context);
                    continue;
                }
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

    private void HandleHealthRequest(HttpListenerContext context)
    {
        try
        {
            var count = ActiveWebSocketCount;
            var json = JsonSerializer.Serialize(new
            {
                connected = count > 0,
                clientCount = count
            }, JsonOpts);
            var payload = Encoding.UTF8.GetBytes(json);
            context.Response.StatusCode = 200;
            context.Response.ContentType = "application/json; charset=utf-8";
            context.Response.ContentLength64 = payload.Length;
            // Localhost-only so CORS isn't a real risk, but the wizard fetches
            // from a custom HttpClient so wildcard is fine + saves debugging.
            context.Response.Headers["Access-Control-Allow-Origin"] = "*";
            context.Response.OutputStream.Write(payload, 0, payload.Length);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Health endpoint write failed");
        }
        finally
        {
            try { context.Response.Close(); } catch { /* ignore */ }
        }
    }

    private async Task Handle(WebSocket ws, CancellationToken ct)
    {
        Interlocked.Increment(ref _activeWebSocketCount);
        try
        {
            await HandleCore(ws, ct);
        }
        finally
        {
            Interlocked.Decrement(ref _activeWebSocketCount);
        }
    }

    private async Task HandleCore(WebSocket ws, CancellationToken ct)
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

                    // Spam filter — runs AFTER trial mode + payload-shape checks
                    // because the cheaper rules (length, links) reject lots of
                    // messages and we don't want to bother evaluating them when
                    // the message is already going to be dropped for other reasons.
                    if (_spamFilter is not null)
                    {
                        var nowSec = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                        var dropReason = _spamFilter.ShouldDrop(msg.Text, msg.Username, nowSec);
                        if (dropReason is not null)
                        {
                            _log.LogDebug(
                                "Spam filter dropped message ({Reason}) from {Platform}:{Username}: {Text}",
                                dropReason, msg.Platform, msg.Username, msg.Text);
                            continue;
                        }
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
