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
    public sealed record Message(string Phone, string Text, SmsKind Kind);

    private readonly List<Message> _sent = new();
    private readonly object _lock = new();

    public bool ThrowOnSend { get; set; }

    /// <summary>Kayıt yapıldıktan sonra çağrılır. Gönderim ortasında durum
    /// değiştiren testler için — job'a sahte bir gecikme/olay enjekte etmenin
    /// tek dürüst yolu, gerçek gönderim noktasına bağlanmak.</summary>
    public Action<Message>? OnSent { get; set; }

    public IReadOnlyList<Message> Sent
    {
        get { lock (_lock) return _sent.ToList(); }
    }

    public void Clear()
    {
        lock (_lock) _sent.Clear();
    }

    public Task SendAsync(string toPhone, string message, SmsKind kind, CancellationToken ct = default)
    {
        if (ThrowOnSend)
            throw new InvalidOperationException("Simulated SMS provider failure.");
        var msg = new Message(toPhone, message, kind);
        lock (_lock) _sent.Add(msg);
        // Kanca kilidin DIŞINDA çağrılır: testler bu kancanın içinden ayrı bir
        // scope açıp DB'ye yazıyor. Kilit tutulurken DB'ye gitmek, aynı
        // fixture'ı paylaşan başka bir testin Sent okumasını bekletirdi.
        OnSent?.Invoke(msg);
        return Task.CompletedTask;
    }
}
