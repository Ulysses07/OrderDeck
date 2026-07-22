using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using Microsoft.EntityFrameworkCore;

namespace OrderDeck.LicenseServer.Services.IntakeForm;

/// <summary>
/// Domain orchestration for intake form configs and submissions.
/// Enforces slug uniqueness, license-active guard, and submission persistence.
/// </summary>
public sealed class IntakeFormService
{
    private readonly LicenseDbContext _db;

    public IntakeFormService(LicenseDbContext db) => _db = db;

    public sealed class SlugAlreadyTakenException : Exception
    {
        public string Slug { get; }
        public SlugAlreadyTakenException(string slug)
            : base($"Slug '{slug}' already taken by another customer.")
            => Slug = slug;
    }

    /// <summary>Loads a customer's existing config, or null if not configured.</summary>
    public Task<IntakeFormConfig?> GetByCustomerAsync(Guid customerId, CancellationToken ct = default) =>
        _db.IntakeFormConfigs.FirstOrDefaultAsync(c => c.CustomerId == customerId, ct);

    /// <summary>
    /// Returns config only if (a) form IsActive AND (b) customer has an active license
    /// (RevokedAt null AND ExpiresAt &gt; now). Otherwise null — caller treats as 410 Gone.
    /// </summary>
    public async Task<IntakeFormConfig?> GetActiveBySlugAsync(string slug, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var config = await _db.IntakeFormConfigs
            .FirstOrDefaultAsync(c => c.Slug == slug && c.IsActive, ct);
        if (config is null) return null;

        var hasActiveLicense = await _db.Licenses
            .AnyAsync(l => l.CustomerId == config.CustomerId
                        && l.RevokedAt == null
                        && l.ExpiresAt > now, ct);
        return hasActiveLicense ? config : null;
    }

    /// <summary>Idempotent claim/update. Throws SlugAlreadyTakenException if slug used by another customer.</summary>
    public async Task<IntakeFormConfig> UpsertConfigAsync(
        Guid customerId, string slug, string whatsAppPhone, string? customTitle, bool isActive,
        CancellationToken ct = default)
    {
        var conflict = await _db.IntakeFormConfigs
            .FirstOrDefaultAsync(c => c.Slug == slug && c.CustomerId != customerId, ct);
        if (conflict is not null) throw new SlugAlreadyTakenException(slug);

        var existing = await GetByCustomerAsync(customerId, ct);
        var now = DateTimeOffset.UtcNow;

        if (existing is null)
        {
            var created = new IntakeFormConfig
            {
                Id = Guid.NewGuid(),
                CustomerId = customerId,
                Slug = slug,
                WhatsAppPhone = whatsAppPhone,
                CustomTitle = customTitle,
                IsActive = isActive,
                CreatedAt = now,
                UpdatedAt = now
            };
            _db.IntakeFormConfigs.Add(created);
            await _db.SaveChangesAsync(ct);
            return created;
        }

        existing.Slug = slug;
        existing.WhatsAppPhone = whatsAppPhone;
        existing.CustomTitle = customTitle;
        existing.IsActive = isActive;
        existing.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);
        return existing;
    }

    // Phase 4f overload kept for backwards compatibility with existing tests.
    public Task<IntakeFormSubmission> SaveSubmissionAsync(
        Guid configId, string username, string fullName, string address,
        string? ipAddress, string? userAgent, CancellationToken ct = default)
        => SaveSubmissionAsync(configId, username, fullName, address, null, ipAddress, userAgent, ct);

    // Phase 4g overload — delegates to the full multi-platform overload.
    public Task<IntakeFormSubmission> SaveSubmissionAsync(
        Guid configId, string username, string fullName, string address,
        string? phone, string? ipAddress, string? userAgent, CancellationToken ct = default)
        => SaveSubmissionAsync(
            configId,
            youTubeUsername: null, instagramUsername: null,
            facebookUsername: null, tikTokUsername: null,
            legacyUsername: username, fullName: fullName, address: address, phone: phone,
            email: null, tckn: null, whatsAppConsent: false, smsConsent: false,
            ipAddress: ipAddress, userAgent: userAgent, ct: ct);

    /// <summary>Multi-platform gönderim: platform kullanıcı adları + email/TC +
    /// mesaj izinleri. <paramref name="legacyUsername"/> eski WPF sync'i için
    /// ilk dolu platform adına set edilir.</summary>
    public async Task<IntakeFormSubmission> SaveSubmissionAsync(
        Guid configId,
        string? youTubeUsername, string? instagramUsername,
        string? facebookUsername, string? tikTokUsername,
        string legacyUsername, string fullName, string address, string? phone,
        string? email, string? tckn, bool whatsAppConsent, bool smsConsent,
        string? ipAddress, string? userAgent, CancellationToken ct = default,
        string? youTubeChannelId = null)
    {
        var sub = new IntakeFormSubmission
        {
            Id = Guid.NewGuid(),
            IntakeFormConfigId = configId,
            Username = legacyUsername,
            YouTubeUsername = youTubeUsername,
            InstagramUsername = instagramUsername,
            FacebookUsername = facebookUsername,
            TikTokUsername = tikTokUsername,
            YouTubeChannelId = youTubeChannelId,
            FullName = fullName,
            Address = address,
            Phone = phone,
            Email = email,
            Tckn = tckn,
            WhatsAppConsent = whatsAppConsent,
            SmsConsent = smsConsent,
            SubmittedAt = DateTimeOffset.UtcNow,
            IpAddress = ipAddress,
            UserAgent = userAgent
        };
        _db.IntakeFormSubmissions.Add(sub);
        await _db.SaveChangesAsync(ct);
        return sub;
    }

    public Task<List<IntakeFormSubmission>> GetSubmissionsSinceAsync(
        Guid customerId, DateTimeOffset since, int limit, CancellationToken ct = default) =>
        _db.IntakeFormSubmissions
            .Where(s => s.Config.CustomerId == customerId && s.SubmittedAt > since)
            .OrderBy(s => s.SubmittedAt)
            .Take(limit)
            .ToListAsync(ct);
}
