using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using OrderDeck.App.Services;
using OrderDeck.Licensing;
using OrderDeck.Licensing.Api;
using Xunit;

namespace OrderDeck.Tests.Services;

/// <summary>
/// <see cref="UpdateChecker"/> — server'dan en son sürümü çekip mevcut sürümle
/// karşılaştırır. Gerçek LicenseApiClient + fake HttpMessageHandler ile
/// /api/app/version yanıtı kanned döndürülür.
/// </summary>
public class UpdateCheckerTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;
        public StubHandler(HttpStatusCode status, string body) { _status = status; _body = body; }
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json"),
            });
    }

    private static UpdateChecker Build(string body, HttpStatusCode status = HttpStatusCode.OK)
    {
        var http = new HttpClient(new StubHandler(status, body)) { BaseAddress = new Uri("https://stub") };
        var api = new LicenseApiClient(http, new LicenseTokenStore());
        return new UpdateChecker(api);
    }

    [Fact]
    public async Task Newer_version_returns_update_info()
    {
        var checker = Build("{\"latestVersion\":\"2.0.0\",\"downloadUrl\":\"https://dl/indir\"}");

        var result = await checker.CheckAsync(new Version(1, 0, 0));

        result.Should().NotBeNull();
        result!.LatestVersion.Should().Be("2.0.0");
        result.DownloadUrl.Should().Be("https://dl/indir");
    }

    [Fact]
    public async Task Equal_version_returns_null()
    {
        var checker = Build("{\"latestVersion\":\"1.2.3\",\"downloadUrl\":\"https://dl\"}");
        (await checker.CheckAsync(new Version(1, 2, 3))).Should().BeNull();
    }

    [Fact]
    public async Task Older_latest_returns_null()
    {
        var checker = Build("{\"latestVersion\":\"1.0.0\",\"downloadUrl\":\"https://dl\"}");
        (await checker.CheckAsync(new Version(2, 0, 0))).Should().BeNull();
    }

    [Fact]
    public async Task Null_latest_version_returns_null()
    {
        var checker = Build("{\"latestVersion\":null,\"downloadUrl\":\"https://dl\"}");
        (await checker.CheckAsync(new Version(1, 0, 0))).Should().BeNull();
    }

    [Fact]
    public async Task Unparseable_latest_returns_null()
    {
        var checker = Build("{\"latestVersion\":\"sürüm-x\",\"downloadUrl\":\"https://dl\"}");
        (await checker.CheckAsync(new Version(1, 0, 0))).Should().BeNull();
    }

    [Fact]
    public async Task Server_error_returns_null_silently()
    {
        var checker = Build("{}", HttpStatusCode.InternalServerError);
        (await checker.CheckAsync(new Version(1, 0, 0))).Should().BeNull();
    }
}
