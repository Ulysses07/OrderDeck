using System.Net;
using FluentAssertions;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Integration;

public class HealthEndpointsTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public HealthEndpointsTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Healthz_returns_200_without_auth()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/healthz");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Ready_returns_200_when_db_reachable()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/ready");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Healthz_does_not_require_auth()
    {
        // No auth header — must not 401.
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = null;
        var resp = await client.GetAsync("/healthz");
        resp.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);
    }
}
