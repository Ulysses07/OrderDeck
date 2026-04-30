using System.Collections.Concurrent;
using LiveDeck.LicenseServer.Services.Email;

namespace LiveDeck.LicenseServer.Tests.TestHelpers;

public sealed class TestEmailSender : IEmailSender
{
    public ConcurrentBag<SentEmail> Sent { get; } = new();

    public Task SendAsync(string toEmail, string toName, string subject,
        string htmlBody, string plainBody, CancellationToken ct = default)
    {
        Sent.Add(new SentEmail(toEmail, toName, subject, htmlBody, plainBody));
        return Task.CompletedTask;
    }
}

public sealed record SentEmail(string ToEmail, string ToName, string Subject, string HtmlBody, string PlainBody);
