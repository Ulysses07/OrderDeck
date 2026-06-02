using OrderDeck.LicenseServer.Services.Sms;

namespace OrderDeck.LicenseServer.Tests.TestHelpers;

/// <summary>
/// Test SMS gönderici — gerçek Netgsm çağrısı yapmaz, gönderilen (telefon,
/// mesaj) çiftlerini in-memory kaydeder. Assertion'lar <see cref="Sent"/>
/// üzerinden. <see cref="ThrowOnSend"/> true ise SMS sağlayıcı arızasını
/// simüle eder (best-effort/escalate path testleri için).
/// </summary>
public sealed class RecordingSmsSender : ISmsSender
{
    public sealed record Message(string Phone, string Text);

    private readonly List<Message> _sent = new();
    private readonly object _lock = new();

    public bool ThrowOnSend { get; set; }

    public IReadOnlyList<Message> Sent
    {
        get { lock (_lock) return _sent.ToList(); }
    }

    public void Clear()
    {
        lock (_lock) _sent.Clear();
    }

    public Task SendAsync(string toPhone, string message, CancellationToken ct = default)
    {
        if (ThrowOnSend)
            throw new InvalidOperationException("Simulated SMS provider failure.");
        lock (_lock) _sent.Add(new Message(toPhone, message));
        return Task.CompletedTask;
    }
}
