using LiveDeck.Core.Settings;
using LiveDeck.Core.Storage.Repositories;
using LiveDeck.Core.Time;
using LiveDeck.Licensing.Api;
using LiveDeck.Licensing.Api.Models;
using Microsoft.Extensions.Logging;

namespace LiveDeck.App.Services.IntakeForm;

/// <summary>
/// Pulls new IntakeFormSubmission rows from the license server, upserts each
/// as a Customer (platform="form"), advances the cursor in AppSettings.
/// Idempotent: duplicate calls are no-op (server filters by SubmittedAt &gt; since).
/// </summary>
public sealed class IntakeFormSyncService
{
    private readonly LicenseApiClient _api;
    private readonly CustomerRepository _customers;
    private readonly SettingsStore _settingsStore;
    private readonly AppSettings _settings;
    private readonly IClock _clock;
    private readonly ILogger<IntakeFormSyncService> _log;

    public event EventHandler<int>? SubmissionsSynced;

    public IntakeFormSyncService(
        LicenseApiClient api,
        CustomerRepository customers,
        SettingsStore settingsStore,
        AppSettings settings,
        IClock clock,
        ILogger<IntakeFormSyncService> log)
    {
        _api = api;
        _customers = customers;
        _settingsStore = settingsStore;
        _settings = settings;
        _clock = clock;
        _log = log;
    }

    public async Task<int> SyncOnceAsync(CancellationToken ct = default)
    {
        var since = _settings.LastIntakeFormSync;

        List<IntakeFormSubmissionDto> submissions;
        try
        {
            submissions = await _api.GetFormSubmissionsAsync(since, limit: 50, ct);
        }
        catch (LicenseApiException ex)
        {
            _log.LogWarning(ex, "Intake form sync failed: {Code}", ex.Code);
            return 0;
        }

        if (submissions.Count == 0) return 0;

        var nowUnix = _clock.UnixNow();
        DateTimeOffset newCursor = since ?? DateTimeOffset.MinValue;

        foreach (var sub in submissions.OrderBy(s => s.SubmittedAt))
        {
            _customers.UpsertFromIntakeForm(sub.Username, sub.FullName, sub.Address, nowUnix);
            if (sub.SubmittedAt > newCursor) newCursor = sub.SubmittedAt;
        }

        _settings.LastIntakeFormSync = newCursor;
        _settingsStore.Save(_settings);

        _log.LogInformation("Intake form sync: {Count} submission(s) processed (cursor → {Cursor})",
            submissions.Count, newCursor);

        SubmissionsSynced?.Invoke(this, submissions.Count);
        return submissions.Count;
    }
}
