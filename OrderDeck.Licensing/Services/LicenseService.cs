using OrderDeck.Core.Chat;
using OrderDeck.Licensing.Api;
using OrderDeck.Licensing.Api.Models;
using OrderDeck.Licensing.Storage;
using OrderDeck.Licensing.Trial;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace OrderDeck.Licensing.Services;

/// <summary>
/// State machine controller. Loads cached auth/license, calls /licenses/validate,
/// and emits a <see cref="LicenseStatus"/> for the UI to bind to.
/// Implements <see cref="ITrialModeProbe"/> so the chat bridge can drop non-Instagram
/// messages when the app is in trial mode.
/// </summary>
public sealed class LicenseService : ITrialModeProbe
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

    /// <inheritdoc cref="ITrialModeProbe.IsTrialMode"/>
    public bool IsTrialMode => CurrentStatus.IsTrialMode();

    public AuthRecord? CurrentAuth { get; private set; }

    /// <summary>Re-reads the auth record from disk and pushes the access token onto
    /// LicenseApiClient. Called by TokenRefresher after rotating the JWT pair so
    /// any caller of <see cref="CurrentAuth"/> sees the fresh token immediately.</summary>
    public void ReloadAuthFromStore()
    {
        var fresh = _authStore.Load();
        CurrentAuth = fresh;
        _api.SetAuthToken(fresh?.Token);
    }

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
            // Auth present but no local license file — common after a fresh install
            // following an admin-issued license. Probe /me/licenses; if the server
            // says the customer has an active (non-revoked, non-expired) license
            // bind it automatically so the user doesn't sit in trial mode while
            // the admin panel says "lisansı var".
            var bound = await TryAutoBindLicenseAsync(ct);
            if (bound) return;

            // Still nothing — fall through to trial fallback. We deliberately do
            // not auto-START a trial for logged-in users; only honor an existing one.
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

    /// <summary>
    /// No license.dat on disk → ask the server "does this customer have any
    /// active license?". If yes, run the same Activate flow we'd run if the
    /// user manually pasted the key in the Account dialog. Returns true on
    /// successful bind (caller skips trial fallback).
    ///
    /// Picks the latest active (non-revoked, non-expired) license when more
    /// than one is returned — extremely uncommon but possible if an admin
    /// issued a renewal before revoking the old one.
    /// </summary>
    private async Task<bool> TryAutoBindLicenseAsync(CancellationToken ct)
    {
        try
        {
            var licenses = await _api.GetMyLicensesAsync(ct);
            var now = DateTimeOffset.UtcNow;
            var candidate = licenses
                .Where(l => l.RevokedAt is null && l.ExpiresAt > now)
                .OrderByDescending(l => l.ExpiresAt)
                .FirstOrDefault();
            if (candidate is null) return false;

            _log.LogInformation(
                "Auto-binding server-side license {Key} (sku={Sku}, expires={Expires})",
                candidate.LicenseKey, candidate.SkuCode, candidate.ExpiresAt);

            await ActivateAsync(candidate.LicenseKey, machineName: Environment.MachineName, ct);
            return CurrentStatus == LicenseStatus.Active;
        }
        catch (LicenseApiNetworkException ex)
        {
            _log.LogWarning(ex, "Auto-bind skipped — server unreachable");
            return false;
        }
        catch (Exception ex)
        {
            // Anything else (slot full, license revoked between fetch and activate,
            // etc.) → log and fall through to the existing trial-fallback path so
            // the user still gets a meaningful state on screen.
            _log.LogWarning(ex, "Auto-bind failed");
            return false;
        }
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
                new ValidateRequest(license.LicenseKey, _hwId.GetHardwareId(), _hwId.GetLegacyHardwareId()), ct);

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
        var legacyHwId = _hwId.GetLegacyHardwareId();
        await _api.ActivateAsync(new ActivateRequest(licenseKey, hwId, machineName, legacyHwId), ct);

        var validate = await _api.ValidateAsync(new ValidateRequest(licenseKey, hwId, legacyHwId), ct);
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

    /// <summary>UI calls this once after showing the trial-start banner so it's not shown again.</summary>
    public void AcknowledgeTrialStartBanner()
    {
        JustStartedTrial = false;
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
