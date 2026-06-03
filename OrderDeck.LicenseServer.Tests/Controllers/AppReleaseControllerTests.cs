using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers;

public class AppReleaseControllerTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public AppReleaseControllerTests(ApiFactory factory) => _factory = factory;

    private sealed record AppVersionResponse(string? LatestVersion, string DownloadUrl);

    [Fact]
    public async Task Version_returns_null_when_LatestVersion_unset()
    {
        // Default appsettings: AppRelease:LatestVersion boş → null.
        var resp = await _factory.CreateClient().GetAsync("/api/app/version");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await resp.Content.ReadFromJsonAsync<AppVersionResponse>();
        body.Should().NotBeNull();
        body!.LatestVersion.Should().BeNull();
        body.DownloadUrl.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Version_returns_configured_value()
    {
        using var factory = new ConfiguredFactory();
        var resp = await factory.CreateClient().GetAsync("/api/app/version");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await resp.Content.ReadFromJsonAsync<AppVersionResponse>();
        body!.LatestVersion.Should().Be("9.9.9");
        body.DownloadUrl.Should().Be("https://dl.test/indir");
    }

    private sealed class ConfiguredFactory : ApiFactory
    {
        protected override IDictionary<string, string?> ExtraConfig => new Dictionary<string, string?>
        {
            ["AppRelease:LatestVersion"] = "9.9.9",
            ["AppRelease:DownloadUrl"] = "https://dl.test/indir",
        };
    }
}
