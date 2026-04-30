using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Auth;

public class RegisterTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public RegisterTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Register_creates_customer_and_sends_confirmation_email()
    {
        var client = _factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            email = $"u-{Guid.NewGuid():N}@example.com",
            name = "Test User",
            password = "secret-password"
        });

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        _factory.Email.Sent.Should().NotBeEmpty();
        var lastEmail = _factory.Email.Sent.Last();
        lastEmail.Subject.Should().Contain("doğrulayın");
        lastEmail.PlainBody.Should().Contain("/api/v1/auth/confirm-email/");
    }

    [Fact]
    public async Task Register_with_short_password_returns_400()
    {
        var client = _factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            email = $"u-{Guid.NewGuid():N}@example.com",
            name = "Test",
            password = "short"
        });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Register_with_existing_email_returns_202_silently_and_does_not_resend()
    {
        var client = _factory.CreateClient();
        var email = $"dup-{Guid.NewGuid():N}@example.com";

        // First registration
        await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            email, name = "First", password = "secret-password"
        });
        var sentCountBefore = _factory.Email.Sent.Count;

        // Second registration with same email
        var resp = await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            email, name = "Second", password = "another-password"
        });

        resp.StatusCode.Should().Be(HttpStatusCode.Accepted);
        _factory.Email.Sent.Count.Should().Be(sentCountBefore);   // no extra email
    }
}
