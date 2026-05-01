using Hangfire;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace OrderDeck.LicenseServer.Services.Email;

/// <summary>
/// Hangfire recurring jobs — günde bir kez çağrılır. Her method ExpiresAt
/// window'unu hesaplayıp eligible licenses için EmailSendCoordinator'a delegate eder.
/// EmailLog dedup garantisi sayesinde idempotent (job 1 saat geç çalışsa bile aynı email
/// 2 defa gitmez).
/// </summary>
public sealed class ReminderJobs
{
    private readonly LicenseDbContext _db;
    private readonly EmailSendCoordinator _coordinator;
    private readonly string _portalUrl;
    private readonly ILogger<ReminderJobs> _log;

    public ReminderJobs(
        LicenseDbContext db,
        EmailSendCoordinator coordinator,
        IConfiguration config,
        ILogger<ReminderJobs> log)
    {
        _db = db;
        _coordinator = coordinator;
        _portalUrl = config["App:PublicBaseUrl"] ?? "https://localhost:5001";
        _log = log;
    }

    [AutomaticRetry(Attempts = 3, DelaysInSeconds = new[] { 60, 300, 1800 }, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
    public Task SendRenewal14dAsync(CancellationToken ct) =>
        ScanAndSendRenewalAsync(daysBeforeExpiry: 14, "renewal-14d",
            (c, k, e, p, u) => EmailTemplates.Renewal14d(c, k, e, p, u), ct);

    [AutomaticRetry(Attempts = 3, DelaysInSeconds = new[] { 60, 300, 1800 }, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
    public Task SendRenewal7dAsync(CancellationToken ct) =>
        ScanAndSendRenewalAsync(7, "renewal-7d",
            (c, k, e, p, u) => EmailTemplates.Renewal7d(c, k, e, p, u), ct);

    [AutomaticRetry(Attempts = 3, DelaysInSeconds = new[] { 60, 300, 1800 }, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
    public Task SendRenewal3dAsync(CancellationToken ct) =>
        ScanAndSendRenewalAsync(3, "renewal-3d",
            (c, k, e, p, u) => EmailTemplates.Renewal3d(c, k, e, p, u), ct);

    [AutomaticRetry(Attempts = 3, DelaysInSeconds = new[] { 60, 300, 1800 }, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
    public Task SendRenewal0dAsync(CancellationToken ct) =>
        ScanAndSendRenewalAsync(0, "renewal-0d",
            (c, k, e, p, u) => EmailTemplates.Renewal0d(c, k, e, p, u), ct);

    [AutomaticRetry(Attempts = 3, DelaysInSeconds = new[] { 60, 300, 1800 }, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
    public async Task SendExpired1dAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var (from, to) = (now.AddDays(-1.5), now.AddDays(-0.5));
        var candidates = await LoadCandidatesAsync(from, to, ct);

        _log.LogInformation("Expired-1d job: {Count} candidates in window [{From}..{To}]",
            candidates.Count, from, to);

        foreach (var license in candidates)
        {
            await _coordinator.TrySendAsync(
                customerId: license.CustomerId,
                templateKey: "expired-1d",
                contextKey: license.LicenseKey,
                templateBuilder: (c, unsubUrl) => EmailTemplates.ExpiredAfter1d(c.Name, license.LicenseKey, _portalUrl, unsubUrl),
                requiresUnsubscribeRespect: true,
                ct);
        }
    }

    private async Task ScanAndSendRenewalAsync(
        int daysBeforeExpiry,
        string templateKey,
        Func<string, string, DateTimeOffset, string, string?, (string, string, string)> templateFn,
        CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var (from, to) = (now.AddDays(daysBeforeExpiry - 0.5), now.AddDays(daysBeforeExpiry + 0.5));
        var candidates = await LoadCandidatesAsync(from, to, ct);

        _log.LogInformation("{Template} job: {Count} candidates in window [{From}..{To}]",
            templateKey, candidates.Count, from, to);

        foreach (var license in candidates)
        {
            await _coordinator.TrySendAsync(
                customerId: license.CustomerId,
                templateKey: templateKey,
                contextKey: license.LicenseKey,
                templateBuilder: (c, unsubUrl) => templateFn(c.Name, license.LicenseKey, license.ExpiresAt, _portalUrl, unsubUrl),
                requiresUnsubscribeRespect: true,
                ct);
        }
    }

    private Task<List<License>> LoadCandidatesAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct) =>
        _db.Licenses
            .Include(l => l.Customer)
            .Where(l => l.RevokedAt == null
                     && l.ExpiresAt >= from && l.ExpiresAt < to
                     && l.Customer.EmailConfirmedAt != null)
            .ToListAsync(ct);
}
