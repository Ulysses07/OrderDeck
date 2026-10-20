using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OrderDeck.LicenseServer.Services.Email;

namespace OrderDeck.LicenseServer.Services.Bank;

/// <summary>
/// Banka saklama ve hizmet sonu işlerinin yöneticiye e-postası (KVKK Ö-13; <c>BackupRestoreDrillJob</c> kalıbı, adres
/// <see cref="AlertEmailKey"/>). İki yol:
/// <list type="bullet">
/// <item><see cref="SendAsync"/> — uyarı: adres boşsa yalnız günlüğe uyarı, gönderim hatası yutulur ve günlüğe düşer; uyarının
/// düşmesi işi düşürmesin.</item>
/// <item><see cref="SendOrThrowAsync"/> — teslimi şart olan e-posta (imha özeti, <c>DataDestructionDigestJob</c>): adres boşsa ya
/// da gönderim düşerse fırlatır; çağıran işini Failed bırakır ve gönderilmemiş sayıp yeniden dener.</item>
/// </list>
/// Gövdeye kişisel veri girmez — yalnız iş adı, istisna türü, sayılar, kimlikler. Alıcı adresi günlüğe ve istisnaya yazılmaz.
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
            await SendToAsync(to, subject, body, ct);
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
        {
            _log.LogError(ex, "Banka iş uyarısı e-postası gönderilemedi — {Subject}", subject);
        }
    }

    /// <summary>Teslimi şart olan e-posta: <see cref="AlertEmailKey"/> boşsa <see cref="InvalidOperationException"/>, gönderim
    /// hatası olduğu gibi çağırana çıkar.</summary>
    public Task SendOrThrowAsync(string subject, string body, CancellationToken ct)
    {
        var to = _config[AlertEmailKey];
        if (string.IsNullOrWhiteSpace(to))
            throw new InvalidOperationException($"{AlertEmailKey} boş: e-posta gönderilemedi — {subject}");
        return SendToAsync(to, subject, body, ct);
    }

    private Task SendToAsync(string to, string subject, string body, CancellationToken ct)
    {
        var html = $"<pre style=\"font-family:monospace\">{WebUtility.HtmlEncode(body)}</pre>";
        return _email.SendAsync(to, "OrderDeck Admin", subject, html, body, ct);
    }
}
