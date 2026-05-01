using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using OrderDeck.Licensing.Services;

namespace OrderDeck.App.Services;

/// <summary>
/// DelegatingHandler that injects the customer JWT bearer (from <see cref="LicenseService.CurrentAuth"/>)
/// on each outgoing request. Used by BackupClient since auth state may change at runtime
/// (login, logout, token refresh) and a static SetAuthToken would race.
///
/// On 401, attempts a single refresh-token rotation via <see cref="TokenRefresher"/>
/// before giving up — this closes the audit gap where short-lived access tokens
/// would force the user back to a login dialog every 15 minutes.
/// </summary>
internal sealed class BearerAuthHandler : DelegatingHandler
{
    private readonly LicenseService _license;
    private readonly TokenRefresher _refresher;

    public BearerAuthHandler(LicenseService license, TokenRefresher refresher)
    {
        _license = license;
        _refresher = refresher;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ApplyBearer(request);
        var resp = await base.SendAsync(request, cancellationToken);
        if (resp.StatusCode != HttpStatusCode.Unauthorized) return resp;

        // One-shot refresh + retry. TokenRefresher is single-flight so concurrent
        // 401s converge on a single /refresh call.
        resp.Dispose();
        var fresh = await _refresher.TryRefreshAsync(cancellationToken);
        if (fresh is null)
        {
            // Rotation failed terminally. Re-issue the request without auth so
            // the caller sees a clean 401 (and can drop to login UX) rather
            // than seeing the stale-but-disposed first response.
            var retry = await CloneAsync(request, cancellationToken);
            return await base.SendAsync(retry, cancellationToken);
        }

        var retried = await CloneAsync(request, cancellationToken);
        ApplyBearer(retried);
        return await base.SendAsync(retried, cancellationToken);
    }

    private void ApplyBearer(HttpRequestMessage req)
    {
        var token = _license.CurrentAuth?.Token;
        req.Headers.Authorization = string.IsNullOrEmpty(token)
            ? null
            : new AuthenticationHeaderValue("Bearer", token);
    }

    /// <summary>HttpRequestMessage is single-use; rebuild for retry. Body content
    /// is buffered into memory so streaming uploads (Phase 5a backup blobs) don't
    /// die after the first read.</summary>
    private static async Task<HttpRequestMessage> CloneAsync(HttpRequestMessage src, CancellationToken ct)
    {
        var clone = new HttpRequestMessage(src.Method, src.RequestUri) { Version = src.Version };
        if (src.Content is not null)
        {
            var bytes = await src.Content.ReadAsByteArrayAsync(ct);
            var newContent = new ByteArrayContent(bytes);
            foreach (var h in src.Content.Headers) newContent.Headers.TryAddWithoutValidation(h.Key, h.Value);
            clone.Content = newContent;
        }
        foreach (var h in src.Headers)
            if (!h.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
                clone.Headers.TryAddWithoutValidation(h.Key, h.Value);
        return clone;
    }
}
