using OrderDeck.LicenseServer.Services.Push;

namespace OrderDeck.LicenseServer.Tests.TestHelpers;

/// <summary>
/// Test push sender — gönderilen tüm bildirim parametrelerini in-memory
/// kaydeder, gerçek FCM/APNS çağrısı yapmaz. Test assertion'ları
/// <see cref="Sent"/> listesi üzerinden yapılır.
/// </summary>
public sealed class RecordingNotificationSender : INotificationSender
{
    public sealed record Notification(
        Guid CustomerId,
        string Title,
        string Body,
        IReadOnlyDictionary<string, string>? Data);

    public sealed record ShopperNotification(
        IReadOnlyCollection<Guid> ShopperIds,
        string Title,
        string Body,
        IReadOnlyDictionary<string, string>? Data);

    private readonly List<Notification> _sent = new();
    private readonly List<ShopperNotification> _sentToShoppers = new();
    private readonly object _lock = new();

    public IReadOnlyList<Notification> Sent
    {
        get { lock (_lock) return _sent.ToList(); }
    }

    public IReadOnlyList<ShopperNotification> SentToShoppers
    {
        get { lock (_lock) return _sentToShoppers.ToList(); }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _sent.Clear();
            _sentToShoppers.Clear();
        }
    }

    public Task SendToCustomerAsync(
        Guid customerId,
        string title,
        string body,
        IReadOnlyDictionary<string, string>? data = null,
        CancellationToken ct = default)
    {
        lock (_lock)
        {
            _sent.Add(new Notification(customerId, title, body, data));
        }
        return Task.CompletedTask;
    }

    public Task SendToShoppersAsync(
        IReadOnlyCollection<Guid> shopperIds,
        string title,
        string body,
        IReadOnlyDictionary<string, string>? data = null,
        CancellationToken ct = default)
    {
        lock (_lock)
        {
            _sentToShoppers.Add(new ShopperNotification(
                shopperIds.ToList(), title, body, data));
        }
        return Task.CompletedTask;
    }
}
