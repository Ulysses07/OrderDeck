using System.Threading;
using System.Threading.Tasks;

namespace LiveDeck.Core.Chat;

public interface IChatIngestor
{
    string Platform { get; }
    Task StartAsync(CancellationToken ct);
    Task StopAsync(CancellationToken ct);
}
