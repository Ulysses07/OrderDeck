using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using LiveDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace LiveDeck.LicenseServer.Tests;

public class HealthCheckTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public HealthCheckTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Health_returns_ok()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/v1/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<HealthBody>();
        body.Should().NotBeNull();
        body!.status.Should().Be("ok");
        body.service.Should().Be("livedeck-license-server");
    }

    private sealed record HealthBody(string status, string service);
}
