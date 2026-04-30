using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace OrderDeck.LicenseServer.Services.Email;

/// <summary>
/// Single send pipeline for all Phase 4e emails. Handles: customer lookup,
/// unsubscribe check, EmailLog dedup, unsubscribe URL signing, IEmailSender
/// delegation, EmailLog persist (with success/error indicator).
/// </summary>
public sealed class EmailSendCoordinator
{
    private readonly LicenseDbContext _db;
    private readonly IEmailSender _sender;
    private readonly UnsubscribeTokenSigner _signer;
    private readonly string _publicBaseUrl;
    private readonly ILogger<EmailSendCoordinator> _log;

    public EmailSendCoordinator(
        LicenseDbContext db,
        IEmailSender sender,
        UnsubscribeTokenSigner signer,
        IConfiguration config,
        ILogger<EmailSendCoordinator> log)
    {
        _db = db;
        _sender = sender;
        _signer = signer;
        _publicBaseUrl = config["App:PublicBaseUrl"]?.TrimEnd('/') ?? "https://localhost:5001";
        _log = log;
    }

    public async Task<bool> TrySendAsync(
        Guid customerId,
        string templateKey,
        string? contextKey,
        Func<Customer, string?, (string Subject, string Html, string Plain)> templateBuilder,
        bool requiresUnsubscribeRespect,
        CancellationToken ct = default)
    {
        var customer = await _db.Customers.FirstOrDefaultAsync(c => c.Id == customerId, ct);
        if (customer is null)
        {
            _log.LogWarning("Email skip: customer {CustomerId} not found (template={Template})", customerId, templateKey);
            return false;
        }

        if (requiresUnsubscribeRespect && customer.Unsubscribed)
        {
            _log.LogInformation("Email skip: customer {CustomerId} unsubscribed (template={Template})", customerId, templateKey);
            return false;
        }

        // Dedup: aynı (customerId, templateKey, contextKey) için successful log varsa skip
        var existing = await _db.EmailLogs
            .Where(e => e.CustomerId == customerId
                     && e.TemplateKey == templateKey
                     && e.ContextKey == contextKey
                     && e.Error == null)
            .FirstOrDefaultAsync(ct);
        if (existing is not null)
        {
            _log.LogDebug("Email skip: dedup hit (customer={CustomerId}, template={Template}, ctx={Ctx})",
                customerId, templateKey, contextKey);
            return false;
        }

        // Unsubscribe URL üret (sadece respect required ise)
        string? unsubscribeUrl = null;
        if (requiresUnsubscribeRespect)
        {
            var token = _signer.Sign(customerId, DateTimeOffset.UtcNow);
            unsubscribeUrl = $"{_publicBaseUrl}/unsubscribe?token={Uri.EscapeDataString(token)}";
        }

        // Template content üret
        var (subject, html, plain) = templateBuilder(customer, unsubscribeUrl);

        // Send (failure swallow — log + persist)
        string? error = null;
        try
        {
            await _sender.SendAsync(customer.Email, customer.Name, subject, html, plain, ct);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            _log.LogWarning(ex, "Email send failed (customer={CustomerId}, template={Template})", customerId, templateKey);
        }

        // EmailLog persist (success or failure)
        _db.EmailLogs.Add(new EmailLog
        {
            Id = Guid.NewGuid(),
            CustomerId = customerId,
            TemplateKey = templateKey,
            ContextKey = contextKey,
            SentAt = DateTimeOffset.UtcNow,
            Error = error
        });
        await _db.SaveChangesAsync(ct);

        return error is null;
    }
}
