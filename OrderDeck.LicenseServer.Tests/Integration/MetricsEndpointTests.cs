using System.Net;
using FluentAssertions;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Integration;

public class MetricsEndpointTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public MetricsEndpointTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Metrics_endpoint_returns_200_with_prometheus_format()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/metrics");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await resp.Content.ReadAsStringAsync();
        // Prometheus exposition format always begins with HELP/TYPE comment
        // lines or with a sample line. Either way, it's text/plain.
        body.Should().NotBeEmpty();
        // Runtime + ASP.NET instrumentations are always-on. The exact metric
        // name format has changed across OTel versions (process_runtime_dotnet
        // → dotnet_process), so we sample any one well-known signal.
        body.Should().Match(b =>
            b.Contains("dotnet_") || b.Contains("process_runtime_dotnet") || b.Contains("aspnetcore_"));
    }

    [Fact]
    public async Task Metrics_endpoint_does_not_require_auth()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = null;
        var resp = await client.GetAsync("/metrics");
        resp.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);
    }
}
