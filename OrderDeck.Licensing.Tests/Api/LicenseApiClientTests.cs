using FluentAssertions;
using OrderDeck.Licensing.Api;
using OrderDeck.Licensing.Api.Models;
using OrderDeck.Licensing.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.Licensing.Tests.Api;

public class LicenseApiClientTests
{
    private static (LicenseApiClient client, FakeHttpMessageHandler handler) BuildClient(
        Func<HttpRequestMessage, HttpResponseMessage> responder, string? token = null)
    {
        var handler = new FakeHttpMessageHandler(responder);
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://test.local") };
        if (token is not null)
            http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return (new LicenseApiClient(http), handler);
    }

    [Fact]
    public async Task LoginAsync_returns_token_on_200()
    {
        var (client, handler) = BuildClient(_ =>
            FakeHttpMessageHandler.Json(200, """{"token":"abc","expiresAt":"2026-05-06T12:00:00Z"}"""));

        var resp = await client.LoginAsync(new LoginRequest("user@example.com", "pw"));

        resp.Token.Should().Be("abc");
        handler.Requests[0].Method.Method.Should().Be("POST");
        handler.Requests[0].RequestUri!.AbsolutePath.Should().Be("/api/v1/auth/login");
    }

    [Fact]
    public async Task LoginAsync_throws_InvalidCredentials_on_401()
    {
        var (client, _) = BuildClient(_ =>
            FakeHttpMessageHandler.Problem(401, "invalid-credentials"));

        var act = async () => await client.LoginAsync(new LoginRequest("u", "p"));
        await act.Should().ThrowAsync<InvalidCredentialsException>();
    }

    [Fact]
    public async Task LoginAsync_throws_EmailNotConfirmed_on_403()
    {
        var (client, _) = BuildClient(_ =>
            FakeHttpMessageHandler.Problem(403, "email-not-confirmed"));

        var act = async () => await client.LoginAsync(new LoginRequest("u", "p"));
        await act.Should().ThrowAsync<EmailNotConfirmedException>();
    }

    [Fact]
    public async Task RegisterAsync_treats_201_and_202_as_success()
    {
        var (client, _) = BuildClient(_ => FakeHttpMessageHandler.Empty(201));
        await client.RegisterAsync(new RegisterRequest("u@x", "n", "p")); // no throw

        var (client2, _) = BuildClient(_ => FakeHttpMessageHandler.Empty(202));
        await client2.RegisterAsync(new RegisterRequest("u@x", "n", "p")); // no throw
    }

    [Fact]
    public async Task RegisterAsync_throws_Validation_on_400()
    {
        var (client, _) = BuildClient(_ =>
            FakeHttpMessageHandler.Problem(400, "password-too-short", "En az 8 karakter olmalı"));

        var act = async () => await client.RegisterAsync(new RegisterRequest("u@x", "n", "p"));
        var ex = await act.Should().ThrowAsync<ValidationException>();
        ex.Which.Code.Should().Be("password-too-short");
    }

    [Fact]
    public async Task ValidateAsync_returns_status()
    {
        var (client, _) = BuildClient(_ => FakeHttpMessageHandler.Json(200,
            """{"status":"active","expiresAt":"2027-04-29T00:00:00Z","remainingDays":365,"sku":"STD","slotInfo":{"used":1,"total":1,"thisDeviceActive":true}}"""));

        var resp = await client.ValidateAsync(new ValidateRequest("LDK-X", "fp"));

        resp.Should().NotBeNull();
        resp!.Status.Should().Be("active");
        resp.RemainingDays.Should().Be(365);
        resp.SlotInfo!.ThisDeviceActive.Should().BeTrue();
    }

    [Fact]
    public async Task ValidateAsync_returns_null_on_404()
    {
        var (client, _) = BuildClient(_ => FakeHttpMessageHandler.Empty(404));

        var resp = await client.ValidateAsync(new ValidateRequest("LDK-X", "fp"));
        resp.Should().BeNull();
    }

    [Fact]
    public async Task ActivateAsync_throws_SlotFull_on_409_with_slot_full_title()
    {
        var (client, _) = BuildClient(_ =>
            FakeHttpMessageHandler.Problem(409, "slot-full", "Slot dolu"));

        var act = async () => await client.ActivateAsync(new ActivateRequest("LDK-X", "fp", null));
        await act.Should().ThrowAsync<SlotFullException>();
    }

    [Fact]
    public async Task ActivateAsync_throws_LicenseRevoked_on_409_with_revoked_title()
    {
        var (client, _) = BuildClient(_ =>
            FakeHttpMessageHandler.Problem(409, "license-revoked"));

        var act = async () => await client.ActivateAsync(new ActivateRequest("LDK-X", "fp", null));
        await act.Should().ThrowAsync<LicenseRevokedException>();
    }

    [Fact]
    public async Task NetworkFailure_wraps_in_LicenseApiNetworkException()
    {
        var (client, _) = BuildClient(_ => throw new HttpRequestException("dns fail"));

        var act = async () => await client.LoginAsync(new LoginRequest("u", "p"));
        await act.Should().ThrowAsync<LicenseApiNetworkException>();
    }

    [Fact]
    public async Task GetMyLicensesAsync_returns_list()
    {
        var (client, _) = BuildClient(_ => FakeHttpMessageHandler.Json(200,
            """[{"licenseKey":"LDK-A","skuCode":"STD","expiresAt":"2027-01-01T00:00:00Z","revokedAt":null}]"""));

        var resp = await client.GetMyLicensesAsync();
        resp.Should().HaveCount(1);
        resp[0].LicenseKey.Should().Be("LDK-A");
    }

    [Fact]
    public async Task DeactivateAsync_treats_204_as_success()
    {
        var (client, _) = BuildClient(_ => FakeHttpMessageHandler.Empty(204));
        await client.DeactivateAsync(new DeactivateRequest("LDK-X", "fp")); // no throw
    }

    [Fact]
    public async Task HeartbeatAsync_returns_response_on_200()
    {
        var (client, _) = BuildClient(_ => FakeHttpMessageHandler.Json(200,
            """{"status":"active","expiresAt":"2027-01-01T00:00:00Z"}"""));

        var resp = await client.HeartbeatAsync(new HeartbeatRequest("LDK-X", "fp"));
        resp.Status.Should().Be("active");
    }

    [Fact]
    public async Task HeartbeatAsync_throws_when_404_not_activated()
    {
        var (client, _) = BuildClient(_ => FakeHttpMessageHandler.Problem(404, "not-activated"));

        var act = async () => await client.HeartbeatAsync(new HeartbeatRequest("LDK-X", "fp"));
        var ex = await act.Should().ThrowAsync<ValidationException>();
        ex.Which.Code.Should().Be("not-activated");
    }

    [Fact]
    public async Task SetAuthToken_attaches_bearer_to_subsequent_requests()
    {
        var (client, handler) = BuildClient(_ => FakeHttpMessageHandler.Json(200,
            """{"id":"00000000-0000-0000-0000-000000000000","email":"u","name":"n","emailConfirmedAt":null,"createdAt":"2026-01-01T00:00:00Z"}"""));

        client.SetAuthToken("test-token");
        await client.GetMeAsync();

        handler.Requests[0].Headers.Authorization!.Scheme.Should().Be("Bearer");
        handler.Requests[0].Headers.Authorization.Parameter.Should().Be("test-token");
    }

    [Fact]
    public async Task GetIntakeFormAsync_returns_null_on_404()
    {
        var (client, _) = BuildClient(_ => FakeHttpMessageHandler.Empty(404));

        var result = await client.GetIntakeFormAsync();
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetIntakeFormAsync_returns_dto_on_200()
    {
        var (client, _) = BuildClient(_ => FakeHttpMessageHandler.Json(200,
            """{"slug":"burak","whatsAppPhone":"+905551234567","customTitle":"Title","isActive":true,"formUrl":"https://x/r/burak"}"""));

        var result = await client.GetIntakeFormAsync();

        result.Should().NotBeNull();
        result!.Slug.Should().Be("burak");
        result.FormUrl.Should().Be("https://x/r/burak");
    }

    [Fact]
    public async Task UpsertIntakeFormAsync_uses_PUT_method()
    {
        var (client, handler) = BuildClient(_ => FakeHttpMessageHandler.Json(200,
            """{"slug":"new","whatsAppPhone":"+905551234567","customTitle":null,"isActive":true,"formUrl":"https://x/r/new"}"""));

        await client.UpsertIntakeFormAsync(new IntakeFormUpsertRequest("new", "+905551234567", null, true));

        handler.Requests[0].Method.Method.Should().Be("PUT");
        handler.Requests[0].RequestUri!.AbsolutePath.Should().Be("/api/v1/me/intake-form");
    }

    [Fact]
    public async Task GetFormSubmissionsAsync_returns_list_with_since_query_param()
    {
        var (client, handler) = BuildClient(_ => FakeHttpMessageHandler.Json(200,
            """[{"id":"00000000-0000-0000-0000-000000000001","username":"u","fullName":"n","address":"a","phone":"+905551111111","submittedAt":"2026-04-30T12:00:00Z"}]"""));

        var since = new DateTimeOffset(2026, 4, 30, 11, 0, 0, TimeSpan.Zero);
        var rows = await client.GetFormSubmissionsAsync(since, limit: 25);

        rows.Should().HaveCount(1);
        rows[0].Username.Should().Be("u");
        rows[0].Phone.Should().Be("+905551111111");
        handler.Requests[0].RequestUri!.AbsolutePath.Should().Be("/api/v1/me/form-submissions");
        handler.Requests[0].RequestUri.Query.Should().Contain("since=").And.Contain("limit=25");
    }
}
