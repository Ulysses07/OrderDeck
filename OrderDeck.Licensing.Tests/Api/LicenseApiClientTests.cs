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
        return (new LicenseApiClient(http, new OrderDeck.Licensing.Api.LicenseTokenStore()), handler);
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
        // This test exercises the production 2-arg ctor + LicenseAuthHandler
        // pipeline directly. The 1-arg BuildClient() helper above creates a
        // throwaway handler that's NOT chained into the message pipeline —
        // fine for everything except SetAuthToken assertions, which need the
        // handler to actually inject headers per request.
        var fakeInner = new FakeHttpMessageHandler(_ => FakeHttpMessageHandler.Json(200,
            """{"id":"00000000-0000-0000-0000-000000000000","email":"u","name":"n","emailConfirmedAt":null,"createdAt":"2026-01-01T00:00:00Z"}"""));
        var tokenStore = new LicenseTokenStore();
        var authHandler = new LicenseAuthHandler(tokenStore) { InnerHandler = fakeInner };
        var http = new HttpClient(authHandler) { BaseAddress = new Uri("https://test.local") };
        var client = new LicenseApiClient(http, tokenStore);

        client.SetAuthToken("test-token");
        await client.GetMeAsync();

        fakeInner.Requests[0].Headers.Authorization!.Scheme.Should().Be("Bearer");
        fakeInner.Requests[0].Headers.Authorization.Parameter.Should().Be("test-token");
    }

    [Fact]
    public async Task SetAuthToken_with_null_clears_authorization_on_subsequent_requests()
    {
        // Logout path: SetAuthToken(null) must drop the header so the next
        // request is anonymous (used by the LoginService.Logout best-effort revoke).
        const string customerJson = """{"id":"00000000-0000-0000-0000-000000000000","email":"u","name":"n","emailConfirmedAt":null,"createdAt":"2026-01-01T00:00:00Z"}""";
        var fakeInner = new FakeHttpMessageHandler(_ => FakeHttpMessageHandler.Json(200, customerJson));
        var tokenStore = new LicenseTokenStore();
        var authHandler = new LicenseAuthHandler(tokenStore) { InnerHandler = fakeInner };
        var http = new HttpClient(authHandler) { BaseAddress = new Uri("https://test.local") };
        var client = new LicenseApiClient(http, tokenStore);

        client.SetAuthToken("test-token");
        await client.GetMeAsync();
        fakeInner.Requests[0].Headers.Authorization.Should().NotBeNull();

        client.SetAuthToken(null);
        await client.GetMeAsync();
        fakeInner.Requests[1].Headers.Authorization.Should().BeNull();
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

    // ─── Onaylı WhatsApp şablonları ───────────────────────────────────

    private static readonly Guid WaTemplateLicenseId =
        Guid.Parse("22222222-2222-2222-2222-222222222222");

    /// <summary>
    /// WhatsApp hesabı bağlı olmayan lisansta sunucu 503 + "no-whatsapp-account"
    /// döner. Bu testin sabitlediği şey <b>hangi tipin</b> fırlatıldığı:
    /// ThrowMappedAsync 5xx'i LicenseApiUnknownException'a çeviriyor, yani
    /// çağıran HttpRequestException yakalayamaz. WhatsAppCloudSettingsViewModel
    /// tam da bunu yakalamaya çalıştığı için "hesap bağlı değil" uyarısı hiç
    /// görünmüyordu; kullanıcı yanlış yöne bakıyordu.
    /// </summary>
    [Fact]
    public async Task GetApprovedWhatsAppTemplatesAsync_throws_Unknown_with_503_when_account_not_linked()
    {
        var (client, _) = BuildClient(_ =>
            FakeHttpMessageHandler.Problem(503, "no-whatsapp-account", "WhatsApp hesabı bağlı değil"));

        var act = async () => await client.GetApprovedWhatsAppTemplatesAsync(WaTemplateLicenseId);

        (await act.Should().ThrowAsync<LicenseApiUnknownException>())
            .Which.StatusCode.Should().Be(503);
    }

    [Fact]
    public async Task GetApprovedWhatsAppTemplatesAsync_returns_templates_on_200()
    {
        var (client, handler) = BuildClient(_ => FakeHttpMessageHandler.Json(200, """
            [{"name":"siparis_onay","language":"tr","category":"UTILITY","headerText":null,
              "bodyText":"Merhaba {{1}}, siparisin hazir.","footerText":null,"buttons":[],
              "parameterCount":1,"parameterExamples":["Ayse"],"unsupportedReason":null}]
            """));

        var rows = await client.GetApprovedWhatsAppTemplatesAsync(WaTemplateLicenseId);

        rows.Should().HaveCount(1);
        rows[0].Name.Should().Be("siparis_onay");
        rows[0].BodyText.Should().Be("Merhaba {{1}}, siparisin hazir.");
        rows[0].ParameterCount.Should().Be(1);
        handler.Requests[0].Method.Method.Should().Be("GET");
        handler.Requests[0].RequestUri!.AbsolutePath
            .Should().Be($"/api/v1/licenses/{WaTemplateLicenseId}/whatsapp/approved-templates");
    }

    // ─── Toplu SMS ────────────────────────────────────────────────────

    private static readonly Guid SmsLicenseId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public async Task GetSmsBalanceAsync_returns_credits()
    {
        var (client, handler) = BuildClient(_ => FakeHttpMessageHandler.Json(200,
            """{"creditsRemaining":420,"updatedAt":"2026-06-14T10:00:00Z"}"""));

        var resp = await client.GetSmsBalanceAsync(SmsLicenseId);

        resp.CreditsRemaining.Should().Be(420);
        handler.Requests[0].Method.Method.Should().Be("GET");
        handler.Requests[0].RequestUri!.AbsolutePath
            .Should().Be($"/api/v1/licenses/{SmsLicenseId}/sms/balance");
    }

    [Fact]
    public async Task PreviewSmsCampaignAsync_posts_body_and_parses_response()
    {
        var (client, handler) = BuildClient(_ => FakeHttpMessageHandler.Json(200,
            """{"recipientCount":12,"segmentsPerMessage":2,"totalCredits":24,"creditsRemaining":100,"sufficient":true}"""));

        var resp = await client.PreviewSmsCampaignAsync(SmsLicenseId, new SmsPreviewRequest("Merhaba"));

        resp.RecipientCount.Should().Be(12);
        resp.SegmentsPerMessage.Should().Be(2);
        resp.TotalCredits.Should().Be(24);
        resp.Sufficient.Should().BeTrue();
        handler.Requests[0].Method.Method.Should().Be("POST");
        handler.Requests[0].RequestUri!.AbsolutePath
            .Should().Be($"/api/v1/licenses/{SmsLicenseId}/sms-campaigns/preview");
    }

    [Fact]
    public async Task CreateSmsCampaignAsync_returns_campaign_id()
    {
        var campaignId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var (client, handler) = BuildClient(_ => FakeHttpMessageHandler.Json(200,
            $$"""{"campaignId":"{{campaignId}}","recipientCount":5,"totalCredits":5}"""));

        var resp = await client.CreateSmsCampaignAsync(SmsLicenseId, new SmsCreateRequest("Kampanya"));

        resp.CampaignId.Should().Be(campaignId);
        resp.RecipientCount.Should().Be(5);
        handler.Requests[0].Method.Method.Should().Be("POST");
        handler.Requests[0].RequestUri!.AbsolutePath
            .Should().Be($"/api/v1/licenses/{SmsLicenseId}/sms-campaigns");
    }

    [Fact]
    public async Task GetSmsCampaignStatusAsync_parses_counts()
    {
        var campaignId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var (client, handler) = BuildClient(_ => FakeHttpMessageHandler.Json(200,
            $$"""{"campaignId":"{{campaignId}}","status":"completed","recipientCount":4,"sent":3,"failed":1,"skipped":0,"creditsRefunded":1,"createdAt":"2026-06-14T10:00:00Z","completedAt":"2026-06-14T10:01:00Z"}"""));

        var resp = await client.GetSmsCampaignStatusAsync(SmsLicenseId, campaignId);

        resp.Status.Should().Be("completed");
        resp.Sent.Should().Be(3);
        resp.Failed.Should().Be(1);
        handler.Requests[0].RequestUri!.AbsolutePath
            .Should().Be($"/api/v1/licenses/{SmsLicenseId}/sms-campaigns/{campaignId}");
    }

    [Fact]
    public async Task ListSmsCampaignsAsync_returns_list_with_take_query()
    {
        var (client, handler) = BuildClient(_ => FakeHttpMessageHandler.Json(200,
            """[{"campaignId":"44444444-4444-4444-4444-444444444444","status":"completed","messagePreview":"Indirim","recipientCount":3,"sent":3,"failed":0,"skipped":0,"creditsRefunded":0,"createdAt":"2026-06-14T10:00:00Z","completedAt":"2026-06-14T10:01:00Z"}]"""));

        var rows = await client.ListSmsCampaignsAsync(SmsLicenseId, take: 20);

        rows.Should().HaveCount(1);
        rows[0].MessagePreview.Should().Be("Indirim");
        rows[0].Sent.Should().Be(3);
        handler.Requests[0].Method.Method.Should().Be("GET");
        handler.Requests[0].RequestUri!.AbsolutePath
            .Should().Be($"/api/v1/licenses/{SmsLicenseId}/sms-campaigns");
        handler.Requests[0].RequestUri.Query.Should().Contain("take=20");
    }
}
