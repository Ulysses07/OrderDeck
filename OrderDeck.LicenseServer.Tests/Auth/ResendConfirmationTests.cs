using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using LiveDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace LiveDeck.LicenseServer.Tests.Auth;

public class ResendConfirmationTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public ResendConfirmationTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Resend_for_unconfirmed_user_sends_new_email()
    {
        var client = _factory.CreateClient();
        var email = $"resend-{Guid.NewGuid():N}@example.com";
        await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            email, name = "Resend Test", password = "secret-password"
        });
        var countAfterRegister = _factory.Email.Sent.Count;

        var resp = await client.PostAsJsonAsync("/api/v1/auth/resend-confirmation", new { email });

        resp.StatusCode.Should().Be(HttpStatusCode.Accepted);
        _factory.Email.Sent.Count.Should().Be(countAfterRegister + 1);
    }

    [Fact]
    public async Task Resend_for_unknown_email_returns_202_silently()
    {
        var client = _factory.CreateClient();
        var countBefore = _factory.Email.Sent.Count;

        var resp = await client.PostAsJsonAsync("/api/v1/auth/resend-confirmation", new
        {
            email = $"never-{Guid.NewGuid():N}@example.com"
        });

        resp.StatusCode.Should().Be(HttpStatusCode.Accepted);
        _factory.Email.Sent.Count.Should().Be(countBefore);   // no email sent
    }
}
