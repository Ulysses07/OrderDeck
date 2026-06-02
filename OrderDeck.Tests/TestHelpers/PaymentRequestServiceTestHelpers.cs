using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using OrderDeck.App.Services.Sync;
using OrderDeck.Licensing;
using OrderDeck.Licensing.Api;

namespace OrderDeck.Tests.TestHelpers;

/// <summary>
/// E3b sonrası PaymentRequestService ctor'u LicenseApiClient + ICurrentLicenseProvider
/// alıyor. Test'ler için no-op stub'lar — async balance path test edilmediği sürece
/// sync OpenWhatsApp davranışını değiştirmez.
/// </summary>
public static class PaymentRequestServiceTestHelpers
{
    public static LicenseApiClient StubApiClient()
    {
        var http = new HttpClient(new NotFoundHandler()) { BaseAddress = new System.Uri("https://stub") };
        return new LicenseApiClient(http, new LicenseTokenStore());
    }

    public sealed class NullLicenseProvider : ICurrentLicenseProvider
    {
        public string? CurrentLicenseKey => null;
    }

    private sealed class NotFoundHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}
