using System.Net.Http.Headers;
using AngleSharp;
using AngleSharp.Html.Dom;
using LiveDeck.LicenseServer.Data;
using LiveDeck.LicenseServer.Services.Auth;
using LiveDeck.LicenseServer.Domain;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LiveDeck.LicenseServer.Tests.TestHelpers;

public static class AdminLoginHelper
{
    /// <summary>
    /// Creates an admin user (if missing) with a known password, then performs an
    /// anti-forgery-aware login POST. Returns an HttpClient with the auth cookie set.
    /// </summary>
    public static async Task<HttpClient> CreateLoggedInAdminClientAsync(
        this ApiFactory factory,
        string username = "admin",
        string password = "admin-password")
    {
        await EnsureAdminSeededAsync(factory, username, password);

        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });

        // GET login → grab anti-forgery token
        var getResp = await client.GetAsync("/admin/login");
        var html = await getResp.Content.ReadAsStringAsync();
        var token = ExtractAntiForgeryToken(html);

        // POST login
        var formData = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["Input.Username"] = username,
            ["Input.Password"] = password
        });
        var postResp = await client.PostAsync("/admin/login", formData);
        if (postResp.StatusCode != System.Net.HttpStatusCode.Redirect)
        {
            var body = await postResp.Content.ReadAsStringAsync();
            throw new InvalidOperationException($"Login failed (status={postResp.StatusCode}): {body}");
        }

        return client;
    }

    public static string ExtractAntiForgeryToken(string html)
    {
        var ctx = BrowsingContext.New(Configuration.Default);
        var doc = ctx.OpenAsync(req => req.Content(html)).GetAwaiter().GetResult();
        var input = doc.QuerySelector("input[name='__RequestVerificationToken']") as IHtmlInputElement;
        return input?.Value ?? throw new InvalidOperationException("No anti-forgery token found in HTML.");
    }

    /// <summary>Idempotently creates an admin user with the given password.</summary>
    public static async Task EnsureAdminSeededAsync(ApiFactory factory, string username, string password)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<PasswordHasher>();
        var existing = await db.AdminUsers.FirstOrDefaultAsync(a => a.Username == username);
        if (existing is not null) return;
        db.AdminUsers.Add(new AdminUser
        {
            Id = Guid.NewGuid(),
            Username = username,
            PasswordHash = hasher.Hash(password),
            CreatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
    }
}
