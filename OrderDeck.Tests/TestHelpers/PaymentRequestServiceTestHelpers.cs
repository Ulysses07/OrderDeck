using System;
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

    /// <summary>Cloud API'nin "aynı gönderim hâlâ işleniyor" (<c>in_progress</c>)
    /// cevabını taklit eden istemci + ona uyan lisans sağlayıcı. ViewModel
    /// testleri bunu kullanıyor: sonucu bilinmeyen gönderimin operatöre nasıl
    /// gösterildiği ancak VM seviyesinde görülüyor.</summary>
    public static (LicenseApiClient Api, ICurrentLicenseProvider License) InProgressCloudApiClient()
    {
        var http = new HttpClient(new InProgressHandler()) { BaseAddress = new System.Uri("https://stub") };
        return (new LicenseApiClient(http, new LicenseTokenStore()), new FixedLicenseProvider());
    }

    public sealed class NullLicenseProvider : ICurrentLicenseProvider
    {
        public string? CurrentLicenseKey => null;
    }

    private sealed class FixedLicenseProvider : ICurrentLicenseProvider
    {
        public string? CurrentLicenseKey => InProgressHandler.LicenseKey;
    }

    private sealed class NotFoundHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    private sealed class InProgressHandler : HttpMessageHandler
    {
        public const string LicenseKey = "LDK-VM-INPROGRESS";
        private static readonly Guid LicenseId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path == "/api/v1/me/licenses")
            {
                return Task.FromResult(Json($$"""
                    [{"licenseKey":"{{LicenseKey}}","skuCode":"STD",
                      "expiresAt":"2030-01-01T00:00:00+00:00","revokedAt":null,
                      "id":"{{LicenseId}}"}]
                    """));
            }

            if (path.EndsWith("/whatsapp/send", StringComparison.Ordinal))
            {
                return Task.FromResult(Json(
                    """{"ok":false,"errorCode":"in_progress","errorMessage":"işleniyor","messageId":null}"""));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static HttpResponseMessage Json(string body) =>
            new(HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            };
    }
}
