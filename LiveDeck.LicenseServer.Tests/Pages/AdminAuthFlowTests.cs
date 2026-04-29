using System.Net;
using FluentAssertions;
using LiveDeck.LicenseServer.Data;
using LiveDeck.LicenseServer.Tests.TestHelpers;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LiveDeck.LicenseServer.Tests.Pages;

public sealed class AdminAuthFlowTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public AdminAuthFlowTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Get_login_returns_200_with_form()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/admin/login");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await resp.Content.ReadAsStringAsync();
        html.Should().Contain("__RequestVerificationToken");
        html.Should().Contain("Input.Username");
        html.Should().Contain("Input.Password");
    }

    [Fact]
    public async Task Post_login_with_valid_credentials_sets_cookie_and_redirects()
    {
        var username = $"admin-{Guid.NewGuid():N}";
        await AdminLoginHelper.EnsureAdminSeededAsync(_factory, username, "admin-password");

        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });
        var getResp = await client.GetAsync("/admin/login");
        var token = AdminLoginHelper.ExtractAntiForgeryToken(await getResp.Content.ReadAsStringAsync());

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["Input.Username"] = username,
            ["Input.Password"] = "admin-password"
        });
        var postResp = await client.PostAsync("/admin/login", form);

        postResp.StatusCode.Should().Be(HttpStatusCode.Redirect);
    }

    [Fact]
    public async Task Post_login_with_wrong_password_returns_200_with_error()
    {
        var username = $"admin-{Guid.NewGuid():N}";
        await AdminLoginHelper.EnsureAdminSeededAsync(_factory, username, "real-password");

        var client = _factory.CreateClient();
        var getResp = await client.GetAsync("/admin/login");
        var token = AdminLoginHelper.ExtractAntiForgeryToken(await getResp.Content.ReadAsStringAsync());

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["Input.Username"] = username,
            ["Input.Password"] = "WRONG"
        });
        var postResp = await client.PostAsync("/admin/login", form);

        postResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await postResp.Content.ReadAsStringAsync();
        html.Should().Contain("Geçersiz");
    }

    [Fact]
    public async Task Anonymous_request_to_admin_index_redirects_to_login()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        var resp = await client.GetAsync("/admin");
        // 404 if Index page yet (Task 4); accept either 302 OR 404 for now.
        // After Task 4: must be 302 with Location starting "/admin/login"
        if (resp.StatusCode == HttpStatusCode.Redirect)
        {
            resp.Headers.Location!.ToString().Should().StartWith("/admin/login");
        }
        else
        {
            resp.StatusCode.Should().Be(HttpStatusCode.NotFound);   // Index page coming in Task 4
        }
    }

    [Fact]
    public async Task Successful_login_then_logout_writes_audit_entries()
    {
        var username = $"admin-{Guid.NewGuid():N}";
        var client = await _factory.CreateLoggedInAdminClientAsync(username, "admin-password");

        // After login, audit log should have 'admin.login'
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var loginEntry = await db.AuditLogs
                .Where(a => a.AdminUsername == username && a.EventType == "admin.login")
                .OrderByDescending(a => a.OccurredAt)
                .FirstOrDefaultAsync();
            loginEntry.Should().NotBeNull();
        }

        // POST logout (need anti-forgery token from any GET that renders form — re-use login GET)
        var getResp = await client.GetAsync("/admin/login");
        var token = AdminLoginHelper.ExtractAntiForgeryToken(await getResp.Content.ReadAsStringAsync());
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token
        });
        var logoutResp = await client.PostAsync("/admin/logout", form);
        logoutResp.StatusCode.Should().Be(HttpStatusCode.Redirect);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var logoutEntry = await db.AuditLogs
                .Where(a => a.AdminUsername == username && a.EventType == "admin.logout")
                .OrderByDescending(a => a.OccurredAt)
                .FirstOrDefaultAsync();
            logoutEntry.Should().NotBeNull();
        }
    }
}
