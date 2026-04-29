using LiveDeck.LicenseServer.Data;
using LiveDeck.LicenseServer.Domain;
using LiveDeck.LicenseServer.Services.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace LiveDeck.LicenseServer.Services.Auth;

public sealed class EmailConfirmationService
{
    public static readonly TimeSpan TokenLifetime = TimeSpan.FromHours(24);

    private readonly LicenseDbContext _db;
    private readonly IEmailSender _email;
    private readonly string _baseUrl;

    public EmailConfirmationService(LicenseDbContext db, IEmailSender email, IConfiguration cfg)
    {
        _db = db;
        _email = email;
        _baseUrl = cfg["App:PublicBaseUrl"] ?? "https://localhost:5001";
    }

    public async Task IssueAndSendAsync(Customer customer, CancellationToken ct = default)
    {
        var token = new EmailConfirmationToken
        {
            Token = Guid.NewGuid(),
            CustomerId = customer.Id,
            CreatedAt = DateTimeOffset.UtcNow,
            UsedAt = null
        };
        _db.EmailConfirmationTokens.Add(token);
        await _db.SaveChangesAsync(ct);

        var url = $"{_baseUrl}/api/v1/auth/confirm-email/{token.Token}";
        var (subject, html, plain) = EmailTemplates.ConfirmEmail(customer.Name, url);
        await _email.SendAsync(customer.Email, customer.Name, subject, html, plain, ct);
    }

    /// <summary>True = success, false = invalid/expired/used.</summary>
    public async Task<bool> ConsumeAsync(Guid token, CancellationToken ct = default)
    {
        var record = await _db.EmailConfirmationTokens
            .Include(t => t.Customer)
            .FirstOrDefaultAsync(t => t.Token == token, ct);
        if (record is null) return false;
        if (record.UsedAt is not null) return false;
        if (DateTimeOffset.UtcNow - record.CreatedAt > TokenLifetime) return false;

        record.UsedAt = DateTimeOffset.UtcNow;
        record.Customer.EmailConfirmedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return true;
    }
}
