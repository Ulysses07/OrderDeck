using MailKit.Net.Smtp;
using MimeKit;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace OrderDeck.LicenseServer.Services.Email;

public sealed class SmtpEmailSender : IEmailSender
{
    private readonly SmtpOptions _opt;
    private readonly ILogger<SmtpEmailSender> _log;

    public SmtpEmailSender(IOptions<SmtpOptions> opt, ILogger<SmtpEmailSender> log)
    {
        _opt = opt.Value;
        _log = log;
    }

    public async Task SendAsync(string toEmail, string toName, string subject,
        string htmlBody, string plainBody, CancellationToken ct = default)
    {
        var msg = new MimeMessage();
        msg.From.Add(new MailboxAddress(_opt.FromName, _opt.FromAddress));
        msg.To.Add(new MailboxAddress(toName, toEmail));
        msg.Subject = subject;
        msg.Body = new BodyBuilder { HtmlBody = htmlBody, TextBody = plainBody }.ToMessageBody();

        try
        {
            using var smtp = new SmtpClient();
            await smtp.ConnectAsync(_opt.Host, _opt.Port,
                _opt.UseSsl ? MailKit.Security.SecureSocketOptions.StartTls
                            : MailKit.Security.SecureSocketOptions.None, ct);
            if (!string.IsNullOrEmpty(_opt.Username))
                await smtp.AuthenticateAsync(_opt.Username, _opt.Password, ct);
            await smtp.SendAsync(msg, ct);
            await smtp.DisconnectAsync(quit: true, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "SMTP send failed for {Email}", toEmail);
            // Email failure is not propagated — caller already returned 202 to client.
        }
    }
}
