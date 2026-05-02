using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace OrderDeck.Licensing.Trial;

/// <summary>
/// Trial state machine. Reads from <see cref="ITrialStorage"/> (typically composite),
/// applies HW fingerprint and time checks, exposes <see cref="GetState"/> + <see cref="StartNewTrial"/>.
/// </summary>
public sealed class TrialService
{
    private readonly ITrialStorage _storage;
    private readonly IHardwareIdProvider _hwId;
    private readonly LicensingOptions _opts;
    private readonly Func<DateTimeOffset> _now;
    private readonly ILogger<TrialService> _log;

    public TrialService(
        ITrialStorage storage,
        IHardwareIdProvider hwId,
        IOptions<LicensingOptions> opts,
        Func<DateTimeOffset> nowProvider,
        ILogger<TrialService> log)
    {
        _storage = storage;
        _hwId = hwId;
        _opts = opts.Value;
        _now = nowProvider;
        _log = log;
    }

    public TrialState GetState()
    {
        var record = _storage.TryRead();
        if (record is null) return TrialState.NoTrial.Instance;

        var currentHw = _hwId.GetHardwareId();
        if (!string.Equals(record.HardwareFingerprint, currentHw, StringComparison.OrdinalIgnoreCase))
        {
            // Phase 5d migration: stored fingerprint may be the pre-SID
            // username-based hash. If it matches the legacy hash for this
            // machine, accept the trial AND silently migrate the stored
            // record to the SID-based hash so subsequent reads stay fast.
            // Mirrors the server-side ActivationManager legacy fallback.
            var legacyHw = _hwId.GetLegacyHardwareId();
            if (legacyHw is not null &&
                string.Equals(record.HardwareFingerprint, legacyHw, StringComparison.OrdinalIgnoreCase))
            {
                _log.LogInformation(
                    "Trial fingerprint matched legacy hash — migrating stored record to SID-based hash");
                var migrated = record with { HardwareFingerprint = currentHw };
                try { _storage.Write(migrated); }
                catch (Exception ex)
                {
                    // Migration failure isn't fatal — fall through to the
                    // legacy-match acceptance below. Next launch will retry.
                    _log.LogWarning(ex, "Trial fingerprint migration write failed");
                }
                record = migrated;
            }
            else
            {
                _log.LogWarning("Trial HW fingerprint mismatch (stored={Stored}, current={Current}) — treating as expired",
                    record.HardwareFingerprint, currentHw);
                return new TrialState.Expired(record.ExpiresAt);
            }
        }

        var now = _now();
        if (now >= record.ExpiresAt)
            return new TrialState.Expired(record.ExpiresAt);

        var remaining = (int)Math.Ceiling((record.ExpiresAt - now).TotalDays);
        return new TrialState.Active(remaining, record.ExpiresAt);
    }

    /// <summary>Persists a new trial record (current HW, configured duration) and returns Active.</summary>
    public TrialState StartNewTrial()
    {
        var now = _now();
        var record = new TrialRecord(
            StartedAt: now,
            ExpiresAt: now.AddDays(_opts.TrialDurationDays),
            HardwareFingerprint: _hwId.GetHardwareId(),
            Version: 1);
        _storage.Write(record);
        _log.LogInformation("Trial started: {Days}-day window expires at {ExpiresAt}",
            _opts.TrialDurationDays, record.ExpiresAt);
        return new TrialState.Active(_opts.TrialDurationDays, record.ExpiresAt);
    }
}
