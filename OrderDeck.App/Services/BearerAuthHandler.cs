using System.Net.Http;
using System.Net.Http.Headers;
using OrderDeck.Licensing.Services;

namespace OrderDeck.App.Services;

/// <summary>
/// DelegatingHandler that injects the customer JWT bearer (from <see cref="LicenseService.CurrentAuth"/>)
/// on each outgoing request. Used by BackupClient since auth state may change at runtime
/// (login, logout, token refresh) and a static SetAuthToken would race.
/// </summary>
internal sealed class BearerAuthHandler : DelegatingHandler
{
    private readonly LicenseService _license;

    public BearerAuthHandler(LicenseService license) => _license = license;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = _license.CurrentAuth?.Token;
        if (!string.IsNullOrEmpty(token))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return base.SendAsync(request, cancellationToken);
    }
}
