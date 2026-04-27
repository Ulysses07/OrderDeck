using System.Threading;
using System.Threading.Tasks;
using LiveDeck.Chat.Bridge;
using LiveDeck.Core.Chat;
using Microsoft.Extensions.Logging;

namespace LiveDeck.Chat.Ingestors;

/// <summary>
/// Phase 1 ingestor that simply ensures the <see cref="ExtensionBridgeServer"/> is running.
/// All message decoding happens inside the bridge; this class is a marker for the App to
/// know that Instagram is the active platform and to gate UI accordingly.
/// </summary>
public sealed class InstagramIngestor : IChatIngestor
{
    private readonly ExtensionBridgeServer _bridge;
    private readonly ILogger<InstagramIngestor> _log;

    public string Platform => "instagram";

    public InstagramIngestor(ExtensionBridgeServer bridge, ILogger<InstagramIngestor> log)
    {
        _bridge = bridge;
        _log = log;
    }

    public Task StartAsync(CancellationToken ct)
    {
        _log.LogInformation("InstagramIngestor starting (bridge port {Port})", _bridge.Port);
        return _bridge.StartAsync(ct);
    }

    public Task StopAsync(CancellationToken ct)
    {
        _log.LogInformation("InstagramIngestor stopping");
        return _bridge.StopAsync(ct);
    }
}
