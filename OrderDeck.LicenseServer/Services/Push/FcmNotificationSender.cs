using FirebaseAdmin;
using FirebaseAdmin.Messaging;
using Google.Apis.Auth.OAuth2;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Data;

namespace OrderDeck.LicenseServer.Services.Push;

/// <summary>
/// Gerçek FCM gönderici (Push Faz 2, 2026-05-15). Firebase Admin SDK üzerinden
/// service account JSON ile auth eder, ilgili customer'ın tüm PushDevice
/// kayıtlarına paralel gönderim yapar.
///
/// FCM hata kodları:
///   - <see cref="MessagingErrorCode.Unregistered"/> → token artık geçersiz
///     (uygulamadan silinmiş ya da yeniden kurulum), o PushDevice DB'den
///     temizlenir.
///   - <see cref="MessagingErrorCode.InvalidArgument"/> + "registration token"
///     → token format bozuk; aynı şekilde temizlenir.
///   - Diğer hatalar (Internal, Unavailable, QuotaExceeded) → transient,
///     log + skip. Bir sonraki notify retry doğal olarak.
///
/// FirebaseApp init bir kez yapılır (singleton), DI üzerinden FirebaseMessaging
/// instance'ı paylaşılır. Program.cs sırasında FCM provider seçilirse init
/// çalıştırılır; service account JSON path'i bulunamazsa boot başarısız olur.
/// </summary>
public sealed class FcmNotificationSender : INotificationSender
{
    private const string AppName = "orderdeck-fcm";

    private readonly LicenseDbContext _db;
    private readonly FirebaseMessaging _messaging;
    private readonly ILogger<FcmNotificationSender> _log;

    public FcmNotificationSender(
        LicenseDbContext db,
        FirebaseMessaging messaging,
        ILogger<FcmNotificationSender> log)
    {
        _db = db;
        _messaging = messaging;
        _log = log;
    }

    public async Task SendToCustomerAsync(
        Guid customerId,
        string title,
        string body,
        IReadOnlyDictionary<string, string>? data = null,
        CancellationToken ct = default)
    {
        var devices = await _db.PushDevices
            .Where(d => d.CustomerId == customerId)
            .Select(d => new { d.Id, d.PushToken, d.Platform })
            .ToListAsync(ct);

        if (devices.Count == 0)
        {
            _log.LogDebug("Push[FCM] no devices for customer={CustomerId}", customerId);
            return;
        }

        var messages = devices.Select(d => new Message
        {
            Token = d.PushToken,
            Notification = new Notification
            {
                Title = title,
                Body = body
            },
            Data = data?.ToDictionary(kv => kv.Key, kv => kv.Value),
            Android = new AndroidConfig
            {
                Priority = Priority.High,
                Notification = new AndroidNotification
                {
                    ChannelId = "orderdeck-default",
                    DefaultSound = true
                }
            },
            Apns = new ApnsConfig
            {
                Aps = new Aps
                {
                    Sound = "default",
                    ContentAvailable = true
                }
            }
        }).ToList();

        BatchResponse? response;
        try
        {
            response = await _messaging.SendEachAsync(messages, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "Push[FCM] batch send threw for customer={CustomerId} count={Count}",
                customerId, messages.Count);
            return;
        }

        // Token-level result handling. Stale ones get removed.
        var staleDeviceIds = new List<Guid>();
        for (var i = 0; i < response.Responses.Count; i++)
        {
            var r = response.Responses[i];
            if (r.IsSuccess) continue;

            if (IsStaleTokenError(r.Exception?.MessagingErrorCode, r.Exception?.Message))
            {
                staleDeviceIds.Add(devices[i].Id);
            }
            else
            {
                _log.LogWarning(
                    "Push[FCM] transient error for device={DeviceId} code={Code} msg={Msg}",
                    devices[i].Id, r.Exception?.MessagingErrorCode, r.Exception?.Message);
            }
        }

        if (staleDeviceIds.Count > 0)
        {
            await _db.PushDevices
                .Where(d => staleDeviceIds.Contains(d.Id))
                .ExecuteDeleteAsync(ct);
            _log.LogInformation(
                "Push[FCM] removed {Count} stale tokens for customer={CustomerId}",
                staleDeviceIds.Count, customerId);
        }

        _log.LogInformation(
            "Push[FCM] customer={CustomerId} success={Success} failure={Failure} stale={Stale}",
            customerId, response.SuccessCount, response.FailureCount, staleDeviceIds.Count);
    }

    public async Task SendToShoppersAsync(
        IReadOnlyCollection<Guid> shopperIds,
        string title,
        string body,
        IReadOnlyDictionary<string, string>? data = null,
        CancellationToken ct = default)
    {
        if (shopperIds.Count == 0) return;

        var devices = await _db.ShopperPushDevices
            .Where(d => shopperIds.Contains(d.ShopperId))
            .Select(d => new { d.Id, d.PushToken, d.Platform })
            .ToListAsync(ct);

        if (devices.Count == 0)
        {
            _log.LogDebug("Push[FCM] no devices for {Count} shoppers", shopperIds.Count);
            return;
        }

        var messages = devices.Select(d => new Message
        {
            Token = d.PushToken,
            Notification = new Notification { Title = title, Body = body },
            Data = data?.ToDictionary(kv => kv.Key, kv => kv.Value),
            Android = new AndroidConfig
            {
                Priority = Priority.High,
                Notification = new AndroidNotification
                {
                    ChannelId = "orderdeck-shopper",
                    DefaultSound = true
                }
            },
            Apns = new ApnsConfig
            {
                Aps = new Aps { Sound = "default", ContentAvailable = true }
            }
        }).ToList();

        BatchResponse? response;
        try
        {
            response = await _messaging.SendEachAsync(messages, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "Push[FCM] shopper batch send threw count={Count}", messages.Count);
            return;
        }

        var staleDeviceIds = new List<Guid>();
        for (var i = 0; i < response.Responses.Count; i++)
        {
            var r = response.Responses[i];
            if (r.IsSuccess) continue;
            if (IsStaleTokenError(r.Exception?.MessagingErrorCode, r.Exception?.Message))
                staleDeviceIds.Add(devices[i].Id);
            else
                _log.LogWarning(
                    "Push[FCM] shopper transient error device={DeviceId} code={Code} msg={Msg}",
                    devices[i].Id, r.Exception?.MessagingErrorCode, r.Exception?.Message);
        }

        if (staleDeviceIds.Count > 0)
        {
            await _db.ShopperPushDevices
                .Where(d => staleDeviceIds.Contains(d.Id))
                .ExecuteDeleteAsync(ct);
            _log.LogInformation(
                "Push[FCM] removed {Count} stale shopper tokens", staleDeviceIds.Count);
        }

        _log.LogInformation(
            "Push[FCM] shoppers={ShopperCount} success={Success} failure={Failure} stale={Stale}",
            shopperIds.Count, response.SuccessCount, response.FailureCount, staleDeviceIds.Count);
    }

    /// <summary>
    /// FCM hata kodlarından "bu token artık geçersiz, sil" anlamına gelenleri
    /// ayırt eder. Unregistered + SenderIdMismatch açıkça stale; InvalidArgument
    /// için mesaj içeriği "registration token" geçiyorsa kabul edilir
    /// (bazen FCM yanlış formatlı token için bu kodu döner).
    /// </summary>
    public static bool IsStaleTokenError(MessagingErrorCode? code, string? message)
    {
        if (code == MessagingErrorCode.Unregistered) return true;
        if (code == MessagingErrorCode.SenderIdMismatch) return true;
        if (code == MessagingErrorCode.InvalidArgument &&
            message is not null &&
            message.Contains("registration token", StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }

    /// <summary>
    /// FirebaseApp singleton init. Program.cs FCM provider seçildiğinde çağırır.
    /// JSON path bulunamazsa fırlatır → boot fails fast.
    /// </summary>
    public static FirebaseMessaging InitializeMessaging(FcmOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.ServiceAccountJsonPath))
            throw new InvalidOperationException(
                "OrderDeck:Push:Fcm:ServiceAccountJsonPath boş — FCM provider için zorunlu.");

        if (!File.Exists(options.ServiceAccountJsonPath))
            throw new FileNotFoundException(
                $"FCM service account JSON bulunamadı: {options.ServiceAccountJsonPath}");

        // Already-initialized koruması: testler birden çok yaratabilir.
        var existing = FirebaseApp.GetInstance(AppName);
        if (existing is null)
        {
            FirebaseApp.Create(new AppOptions
            {
                Credential = GoogleCredential.FromFile(options.ServiceAccountJsonPath)
            }, AppName);
        }

        return FirebaseMessaging.GetMessaging(FirebaseApp.GetInstance(AppName));
    }
}
