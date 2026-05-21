using System.Net;
using System.Text;
using FluentAssertions;
using OrderDeck.Licensing.Api;
using OrderDeck.Licensing.Api.Models;
using OrderDeck.Tests.TestHelpers;

namespace OrderDeck.Tests.Licensing;

public sealed class LicenseApiClientTests
{
    private static readonly Guid TestLicenseId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    // ─── Factory ──────────────────────────────────────────────────────────

    private static LicenseApiClient BuildClient(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var handler = new FakeHttpMessageHandler(responder);
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://test.local") };
        return new LicenseApiClient(http, new LicenseTokenStore());
    }

    // ─── GetShopperCodeAsync ───────────────────────────────────────────────

    [Fact]
    public async Task GetShopperCodeAsync_calls_panel_endpoint_and_parses_response()
    {
        var json = $$"""
            {
              "code": "royal",
              "updatedAt": "2026-05-20T10:00:00Z",
              "canChangeAt": "2026-05-27T10:00:00Z",
              "licenseId": "{{TestLicenseId}}"
            }
            """;

        var client = BuildClient(_ => FakeHttpMessageHandler.Json(200, json));

        var result = await client.GetShopperCodeAsync();

        result.Code.Should().Be("royal");
        result.UpdatedAt.Should().Be(DateTimeOffset.Parse("2026-05-20T10:00:00Z"));
        result.CanChangeAt.Should().Be(DateTimeOffset.Parse("2026-05-27T10:00:00Z"));
        result.LicenseId.Should().Be(TestLicenseId);
    }

    [Fact]
    public async Task GetShopperCodeAsync_returns_null_code_for_first_time()
    {
        var json = $$"""{"code":null,"updatedAt":null,"canChangeAt":null,"licenseId":"{{TestLicenseId}}"}""";
        var client = BuildClient(_ => FakeHttpMessageHandler.Json(200, json));

        var result = await client.GetShopperCodeAsync();

        result.Code.Should().BeNull();
        result.UpdatedAt.Should().BeNull();
        result.CanChangeAt.Should().BeNull();
        result.LicenseId.Should().Be(TestLicenseId);
    }

    [Fact]
    public async Task GetShopperCodeAsync_throws_on_404()
    {
        var client = BuildClient(_ => FakeHttpMessageHandler.Empty(404));

        var act = () => client.GetShopperCodeAsync();

        await act.Should().ThrowAsync<Exception>("404 should propagate as an error");
    }

    // ─── SetShopperCodeAsync ───────────────────────────────────────────────

    [Fact]
    public async Task SetShopperCodeAsync_sends_correct_body_and_parses_response()
    {
        var responseJson = $$"""
            {
              "code": "royal",
              "updatedAt": "2026-05-20T10:00:00Z",
              "canChangeAt": "2026-05-27T10:00:00Z",
              "licenseId": "{{TestLicenseId}}"
            }
            """;

        string? capturedBody = null;
        HttpMethod? capturedMethod = null;
        string? capturedPath = null;

        var client = BuildClient(req =>
        {
            capturedBody = req.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
            capturedMethod = req.Method;
            capturedPath = req.RequestUri?.PathAndQuery;
            return FakeHttpMessageHandler.Json(200, responseJson);
        });

        var result = await client.SetShopperCodeAsync("royal");

        capturedMethod.Should().Be(HttpMethod.Put);
        capturedPath.Should().Be("/api/panel/shopper-code");
        capturedBody.Should().Contain("\"code\"");
        capturedBody.Should().Contain("royal");

        result.Code.Should().Be("royal");
        result.LicenseId.Should().Be(TestLicenseId);
    }

    [Fact]
    public async Task SetShopperCodeAsync_throws_validation_exception_on_400()
    {
        var client = BuildClient(_ =>
            FakeHttpMessageHandler.Problem(400, "format"));

        var act = () => client.SetShopperCodeAsync("bad code!!");

        var ex = await act.Should().ThrowAsync<ShopperCodeValidationException>();
        ex.Which.ErrorCode.Should().Be("format");
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("length")]
    [InlineData("format")]
    [InlineData("reserved")]
    [InlineData("profanity")]
    [InlineData("cooldown")]
    [InlineData("taken")]
    public async Task SetShopperCodeAsync_throws_validation_exception_for_each_errorCode(string errorCode)
    {
        var client = BuildClient(_ =>
            FakeHttpMessageHandler.Problem(400, errorCode));

        var act = () => client.SetShopperCodeAsync("any");

        var ex = await act.Should().ThrowAsync<ShopperCodeValidationException>();
        ex.Which.ErrorCode.Should().Be(errorCode);
    }

    // ─── SyncPaymentAccountAsync ───────────────────────────────────────────

    [Fact]
    public async Task SyncPaymentAccountAsync_sends_post_with_iban_and_account_holder()
    {
        string? capturedBody = null;
        string? capturedPath = null;

        var client = BuildClient(req =>
        {
            capturedBody = req.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
            capturedPath = req.RequestUri?.PathAndQuery;
            return FakeHttpMessageHandler.Empty(204);
        });

        await client.SyncPaymentAccountAsync(TestLicenseId, "TR330006100519786457841326", "Ahmet Yilmaz");

        capturedPath.Should().Be($"/api/v1/licenses/{TestLicenseId}/payment-account");
        capturedBody.Should().Contain("TR330006100519786457841326");
        capturedBody.Should().Contain("Ahmet Yilmaz");
    }

    // ─── SyncWpfCustomersAsync ─────────────────────────────────────────────

    [Fact]
    public async Task SyncWpfCustomersAsync_sends_batch_and_parses_response()
    {
        var responseJson = """{"synced":3,"retroactiveMatches":1}""";
        string? capturedBody = null;
        string? capturedPath = null;

        var client = BuildClient(req =>
        {
            capturedBody = req.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
            capturedPath = req.RequestUri?.PathAndQuery;
            return FakeHttpMessageHandler.Json(200, responseJson);
        });

        var customers = new List<WpfCustomerSyncItem>
        {
            new(Guid.NewGuid(), "youtube", "user1", "User One",   "+905001111111", null,           DateTimeOffset.UtcNow),
            new(Guid.NewGuid(), "youtube", "user2", null,          null,           "Istanbul",     DateTimeOffset.UtcNow),
            new(Guid.NewGuid(), "twitch",  "user3", "User Three",  "+905002222222", "Ankara",      DateTimeOffset.UtcNow),
        };

        var result = await client.SyncWpfCustomersAsync(TestLicenseId, customers);

        capturedPath.Should().Be($"/api/v1/licenses/{TestLicenseId}/wpf-customers/sync");
        capturedBody.Should().Contain("\"customers\"");
        // All 3 usernames should appear in the serialized body
        capturedBody.Should().Contain("user1");
        capturedBody.Should().Contain("user2");
        capturedBody.Should().Contain("user3");

        result.Synced.Should().Be(3);
        result.RetroactiveMatches.Should().Be(1);
    }
}
