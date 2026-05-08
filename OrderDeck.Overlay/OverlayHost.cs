using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OrderDeck.Core.Chat;
using OrderDeck.Core.Sales;
using OrderDeck.Overlay.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace OrderDeck.Overlay;

/// <summary>Snapshot of audio settings captured at broadcast time.</summary>
/// <remarks>
/// Kept in the Overlay assembly (not OrderDeck.Core) because it is a
/// presentation/wire concern, not a domain concept.
/// </remarks>
public sealed record GiveawayAudioSnapshot(double Volume, bool Muted);

/// <summary>
/// Hosts the OBS Browser Source endpoints. Started/stopped by the WPF App.
///   * GET  /overlay/chat       → static HTML page bundled via wwwroot
///   * WS   /ws/chat            → live ChatMessage stream
///   * GET  /overlay/giveaway   → static HTML page for giveaway roulette
///   * WS   /ws/giveaway        → live giveaway event stream
/// </summary>
public sealed class OverlayHost : IAsyncDisposable
{
    private readonly IChatBus _bus;
    private readonly GiveawayService _giveaway;
    private readonly ILogger<OverlayHost> _log;
    private readonly Func<GiveawayAudioSnapshot> _audioProvider;

    // Wire serialization. The overlay JS clients (chat.js, giveaway.js,
    // preview.js) all consume camelCase fields — `evt.type`, `evt.data`,
    // `msg.displayName`, `evt.data.recentMessages`, etc. Without this
    // option System.Text.Json emits the C# PascalCase property names
    // (`Type`, `Data`, `DisplayName`) and every JS branch silently
    // misses, so messages come over the WS but never make it to the DOM.
    // First reproed on /overlay/chat showing zero messages with a healthy
    // bus + bridge.
    private static readonly JsonSerializerOptions WireJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
    private WebApplication? _app;
    private readonly ConcurrentDictionary<Guid, WebSocket> _chatClients = new();
    private readonly ConcurrentDictionary<Guid, WebSocket> _giveawayClients = new();
    private IDisposable? _busSub;
    private Action<GiveawayStartedEvent>? _onStarted;
    private Action<GiveawayParticipantEvent>? _onParticipant;
    private Action<GiveawayWinnersDrawnEvent>? _onWinners;
    private Action<GiveawayCancelledEvent>? _onCancelled;

    /// <summary>
    /// Wire-only payload for the `giveaway.started` JSON event. Mirrors
    /// <see cref="GiveawayStartedEvent"/> + adds audio fields the overlay
    /// needs at broadcast time. Audio is presentation, not domain — it
    /// lives here, not in OrderDeck.Core.
    /// </summary>
    private sealed record GiveawayStartedWirePayload(
        string GiveawayId,
        string Keyword,
        int WinnerCount,
        int DurationSeconds,
        long StartedAt,
        string AnimationId,
        double AudioVolume,
        bool AudioMuted);

    public int Port { get; private set; }

    public OverlayHost(
        IChatBus bus,
        GiveawayService giveaway,
        int port = 4747,
        ILogger<OverlayHost>? log = null,
        Func<GiveawayAudioSnapshot>? audioProvider = null)
    {
        _bus = bus;
        _giveaway = giveaway;
        _log = log ?? NullLogger<OverlayHost>.Instance;
        Port = port;
        _audioProvider = audioProvider ?? (() => new GiveawayAudioSnapshot(0.7, false));
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
            await ctx.Response.SendFileAsync(Path.Combine(wwwroot, "chat.html"));
        });

        _app.MapGet("/overlay/giveaway", async (HttpContext ctx) =>
        {
            ctx.Response.ContentType = "text/html; charset=utf-8";
            await ctx.Response.SendFileAsync(Path.Combine(wwwroot, "giveaway.html"));
        });

        _app.MapGet("/overlay/preview", async (HttpContext ctx) =>
        {
            ctx.Response.ContentType = "text/html; charset=utf-8";
            await ctx.Response.SendFileAsync(Path.Combine(wwwroot, "preview.html"));
        });

        // Diagnostic page — imports + inits all 10 plugins, shows pass/fail
        // table inline. Use when an animation looks broken: open
        // http://localhost:<port>/overlay/diagnose and see the exact error
        // for each plugin (no DevTools needed).
        _app.MapGet("/overlay/diagnose", async (HttpContext ctx) =>
        {
            ctx.Response.ContentType = "text/html; charset=utf-8";
            await ctx.Response.SendFileAsync(Path.Combine(wwwroot, "diagnose.html"));
        });

        _app.Map("/ws/chat", async (HttpContext ctx) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
            using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
            await HandleChatClient(ws, ctx.RequestAborted);
        });

        _app.Map("/ws/giveaway", async (HttpContext ctx) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
            using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
            await HandleGiveawayClient(ws, ctx.RequestAborted);
        });

        _busSub = _bus.Subscribe(BroadcastChatMessage);

        _onStarted = e =>
        {
            var audio = _audioProvider();
            var wire = new GiveawayStartedWirePayload(
                e.GiveawayId, e.Keyword, e.WinnerCount, e.DurationSeconds,
                e.StartedAt, e.AnimationId, audio.Volume, audio.Muted);
            BroadcastGiveaway("giveaway.started", wire);
        };
        _onParticipant = e => BroadcastGiveaway("giveaway.participant", e);
        _onWinners = e => BroadcastGiveaway("giveaway.winners.drawn", e);
        _onCancelled = e => BroadcastGiveaway("giveaway.cancelled", e);

        _giveaway.Started += _onStarted;
        _giveaway.ParticipantAdded += _onParticipant;
        _giveaway.WinnersDrawn += _onWinners;
        _giveaway.Cancelled += _onCancelled;

        await _app.StartAsync();
        _log.LogInformation("OverlayHost listening on http://localhost:{Port}", Port);
    }

    public async Task StopAsync()
    {
        _busSub?.Dispose();
        if (_onStarted is not null) _giveaway.Started -= _onStarted;
        if (_onParticipant is not null) _giveaway.ParticipantAdded -= _onParticipant;
        if (_onWinners is not null) _giveaway.WinnersDrawn -= _onWinners;
        if (_onCancelled is not null) _giveaway.Cancelled -= _onCancelled;

        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
            _app = null;
        }
    }

    private async Task HandleChatClient(WebSocket ws, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        _chatClients.TryAdd(id, ws);
        try
        {
            var snapshot = new OverlayEvent("chat.snapshot", new ChatSnapshotEvent(BuildChatSnapshot()));
            await SendJson(ws, snapshot, ct);
            await PumpReceiveLoop(ws, ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log.LogWarning(ex, "Overlay chat client error"); }
        finally
        {
            _chatClients.TryRemove(id, out _);
            try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); } catch { }
        }
    }

    private async Task HandleGiveawayClient(WebSocket ws, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        _giveawayClients.TryAdd(id, ws);
        try
        {
            // If a giveaway is already active, send a "started" snapshot so a late-joining overlay catches up.
            var active = _giveaway.Active;
            if (active is not null)
            {
                var audio = _audioProvider();
                var startedEvt = new OverlayEvent("giveaway.started",
                    new GiveawayStartedWirePayload(
                        active.Id, active.Keyword, active.WinnerCount, active.DurationSeconds,
                        active.StartedAt, active.AnimationId,
                        audio.Volume, audio.Muted));
                await SendJson(ws, startedEvt, ct);

                // Late-joining overlay should also see current participant count, not 0.
                var count = _giveaway.GetActiveParticipantCount();
                if (count > 0)
                {
                    var countEvt = new OverlayEvent("giveaway.participant", new GiveawayParticipantEvent(
                        active.Id,
                        Username: "",
                        DisplayName: null,
                        AvatarUrl: null,
                        Platform: "",
                        TotalCount: count));
                    await SendJson(ws, countEvt, ct);
                }
            }
            await PumpReceiveLoop(ws, ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log.LogWarning(ex, "Overlay giveaway client error"); }
        finally
        {
            _giveawayClients.TryRemove(id, out _);
            try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); } catch { }
        }
    }

    private static async Task PumpReceiveLoop(WebSocket ws, CancellationToken ct)
    {
        var buf = new byte[1024];
        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var res = await ws.ReceiveAsync(buf, ct);
            if (res.MessageType == WebSocketMessageType.Close) break;
        }
    }

    private System.Collections.Generic.IReadOnlyList<ChatMessageEvent> BuildChatSnapshot()
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
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(evt, WireJson));
        // Snapshot the value collection before iterating: enumerating a
        // ConcurrentDictionary is technically allowed but a concurrent
        // TryRemove (from HandleChatClient.finally on a closing socket)
        // can yield stale or duplicate entries to the broadcast loop.
        // Capturing once also frees the broadcast thread to fire SendBytes
        // without the dictionary's read lock held under load.
        foreach (var ws in _chatClients.Values.ToArray())
        {
            if (ws.State != WebSocketState.Open) continue;
            _ = SendBytes(ws, bytes, CancellationToken.None);
        }
    }

    private void BroadcastGiveaway(string type, object data)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new OverlayEvent(type, data), WireJson));
        // Same snapshot rationale as BroadcastChatMessage above.
        foreach (var ws in _giveawayClients.Values.ToArray())
        {
            if (ws.State != WebSocketState.Open) continue;
            _ = SendBytes(ws, bytes, CancellationToken.None);
        }
    }

    private static async Task SendJson(WebSocket ws, object payload, CancellationToken ct)
    {
        await SendBytes(ws, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, WireJson)), ct);
    }

    private static async Task SendBytes(WebSocket ws, byte[] bytes, CancellationToken ct)
    {
        try { await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct); }
        catch { /* swallow per-client errors so one slow client doesn't kill the broadcast */ }
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
