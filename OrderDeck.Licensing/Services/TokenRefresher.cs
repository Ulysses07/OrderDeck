using OrderDeck.Licensing.Api;
using OrderDeck.Licensing.Api.Models;
using OrderDeck.Licensing.Storage;
using Microsoft.Extensions.Logging;

namespace OrderDeck.Licensing.Services;

/// <summary>
/// Single-flight refresh-token rotation. Multiple concurrent 401s from different
/// HTTP clients converge on a single network call; everyone else waits and reuses
/// the new pair. Without the gate, a flurry of parallel requests after token
/// expiry would each attempt their own /refresh, racing the rotation chain and
/// invalidating each other.
/// </summary>
public sealed class TokenRefresher
{
    private readonly LicenseApiClient _api;
    private readonly AuthStore _authStore;
    private readonly LicenseService _license;
    private readonly ILogger<TokenRefresher> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public TokenRefresher(LicenseApiClient api, AuthStore authStore, LicenseService license, ILogger<TokenRefresher> log)
    {
        _api = api;
        _authStore = authStore;
        _license = license;
        _log = log;
    }

    /// <summary>Returns the (possibly-just-refreshed) bearer token, or null if no
    /// refresh-token is on disk or rotation failed terminally.</summary>
    public async Task<string?> TryRefreshAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var auth = _authStore.Load();
            if (auth?.RefreshToken is null)
                return null;

            // Race: another caller refreshed while we waited on the gate. If the
            // current access token is now valid (>30s remaining), reuse it.
            if (auth.TokenExpiresAt > DateTimeOffset.UtcNow.AddSeconds(30))
            {
                _license.ReloadAuthFromStore();
                return auth.Token;
            }

            try
            {
                var fresh = await _api.RefreshAsync(new RefreshRequest(auth.RefreshToken), ct);
                var updated = auth with
                {
                    Token = fresh.Token,
                    TokenExpiresAt = fresh.ExpiresAt,
                    RefreshToken = fresh.RefreshToken ?? auth.RefreshToken,
                    RefreshExpiresAt = fresh.RefreshExpiresAt ?? auth.RefreshExpiresAt
                };
                _authStore.Save(updated);
                _license.ReloadAuthFromStore();  // bumps CurrentAuth + LicenseApiClient bearer in one place
                return updated.Token;
            }
            catch (InvalidCredentialsException)
            {
                // Refresh token rejected — likely revoked, expired, or session
                // ended elsewhere. Clear local auth so caller falls through to
                // re-login UX rather than looping refresh forever.
                _log.LogInformation("Refresh token rejected; clearing local auth.");
                _authStore.Clear();
                _license.ReloadAuthFromStore();  // CurrentAuth → null, bearer cleared
                return null;
            }
            catch (LicenseApiNetworkException ex)
            {
                // Transient — keep existing token (Polly should have already
                // retried). Surface null so caller knows refresh didn't happen.
                _log.LogWarning(ex, "Token refresh failed (network); keeping current token.");
                return null;
            }
        }
        finally { _gate.Release(); }
    }
}
