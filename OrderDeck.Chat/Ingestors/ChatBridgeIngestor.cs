using System.Threading;
using System.Threading.Tasks;
using LiveDeck.Chat.Bridge;
using LiveDeck.Core.Chat;
using Microsoft.Extensions.Logging;

namespace LiveDeck.Chat.Ingestors;

/// <summary>
/// Platform-agnostic ingestor that starts/stops the <see cref="ExtensionBridgeServer"/>.
/// All decoded chat events flow through the same bridge regardless of platform.
/// </summary>
public sealed class ChatBridgeIngestor : IChatIngestor
{
    private readonly ExtensionBridgeServer _bridge;
    private readonly ILogger<ChatBridgeIngestor> _log;

    /// <summary>Always "all" — the bridge multiplexes platforms.</summary>
    public string Platform => "all";

    public ChatBridgeIngestor(ExtensionBridgeServer bridge, ILogger<ChatBridgeIngestor> log)
    {
        _bridge = bridge;
        _log = log;
    }

    public Task StartAsync(CancellationToken ct)
    {
        _log.LogInformation("ChatBridgeIngestor starting (bridge port {Port})", _bridge.Port);
        return _bridge.StartAsync(ct);
    }

    public Task StopAsync(CancellationToken ct)
    {
        _log.LogInformation("ChatBridgeIngestor stopping");
        return _bridge.StopAsync(ct);
    }
}
