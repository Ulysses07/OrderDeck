using Microsoft.Extensions.Logging;
using OrderDeck.LicenseServer.Services.Observability;

namespace OrderDeck.LicenseServer.Services.Sms;

/// <summary>
/// Dev/test SMS gönderici — gerçek SMS atmaz, mesajı log'a yazar.
/// Email tarafındaki <c>DiskEmailSender</c> muadili. Sms:Provider=log iken
/// DI'a bağlanır; lokal geliştirmede OTP kodunu log'dan okuyabilmek için
/// telefon maskelenir ama mesaj (kod dahil) tam yazılır.
/// </summary>
public sealed class LogSmsSender : ISmsSender
{
    private readonly ILogger<LogSmsSender> _log;

    public LogSmsSender(ILogger<LogSmsSender> log) => _log = log;

    public Task SendAsync(string toPhone, string message, CancellationToken ct = default)
    {
        _log.LogInformation("SMS (log provider) to {Phone}: {Message}",
            PiiMasker.MaskPhone(toPhone), message);
        return Task.CompletedTask;
    }
}
