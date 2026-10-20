using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OrderDeck.LicenseServer.Services.Email;

namespace OrderDeck.LicenseServer.Services.Bank;

/// <summary>
/// Banka saklama ve hizmet sonu işlerinin yöneticiye e-postası (KVKK Ö-13; <c>BackupRestoreDrillJob</c> kalıbı, adres
/// <see cref="AlertEmailKey"/>). Adres boşsa yalnız günlüğe uyarı. Gönderim hatası yutulur ve günlüğe düşer: uyarının düşmesi
/// işi düşürmesin. Gövdeye kişisel veri girmez — yalnız iş adı, istisna türü, sayılar, lisans kimliği. Alıcı adresi günlüğe
/// yazılmaz.
/// </summary>
public sealed class BankJobAlerts
{
    public const string AlertEmailKey = "Admin:AlertEmail";

    private readonly IEmailSender _email;
    private readonly IConfiguration _config;
    private readonly ILogger<BankJobAlerts> _log;

    public BankJobAlerts(IEmailSender email, IConfiguration config, ILogger<BankJobAlerts> log)
    { _email = email; _config = config; _log = log; }

    public async Task SendAsync(string subject, string body, CancellationToken ct)
    {
        var to = _config[AlertEmailKey];
        if (string.IsNullOrWhiteSpace(to))
        {
            _log.LogWarning("Banka iş uyarısı gönderilemedi: {Key} boş — {Subject}", AlertEmailKey, subject);
            return;
        }
        try
        {
            var html = $"<pre style=\"font-family:monospace\">{WebUtility.HtmlEncode(body)}</pre>";
            await _email.SendAsync(to, "OrderDeck Admin", subject, html, body, ct);
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
        {
            _log.LogError(ex, "Banka iş uyarısı e-postası gönderilemedi — {Subject}", subject);
        }
    }
}
