using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LiveDeck.LicenseServer.Services.Email;

public sealed class DiskEmailSender : IEmailSender
{
    private readonly SmtpOptions _opt;
    private readonly ILogger<DiskEmailSender> _log;

    public DiskEmailSender(IOptions<SmtpOptions> opt, ILogger<DiskEmailSender> log)
    {
        _opt = opt.Value;
        _log = log;
    }

    public Task SendAsync(string toEmail, string toName, string subject,
        string htmlBody, string plainBody, CancellationToken ct = default)
    {
        Directory.CreateDirectory(_opt.DiskOutputDirectory);
        var filename = $"{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}.eml";
        var path = Path.Combine(_opt.DiskOutputDirectory, filename);

        var sb = new StringBuilder();
        sb.AppendLine($"From: {_opt.FromName} <{_opt.FromAddress}>");
        sb.AppendLine($"To: {toName} <{toEmail}>");
        sb.AppendLine($"Subject: {subject}");
        sb.AppendLine($"Date: {DateTime.UtcNow:R}");
        sb.AppendLine("Content-Type: text/html; charset=utf-8");
        sb.AppendLine();
        sb.AppendLine(htmlBody);
        sb.AppendLine();
        sb.AppendLine("---PLAIN---");
        sb.AppendLine(plainBody);

        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        _log.LogInformation("Email written to {Path}", path);
        return Task.CompletedTask;
    }
}
