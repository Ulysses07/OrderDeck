using LiveDeck.Licensing.Api;
using LiveDeck.Licensing.Api.Models;
using LiveDeck.Licensing.Storage;
using LiveDeck.Licensing.Trial;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LiveDeck.Licensing.Services;

/// <summary>
/// State machine controller. Loads cached auth/license, calls /licenses/validate,
/// and emits a <see cref="LicenseStatus"/> for the UI to bind to.
/// </summary>
public sealed class LicenseService
{
    private readonly LicenseApiClient _api;
    private readonly AuthStore _authStore;
    private readonly LicenseStateStore _licenseStore;
    private readonly IHardwareIdProvider _hwId;
    private readonly LicensingOptions _opt;
    private readonly TrialService _trial;
    private readonly ILogger<LicenseService> _log;

    public LicenseService(
        LicenseApiClient api,
        AuthStore authStore,
        LicenseStateStore licenseStore,
        IHardwareIdProvider hwId,
        IOptions<LicensingOptions> opt,
        TrialService trial,
        ILogger<LicenseService> log)
    {
        _api = api;
        _authStore = authStore;
        _licenseStore = licenseStore;
        _hwId = hwId;
        _opt = opt.Value;
        _trial = trial;
        _log = log;
    }

    public LicenseStatus CurrentStatus { get; private set; } = LicenseStatus.Initializing;

    public AuthRecord? CurrentAuth { get; private set; }

    public LicenseRecord? CurrentLicense { get; private set; }

    public event EventHandler<LicenseStatus>? StatusChanged;

    public TrialState? CurrentTrial { get; private set; }
    public bool JustStartedTrial { get; private set; }

    /// <summary>
    /// Called once at app startup. Loads cached auth, attempts online validate, falls back to offline grace.
    /// If no auth is present, falls through to trial path.
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var auth = _authStore.Load();
        if (auth is null)
        {
            await InitializeTrialPathAsync();
            return;
        }

        if (auth.TokenExpiresAt <= DateTimeOffset.UtcNow)
        {
            _log.LogInformation("Saved auth token expired locally; clearing.");
            _authStore.Clear();
            await InitializeTrialPathAsync();
            return;
        }

        CurrentAuth = auth;
        _api.SetAuthToken(auth.Token);

        var license = _licenseStore.Load();
        CurrentLicense = license;
        if (license is null)
        {
            // Auth present but no license — check trial fallback (no new trial start for logged-in users)
            var trialState = _trial.GetState();
            if (trialState is TrialState.Active a)
            {
                CurrentTrial = a;
                SetStatus(LicenseStatus.TrialActive);
            }
            else if (trialState is TrialState.Expired e)
            {
                CurrentTrial = e;
                SetStatus(LicenseStatus.TrialExpired);
            }
            else
            {
                // NoTrial — logged-in user has no license and no trial; do not auto-start trial
                SetStatus(LicenseStatus.NoLicense);
            }
            return;
        }

        await RefreshAsync(ct);
    }

    private Task InitializeTrialPathAsync()
    {
        var state = _trial.GetState();
        if (state is TrialState.NoTrial)
        {
            state = _trial.StartNewTrial();
            JustStartedTrial = true;
            _log.LogInformation("Trial started for new user.");
        }

        CurrentTrial = state;
        SetStatus(state switch
        {
            TrialState.Active   => LicenseStatus.TrialActive,
            TrialState.Expired  => LicenseStatus.TrialExpired,
            _                   => LicenseStatus.NoLicense
        });
        return Task.CompletedTask;
    }

    /// <summary>
    /// Re-validates the current license against the server. Called on startup and from heartbeat.
    /// </summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        var license = CurrentLicense ?? _licenseStore.Load();
        if (license is null || CurrentAuth is null)
        {
            SetStatus(LicenseStatus.NoLicense);
            return;
        }

        try
        {
            var response = await _api.ValidateAsync(
                new ValidateRequest(license.LicenseKey, _hwId.GetHardwareId()), ct);

            if (response is null)
            {
                // 404 — license not found for this customer
                _licenseStore.Clear();
                CurrentLicense = null;
                SetStatus(LicenseStatus.NoLicense);
                return;
            }

            HandleValidateResponse(license, response);
        }
        catch (InvalidCredentialsException)
        {
            // Token rejected by server — clear and force re-login
            _authStore.Clear();
            CurrentAuth = null;
            _api.SetAuthToken(null);
            SetStatus(LicenseStatus.NoLicense);
        }
        catch (LicenseApiNetworkException)
        {
            // Offline — grace decision
            var elapsed = DateTimeOffset.UtcNow - license.LastSuccessfulOnlineAt;
            var graceWindow = TimeSpan.FromDays(_opt.OfflineGraceDays);
            SetStatus(elapsed <= graceWindow ? LicenseStatus.OfflineGrace : LicenseStatus.OfflineExpired);
        }
    }

    /// <summary>
    /// Called from the UI after the user picks (or auto-binds) a license. Calls /licenses/activate
    /// then /licenses/validate to get fresh state.
    /// </summary>
    public async Task ActivateAsync(string licenseKey, string? machineName, CancellationToken ct = default)
    {
        // If not yet initialized, try to load auth from store (e.g. called directly in tests or after DI resolution).
        if (CurrentAuth is null)
        {
            var stored = _authStore.Load();
            if (stored is null || stored.TokenExpiresAt <= DateTimeOffset.UtcNow)
                throw new InvalidOperationException("ActivateAsync requires a logged-in customer.");
            CurrentAuth = stored;
            _api.SetAuthToken(stored.Token);
        }

        var hwId = _hwId.GetHardwareId();
        await _api.ActivateAsync(new ActivateRequest(licenseKey, hwId, machineName), ct);

        var validate = await _api.ValidateAsync(new ValidateRequest(licenseKey, hwId), ct);
        if (validate is null)
        {
            SetStatus(LicenseStatus.NoLicense);
            return;
        }

        var seed = new LicenseRecord(
            LicenseKey: licenseKey,
            SkuCode: validate.Sku ?? "STD",
            ExpiresAt: validate.ExpiresAt ?? DateTimeOffset.UtcNow.AddDays(1),
            RemainingDaysAtLastCheck: validate.RemainingDays ?? 0,
            LastValidatedAt: DateTimeOffset.UtcNow,
            LastSuccessfulOnlineAt: DateTimeOffset.UtcNow,
            LastKnownStatus: validate.Status);
        HandleValidateResponse(seed, validate);
    }

    /// <summary>Logout: clear caches and force NoLicense state. Trial storage is NOT cleared.</summary>
    public void Logout()
    {
        _authStore.Clear();
        _licenseStore.Clear();
        _api.SetAuthToken(null);
        CurrentAuth = null;
        CurrentLicense = null;
        CurrentTrial = null;
        JustStartedTrial = false;
        SetStatus(LicenseStatus.NoLicense);
        // Trial storage NOT cleared — anti-reset preserves trial state across logout
    }

    private void HandleValidateResponse(LicenseRecord prior, ValidateResponse response)
    {
        var status = MapServerStatus(response.Status);

        if (status == LicenseStatus.NoLicense)
        {
            _licenseStore.Clear();
            CurrentLicense = null;
            SetStatus(LicenseStatus.NoLicense);
            return;
        }

        var isOnlineSuccess = status == LicenseStatus.Active;
        var lastOnline = isOnlineSuccess ? DateTimeOffset.UtcNow : prior.LastSuccessfulOnlineAt;

        var updated = new LicenseRecord(
            LicenseKey: prior.LicenseKey,
            SkuCode: response.Sku ?? prior.SkuCode,
            ExpiresAt: response.ExpiresAt ?? prior.ExpiresAt,
            RemainingDaysAtLastCheck: response.RemainingDays ?? prior.RemainingDaysAtLastCheck,
            LastValidatedAt: DateTimeOffset.UtcNow,
            LastSuccessfulOnlineAt: lastOnline,
            LastKnownStatus: response.Status);
        _licenseStore.Save(updated);
        CurrentLicense = updated;
        SetStatus(status);
    }

    private static LicenseStatus MapServerStatus(string serverStatus) =>
        serverStatus.ToLowerInvariant() switch
        {
            "active" => LicenseStatus.Active,
            "revoked" => LicenseStatus.Revoked,
            "expired" => LicenseStatus.ExpiredOnline,
            "notactivated" => LicenseStatus.NoLicense,
            _ => LicenseStatus.NoLicense
        };

    private void SetStatus(LicenseStatus status)
    {
        if (CurrentStatus == status) return;
        CurrentStatus = status;
        StatusChanged?.Invoke(this, status);
    }
}
