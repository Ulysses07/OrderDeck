using LiveDeck.LicenseServer.Data;
using LiveDeck.LicenseServer.Domain;
using LiveDeck.LicenseServer.Services.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace LiveDeck.LicenseServer.Services.Auth;

public enum PasswordResetResult { Success, TokenInvalid, PasswordTooShort }

public sealed class PasswordResetService
{
    public static readonly TimeSpan TokenLifetime = TimeSpan.FromHours(1);
    public static readonly TimeSpan RequestThrottle = TimeSpan.FromMinutes(15);

    private readonly LicenseDbContext _db;
    private readonly PasswordHasher _hasher;
    private readonly EmailSendCoordinator _coordinator;
    private readonly string _publicBaseUrl;
    private readonly ILogger<PasswordResetService> _log;

    public PasswordResetService(
        LicenseDbContext db,
        PasswordHasher hasher,
        EmailSendCoordinator coordinator,
        IConfiguration config,
        ILogger<PasswordResetService> log)
    {
        _db = db;
        _hasher = hasher;
        _coordinator = coordinator;
        _publicBaseUrl = config["App:PublicBaseUrl"]?.TrimEnd('/') ?? "https://localhost:5001";
        _log = log;
    }

    public async Task RequestResetAsync(string email, CancellationToken ct = default)
    {
        var customer = await _db.Customers.FirstOrDefaultAsync(c => c.Email == email, ct);
        if (customer is null) return;                          // enumeration-safe
        if (customer.EmailConfirmedAt is null) return;         // unconfirmed → silent

        var now = DateTimeOffset.UtcNow;
        var throttleCutoff = now - RequestThrottle;

        // Recent unused token reuse
        var existing = await _db.PasswordResetTokens
            .Where(t => t.CustomerId == customer.Id && t.UsedAt == null && t.CreatedAt > throttleCutoff)
            .OrderByDescending(t => t.CreatedAt)
            .FirstOrDefaultAsync(ct);

        Guid tokenId;
        if (existing is not null)
        {
            tokenId = existing.Id;
        }
        else
        {
            tokenId = Guid.NewGuid();
            _db.PasswordResetTokens.Add(new PasswordResetToken
            {
                Id = tokenId,
                CustomerId = customer.Id,
                CreatedAt = now
            });
            await _db.SaveChangesAsync(ct);
        }

        var resetUrl = $"{_publicBaseUrl}/password-reset?token={tokenId}";

        await _coordinator.TrySendAsync(
            customerId: customer.Id,
            templateKey: "password-reset",
            contextKey: tokenId.ToString(),
            templateBuilder: (c, _) => EmailTemplates.PasswordReset(c.Name, resetUrl),
            requiresUnsubscribeRespect: false,                 // transactional
            ct);
    }

    public async Task<PasswordResetResult> CompleteResetAsync(Guid token, string newPassword, CancellationToken ct = default)
    {
        var record = await _db.PasswordResetTokens
            .Include(t => t.Customer)
            .FirstOrDefaultAsync(t => t.Id == token, ct);

        if (record is null) return PasswordResetResult.TokenInvalid;
        if (record.UsedAt is not null) return PasswordResetResult.TokenInvalid;
        if (DateTimeOffset.UtcNow - record.CreatedAt > TokenLifetime) return PasswordResetResult.TokenInvalid;
        if (newPassword.Length < 8) return PasswordResetResult.PasswordTooShort;

        record.Customer.PasswordHash = _hasher.Hash(newPassword);
        record.UsedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);

        _log.LogInformation("Password reset completed for customer {CustomerId}", record.CustomerId);
        return PasswordResetResult.Success;
    }
}
