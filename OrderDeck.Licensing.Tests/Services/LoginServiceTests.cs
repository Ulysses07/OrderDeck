using FluentAssertions;
using OrderDeck.Licensing.Api;
using OrderDeck.Licensing.Api.Models;
using OrderDeck.Licensing.Services;
using OrderDeck.Licensing.Storage;
using OrderDeck.Licensing.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.Licensing.Tests.Services;

public sealed class LoginServiceTests : IDisposable
{
    private readonly string _dir;
    private readonly AuthStore _authStore;

    public LoginServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "OrderDeck.Licensing.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _authStore = new AuthStore(new EncryptedStore(), Path.Combine(_dir, "auth.dat"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private (LoginService svc, FakeHttpMessageHandler handler, LicenseApiClient api) Build(
        Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var handler = new FakeHttpMessageHandler(responder);
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://test.local") };
        var api = new LicenseApiClient(http, new OrderDeck.Licensing.Api.LicenseTokenStore());
        return (new LoginService(api, _authStore), handler, api);
    }

    [Fact]
    public async Task LoginAsync_persists_AuthRecord_with_token_and_me_data()
    {
        var customerId = Guid.NewGuid();
        var (svc, handler, _) = Build(req =>
        {
            if (req.RequestUri!.AbsolutePath == "/api/v1/auth/login")
                return FakeHttpMessageHandler.Json(200, """{"token":"jwt-abc","expiresAt":"2026-05-06T12:00:00Z"}""");
            if (req.RequestUri.AbsolutePath == "/api/v1/me")
                return FakeHttpMessageHandler.Json(200, $$"""{"id":"{{customerId}}","email":"user@example.com","name":"Test User","emailConfirmedAt":"2026-04-29T00:00:00Z","createdAt":"2026-04-01T00:00:00Z"}""");
            throw new InvalidOperationException("unexpected: " + req.RequestUri);
        });

        await svc.LoginAsync("user@example.com", "pw");

        _authStore.IsPresent.Should().BeTrue();
        var saved = _authStore.Load();
        saved.Should().NotBeNull();
        saved!.CustomerId.Should().Be(customerId);
        saved.Email.Should().Be("user@example.com");
        saved.Name.Should().Be("Test User");
        saved.Token.Should().Be("jwt-abc");
    }

    [Fact]
    public async Task LoginAsync_does_not_persist_when_credentials_invalid()
    {
        var (svc, _, _) = Build(_ => FakeHttpMessageHandler.Problem(401, "invalid-credentials"));

        var act = async () => await svc.LoginAsync("u", "wrong");
        await act.Should().ThrowAsync<InvalidCredentialsException>();

        _authStore.IsPresent.Should().BeFalse();
    }

    [Fact]
    public async Task LoginAsync_does_not_persist_when_email_unconfirmed()
    {
        var (svc, _, _) = Build(_ => FakeHttpMessageHandler.Problem(403, "email-not-confirmed"));

        var act = async () => await svc.LoginAsync("u", "p");
        await act.Should().ThrowAsync<EmailNotConfirmedException>();

        _authStore.IsPresent.Should().BeFalse();
    }

    [Fact]
    public async Task RegisterAsync_calls_register_endpoint_and_does_not_persist_auth()
    {
        var (svc, handler, _) = Build(_ => FakeHttpMessageHandler.Empty(201));

        await svc.RegisterAsync("u@x.com", "User", "password123");

        handler.Requests[0].RequestUri!.AbsolutePath.Should().Be("/api/v1/auth/register");
        _authStore.IsPresent.Should().BeFalse();
    }

    [Fact]
    public async Task Logout_clears_auth_store_and_token()
    {
        var (svc, _, api) = Build(_ => throw new InvalidOperationException("should not be called"));
        _authStore.Save(new AuthRecord(Guid.NewGuid(), "e", "n", "tok", DateTimeOffset.UtcNow.AddDays(7)));
        api.SetAuthToken("tok");

        svc.Logout();

        _authStore.IsPresent.Should().BeFalse();
    }
}
