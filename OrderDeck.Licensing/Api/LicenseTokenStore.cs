using System.Net.Http.Headers;

namespace OrderDeck.Licensing.Api;

/// <summary>
/// Singleton holder for the bearer token used by every license-server HTTP
/// call. Split out from <see cref="LicenseAuthHandler"/> so the handler
/// itself can be transient (the lifetime DelegatingHandler instances must
/// have when registered with HttpClientFactory) while every transient
/// handler still observes the same volatile token reference.
///
/// Previously the token lived on a singleton LicenseAuthHandler and
/// AddHttpMessageHandler re-used the same instance across HttpClient
/// creations. The first creation set Handler.InnerHandler; subsequent
/// creations threw "InnerHandler must be null. DelegatingHandler instances
/// provided to HttpMessageHandlerBuilder must not be reused or cached."
/// (Reproed by opening the Settings dialog twice.)
/// </summary>
public sealed class LicenseTokenStore
{
    // Reference assignment is atomic on the CLR; volatile guarantees
    // freshness across threads. AuthenticationHeaderValue is immutable so
    // the same instance can be handed out to multiple in-flight requests.
    private volatile AuthenticationHeaderValue? _authHeader;

    public AuthenticationHeaderValue? CurrentHeader => _authHeader;

    public void SetToken(string? token)
    {
        _authHeader = string.IsNullOrEmpty(token)
            ? null
            : new AuthenticationHeaderValue("Bearer", token);
    }
}
