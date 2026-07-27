using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace OrderDeck.LicenseServer.Services.WhatsApp;

/// <summary>
/// Dev/test provider: gerçek API çağrısı yapmaz, sadece loglar ve sahte bir
/// message id döner. <c>OrderDeck:WhatsApp:Provider</c> "cloud" değilse kullanılır.
/// </summary>
public sealed class LogWhatsAppSender : IWhatsAppSender
{
    private readonly ILogger<LogWhatsAppSender> _log;

    public LogWhatsAppSender(ILogger<LogWhatsAppSender> log) => _log = log;

    public Task<WhatsAppSendResult> SendTextAsync(
        WhatsAppSendContext ctx, string toPhoneE164, string text, CancellationToken ct = default)
    {
        _log.LogInformation(
            "[WA-LOG] text → {To} (pnid={Pnid}): {Text}", toPhoneE164, ctx.PhoneNumberId, text);
        return Task.FromResult(WhatsAppSendResult.Success("wa-log-" + Guid.NewGuid().ToString("N")));
    }

    public Task<WhatsAppSendResult> SendTemplateAsync(
        WhatsAppSendContext ctx, string toPhoneE164, WhatsAppTemplate template, CancellationToken ct = default)
    {
        _log.LogInformation(
            "[WA-LOG] template '{Name}' ({Lang}) → {To} (pnid={Pnid}) params=[{Params}]",
            template.Name, template.LanguageCode, toPhoneE164, ctx.PhoneNumberId,
            string.Join(", ", template.BodyParams));
        return Task.FromResult(WhatsAppSendResult.Success("wa-log-" + Guid.NewGuid().ToString("N")));
    }
}
