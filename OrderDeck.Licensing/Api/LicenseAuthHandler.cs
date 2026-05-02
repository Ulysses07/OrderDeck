using System.Net.Http.Headers;

namespace OrderDeck.Licensing.Api;

/// <summary>
/// Per-request bearer-token injector for <see cref="LicenseApiClient"/>.
///
/// Replaces the previous SetAuthToken implementation that mutated
/// HttpClient.DefaultRequestHeaders. HttpHeaders is not thread-safe — a
/// concurrent SendAsync (heartbeat hosted service) and a SetAuthToken call
/// (logout, token rotation) on the same HttpClient could race and throw
/// InvalidOperationException("Collection was modified").
///
/// This handler stores the token in a single volatile reference, snapshots it
/// per request, and writes Authorization onto the per-request HttpRequestMessage.
/// HttpRequestMessage.Headers is owned by the request, so there's no shared
/// mutable state on the hot path.
/// </summary>
public sealed class LicenseAuthHandler : DelegatingHandler
{
    // Reference assignment is atomic on the CLR; volatile guarantees freshness
    // across threads. AuthenticationHeaderValue is immutable so we can hand
    // out the same instance to multiple in-flight requests safely.
    private volatile AuthenticationHeaderValue? _authHeader;

    public void SetToken(string? token)
    {
        _authHeader = string.IsNullOrEmpty(token)
            ? null
            : new AuthenticationHeaderValue("Bearer", token);
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Snapshot — if the caller rotates the token mid-flight, the request
        // already in transit keeps its original credential. The next request
        // picks up the new value.
        var header = _authHeader;
        if (header is not null)
            request.Headers.Authorization = header;
        return base.SendAsync(request, cancellationToken);
    }
}
