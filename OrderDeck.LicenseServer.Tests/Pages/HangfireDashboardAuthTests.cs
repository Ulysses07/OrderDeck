using System.Net;
using FluentAssertions;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Pages;

public sealed class HangfireDashboardAuthTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public HangfireDashboardAuthTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Anonymous_request_to_hangfire_dashboard_returns_unauthorized()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        var resp = await client.GetAsync("/hangfire");

        // Hangfire dashboard auth filter false dönerse 401 status döndürür
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Logged_in_admin_can_access_hangfire_dashboard()
    {
        var client = await _factory.CreateLoggedInAdminClientAsync($"admin-{Guid.NewGuid():N}");
        var resp = await client.GetAsync("/hangfire");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await resp.Content.ReadAsStringAsync();
        // Hangfire dashboard ana sayfası "Hangfire" string'i içerir
        html.Should().Contain("Hangfire");
    }
}
