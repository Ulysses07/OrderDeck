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
/// Token state lives on a singleton <see cref="LicenseTokenStore"/>; this
/// handler is registered as transient so HttpClientFactory can build a
/// fresh pipeline on every CreateClient call (DelegatingHandler instances
/// must not be reused across handler chains — that's an explicit invariant
/// the factory enforces).
/// </summary>
public sealed class LicenseAuthHandler : DelegatingHandler
{
    private readonly LicenseTokenStore _tokenStore;

    public LicenseAuthHandler(LicenseTokenStore tokenStore)
    {
        _tokenStore = tokenStore;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Snapshot — if the caller rotates the token mid-flight, the
        // request already in transit keeps its original credential. The
        // next request picks up the new value.
        var header = _tokenStore.CurrentHeader;
        if (header is not null)
            request.Headers.Authorization = header;
        return base.SendAsync(request, cancellationToken);
    }
}
