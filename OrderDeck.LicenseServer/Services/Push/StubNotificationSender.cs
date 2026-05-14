using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;

namespace OrderDeck.LicenseServer.Services.Push;

/// <summary>
/// FCM/APNS yapılandırılmadığında devreye giren log-only sender. Device sayısını
/// loglar, gerçek gönderim yapmaz. Üretimde `OrderDeck:Push:Provider = "stub"`
/// kalırsa hiç bildirim atılmaz — gerçek gönderim için FCM impl + service
/// account JSON gerekir (Faz 2).
/// </summary>
public sealed class StubNotificationSender : INotificationSender
{
    private readonly LicenseDbContext _db;
    private readonly ILogger<StubNotificationSender> _log;

    public StubNotificationSender(LicenseDbContext db, ILogger<StubNotificationSender> log)
    {
        _db = db;
        _log = log;
    }

    public async Task SendToCustomerAsync(
        Guid customerId,
        string title,
        string body,
        IReadOnlyDictionary<string, string>? data = null,
        CancellationToken ct = default)
    {
        var deviceCount = await _db.PushDevices
            .CountAsync(d => d.CustomerId == customerId, ct);

        _log.LogInformation(
            "Push[STUB] → customer={CustomerId} devices={DeviceCount} title={Title!} body={Body!}",
            customerId, deviceCount, title, body);
    }
}
