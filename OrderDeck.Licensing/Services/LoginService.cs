using OrderDeck.Licensing.Api;
using OrderDeck.Licensing.Api.Models;
using OrderDeck.Licensing.Storage;

namespace OrderDeck.Licensing.Services;

/// <summary>
/// Orchestrates the auth flow: register/resend/login + persisting AuthRecord.
/// License activation lives in <see cref="LicenseService"/>.
/// </summary>
public sealed class LoginService
{
    private readonly LicenseApiClient _api;
    private readonly AuthStore _authStore;

    public LoginService(LicenseApiClient api, AuthStore authStore)
    {
        _api = api;
        _authStore = authStore;
    }

    public async Task LoginAsync(string email, string password, CancellationToken ct = default)
    {
        var loginResp = await _api.LoginAsync(new LoginRequest(email, password), ct);
        _api.SetAuthToken(loginResp.Token);

        var me = await _api.GetMeAsync(ct);

        _authStore.Save(new AuthRecord(
            CustomerId: me.Id,
            Email: me.Email,
            Name: me.Name,
            Token: loginResp.Token,
            TokenExpiresAt: loginResp.ExpiresAt,
            // Phase 5b — server now returns refresh tokens; old servers leave nulls.
            RefreshToken: loginResp.RefreshToken,
            RefreshExpiresAt: loginResp.RefreshExpiresAt));
    }

    public Task RegisterAsync(string email, string name, string password, CancellationToken ct = default)
        => _api.RegisterAsync(new RegisterRequest(email, name, password), ct);

    public Task ResendConfirmationAsync(string email, CancellationToken ct = default)
        => _api.ResendConfirmationAsync(new ResendRequest(email), ct);

    /// <summary>Returns the customer's active licenses (uses the token from <see cref="LicenseApiClient.SetAuthToken"/>).</summary>
    public Task<List<LicenseSummary>> GetMyLicensesAsync(CancellationToken ct = default)
        => _api.GetMyLicensesAsync(ct);

    public void Logout()
    {
        // Best-effort revoke server-side. Don't block local clear on network —
        // user pressed Logout, that must succeed locally even if offline.
        var auth = _authStore.Load();
        if (!string.IsNullOrEmpty(auth?.RefreshToken))
        {
            _ = Task.Run(async () =>
            {
                try { await _api.LogoutAsync(new LogoutRequest(auth.RefreshToken), CancellationToken.None); }
                catch { /* offline / server down — refresh will expire on its own */ }
            });
        }
        _authStore.Clear();
        _api.SetAuthToken(null);
    }
}
