namespace OrderDeck.LicenseServer.Services.Email;

public interface IEmailSender
{
    Task SendAsync(string toEmail, string toName, string subject, string htmlBody, string plainBody, CancellationToken ct = default);
}
