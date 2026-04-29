using System.Net;
using FluentAssertions;
using LiveDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace LiveDeck.LicenseServer.Tests.Pages;

public class AdminLoginPlaceholderTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public AdminLoginPlaceholderTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Get_admin_login_returns_200_anonymously()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/admin/login");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("LiveDeck Admin");
    }

    [Fact(Skip = "Index page added in Task 4 — re-enable then")]
    public async Task Get_admin_index_redirects_to_login_for_anonymous()
    {
        var options = new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        };
        var client = _factory.CreateClient(options);
        var resp = await client.GetAsync("/admin");
        // Cookie auth scheme: anonymous → 302 Redirect to /admin/login
        resp.StatusCode.Should().Be(HttpStatusCode.Redirect);
        resp.Headers.Location!.ToString().Should().StartWith("/admin/login");
    }
}
