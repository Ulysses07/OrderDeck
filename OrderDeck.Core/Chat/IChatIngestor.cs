using System.Threading;
using System.Threading.Tasks;

namespace OrderDeck.Core.Chat;

public interface IChatIngestor
{
    string Platform { get; }
    Task StartAsync(CancellationToken ct);
    Task StopAsync(CancellationToken ct);
}
