using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LiveDeck.Core.Chat;
using LiveDeck.Overlay.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LiveDeck.Overlay;

/// <summary>
/// Hosts the OBS Browser Source endpoints. Started/stopped by the WPF App.
///   * GET  /overlay/chat       → static HTML page bundled via wwwroot
///   * WS   /ws/chat            → live ChatMessage stream
/// </summary>
public sealed class OverlayHost : IAsyncDisposable
{
    private readonly IChatBus _bus;
    private readonly ILogger<OverlayHost> _log;
    private WebApplication? _app;
    private readonly ConcurrentDictionary<Guid, WebSocket> _clients = new();
    private IDisposable? _busSub;

    public int Port { get; private set; }

    public OverlayHost(IChatBus bus, int port = 4747, ILogger<OverlayHost>? log = null)
    {
        _bus = bus;
        _log = log ?? NullLogger<OverlayHost>.Instance;
        Port = port;
    }

    public async Task StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://localhost:{Port}");

        _app = builder.Build();
        _app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });

        var wwwroot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        if (Directory.Exists(wwwroot))
        {
            _app.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = new PhysicalFileProvider(wwwroot)
            });
        }

        _app.MapGet("/overlay/chat", async (HttpContext ctx) =>
        {
            ctx.Response.ContentType = "text/html; charset=utf-8";
            var html = Path.Combine(wwwroot, "chat.html");
            await ctx.Response.SendFileAsync(html);
        });

        _app.Map("/ws/chat", async (HttpContext ctx) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = 400;
                return;
            }
            using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
            await HandleClient(ws, ctx.RequestAborted);
        });

        _busSub = _bus.Subscribe(BroadcastChatMessage);

        await _app.StartAsync();
        _log.LogInformation("OverlayHost listening on http://localhost:{Port}", Port);
    }

    public async Task StopAsync()
    {
        _busSub?.Dispose();
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
            _app = null;
        }
    }

    private async Task HandleClient(WebSocket ws, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        _clients.TryAdd(id, ws);
        try
        {
            // Send snapshot of recent messages
            var snapshot = new OverlayEvent("chat.snapshot", new ChatSnapshotEvent(
                BuildSnapshot()));
            await SendJson(ws, snapshot, ct);

            var buf = new byte[1024];
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var res = await ws.ReceiveAsync(buf, ct);
                if (res.MessageType == WebSocketMessageType.Close) break;
            }
        }
        catch (OperationCanceledException) { /* normal shutdown */ }
        catch (Exception ex) { _log.LogWarning(ex, "Overlay client error"); }
        finally
        {
            _clients.TryRemove(id, out _);
            try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); }
            catch { /* ignore */ }
        }
    }

    private System.Collections.Generic.IReadOnlyList<ChatMessageEvent> BuildSnapshot()
    {
        var recent = _bus.RecentMessages();
        var list = new System.Collections.Generic.List<ChatMessageEvent>(recent.Count);
        foreach (var m in recent)
            list.Add(new ChatMessageEvent(m.Id, m.Platform, m.Username, m.DisplayName,
                m.AvatarUrl, m.Text, m.ReceivedAt));
        return list;
    }

    private void BroadcastChatMessage(ChatMessage m)
    {
        var evt = new OverlayEvent("chat.message",
            new ChatMessageEvent(m.Id, m.Platform, m.Username, m.DisplayName,
                m.AvatarUrl, m.Text, m.ReceivedAt));
        var json = JsonSerializer.Serialize(evt);
        var bytes = Encoding.UTF8.GetBytes(json);

        foreach (var (id, ws) in _clients)
        {
            if (ws.State != WebSocketState.Open) continue;
            _ = SendBytes(ws, bytes, CancellationToken.None);
        }
    }

    private static async Task SendJson(WebSocket ws, object payload, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(payload);
        await SendBytes(ws, Encoding.UTF8.GetBytes(json), ct);
    }

    private static async Task SendBytes(WebSocket ws, byte[] bytes, CancellationToken ct)
    {
        try
        {
            await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
        }
        catch { /* swallow per-client errors so one slow client doesn't kill the broadcast */ }
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
