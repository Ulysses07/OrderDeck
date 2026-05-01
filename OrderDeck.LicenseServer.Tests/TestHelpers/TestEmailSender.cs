using System.Collections.Concurrent;
using OrderDeck.LicenseServer.Services.Email;

namespace OrderDeck.LicenseServer.Tests.TestHelpers;

public sealed class TestEmailSender : IEmailSender
{
    public ConcurrentBag<SentEmail> Sent { get; } = new();

    /// <summary>Override per-test to simulate transient/permanent SMTP failures.
    /// Returning null = success. Otherwise the returned exception is thrown.</summary>
    public Func<string, Exception?>? FailWith { get; set; }

    public Task SendAsync(string toEmail, string toName, string subject,
        string htmlBody, string plainBody, CancellationToken ct = default)
    {
        var ex = FailWith?.Invoke(toEmail);
        if (ex is not null) throw ex;
        Sent.Add(new SentEmail(toEmail, toName, subject, htmlBody, plainBody));
        return Task.CompletedTask;
    }
}

public sealed record SentEmail(string ToEmail, string ToName, string Subject, string HtmlBody, string PlainBody);
