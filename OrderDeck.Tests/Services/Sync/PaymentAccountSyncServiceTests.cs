using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using OrderDeck.App.Services.Sync;
using OrderDeck.Core.Settings;
using OrderDeck.Licensing.Api;
using OrderDeck.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.Tests.Services.Sync;

public sealed class PaymentAccountSyncServiceTests
{
    private sealed class FakeLicenseProvider : ICurrentLicenseProvider
    {
        public string? CurrentLicenseKey { get; set; }
    }

    private static readonly Guid TestLicenseId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private const string TestLicenseKey = "PAY-ACCT-TEST-KEY";

    private static string LicensesJson() =>
        $"[{{\"id\":\"{TestLicenseId}\",\"licenseKey\":\"{TestLicenseKey}\"}}]";

    private sealed record Fixture(
        PaymentAccountSyncService Svc,
        SettingsStore Store,
        AppSettings Settings,
        FakeLicenseProvider License,
        List<(HttpMethod Method, string Path, string? Body)> Requests);

    private static Fixture Build(
        Func<HttpRequestMessage, HttpResponseMessage> responder,
        bool seedLicense = true)
    {
        var settingsPath = Path.Combine(Path.GetTempPath(), $"pa-settings-{Guid.NewGuid():N}.json");
        var store = new SettingsStore(settingsPath);
        var settings = store.Load();

        var requests = new List<(HttpMethod, string, string?)>();
        var handler = new FakeHttpMessageHandler(req =>
        {
            var body = req.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
            requests.Add((req.Method, req.RequestUri!.PathAndQuery, body));
            return responder(req);
        });
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://test.local") };
        var api  = new LicenseApiClient(http, new LicenseTokenStore());

        var licenseProvider = new FakeLicenseProvider();
        if (seedLicense) licenseProvider.CurrentLicenseKey = TestLicenseKey;

        var svc = new PaymentAccountSyncService(
            api, store, licenseProvider, NullLogger<PaymentAccountSyncService>.Instance);

        return new Fixture(svc, store, settings, licenseProvider, requests);
    }

    // Helper: wire responder that serves /me/licenses then 204 for payment-account
    private static HttpResponseMessage DefaultResponder(HttpRequestMessage req)
    {
        var path = req.RequestUri!.PathAndQuery;
        if (path.StartsWith("/api/v1/me/licenses"))
            return FakeHttpMessageHandler.Json(200, LicensesJson());
        if (path.Contains("/payment-account"))
            return FakeHttpMessageHandler.Empty(204);
        return FakeHttpMessageHandler.Empty(404);
    }

    [Fact]
    public async Task Sync_no_license_skips()
    {
        var fx = Build(_ => FakeHttpMessageHandler.Json(200, LicensesJson()), seedLicense: false);

        await fx.Svc.SyncIfChangedAsync(CancellationToken.None);

        fx.Requests.Should().BeEmpty("no HTTP calls when no license key");
    }

    [Fact]
    public async Task Sync_first_call_sends_values()
    {
        var fx = Build(DefaultResponder);
        var settings = fx.Store.Load();
        settings.Payment.Iban          = "TR330006100519786457841326";
        settings.Payment.AccountHolder = "Ahmet Yıldız";
        fx.Store.Save(settings);

        await fx.Svc.SyncIfChangedAsync(CancellationToken.None);

        fx.Requests.Should().Contain(r =>
            r.Path.Contains("/payment-account") && r.Method == HttpMethod.Post,
            "should POST to payment-account endpoint");
    }

    [Fact]
    public async Task Sync_no_change_does_not_call_api_again()
    {
        var fx = Build(DefaultResponder);
        var settings = fx.Store.Load();
        settings.Payment.Iban          = "TR330006100519786457841326";
        settings.Payment.AccountHolder = "Ahmet Yıldız";
        fx.Store.Save(settings);

        // First call → sends
        await fx.Svc.SyncIfChangedAsync(CancellationToken.None);
        var afterFirst = fx.Requests.Count;

        // Second call with identical values → no-op
        await fx.Svc.SyncIfChangedAsync(CancellationToken.None);

        fx.Requests.Should().HaveCount(afterFirst, "no new requests when values unchanged");
    }

    [Fact]
    public async Task Sync_change_sends_new_values()
    {
        var postCount = 0;
        var fx = Build(req =>
        {
            var path = req.RequestUri!.PathAndQuery;
            if (path.StartsWith("/api/v1/me/licenses"))
                return FakeHttpMessageHandler.Json(200, LicensesJson());
            if (path.Contains("/payment-account"))
            {
                Interlocked.Increment(ref postCount);
                return FakeHttpMessageHandler.Empty(204);
            }
            return FakeHttpMessageHandler.Empty(404);
        });

        var s1 = fx.Store.Load();
        s1.Payment.Iban = "TR330006100519786457841326";
        fx.Store.Save(s1);
        await fx.Svc.SyncIfChangedAsync(CancellationToken.None);

        var s2 = fx.Store.Load();
        s2.Payment.Iban = "TR440006100519786457841327";
        fx.Store.Save(s2);
        await fx.Svc.SyncIfChangedAsync(CancellationToken.None);

        postCount.Should().Be(2, "two POSTs: one for each distinct IBAN");
    }

    [Fact]
    public async Task Sync_whitespace_iban_treated_as_null_and_equals_initial_state_so_noop()
    {
        // Blank IBAN/holder normalise to null. The service's initial in-memory cache is
        // also null/null, so there is no change → no POST on the first call.
        var fx = Build(DefaultResponder);

        var settings = fx.Store.Load();
        settings.Payment.Iban          = "   ";
        settings.Payment.AccountHolder = "  ";
        fx.Store.Save(settings);

        await fx.Svc.SyncIfChangedAsync(CancellationToken.None);

        // Only the /me/licenses call may fire (for license resolution), but
        // no payment-account POST should occur because null == null.
        fx.Requests.Should().NotContain(r => r.Path.Contains("/payment-account"),
            "whitespace values normalise to null which matches initial null cache → no-op");
    }

    [Fact]
    public async Task Sync_whitespace_iban_after_real_value_sends_null()
    {
        // First sync with a real IBAN, then overwrite with whitespace.
        // Service must detect the change (real → null) and POST with null iban.
        string? capturedBody = null;
        var postCount = 0;
        var fx = Build(req =>
        {
            var path = req.RequestUri!.PathAndQuery;
            if (path.StartsWith("/api/v1/me/licenses"))
                return FakeHttpMessageHandler.Json(200, LicensesJson());
            if (path.Contains("/payment-account"))
            {
                Interlocked.Increment(ref postCount);
                if (postCount == 2)
                    capturedBody = req.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
                return FakeHttpMessageHandler.Empty(204);
            }
            return FakeHttpMessageHandler.Empty(404);
        });

        var s1 = fx.Store.Load();
        s1.Payment.Iban = "TR330006100519786457841326";
        fx.Store.Save(s1);
        await fx.Svc.SyncIfChangedAsync(CancellationToken.None); // first POST: real IBAN

        var s2 = fx.Store.Load();
        s2.Payment.Iban = "   "; // overwrite with whitespace
        fx.Store.Save(s2);
        await fx.Svc.SyncIfChangedAsync(CancellationToken.None); // second POST: null iban

        postCount.Should().Be(2, "change from real to whitespace (null) must trigger a new POST");
        capturedBody.Should().NotBeNull();
        capturedBody!.Should().Contain("null",
            "whitespace IBAN should be serialised as null in the request body");
    }

    [Fact]
    public async Task Sync_api_failure_does_not_throw()
    {
        var fx = Build(req =>
        {
            var path = req.RequestUri!.PathAndQuery;
            if (path.StartsWith("/api/v1/me/licenses"))
                return FakeHttpMessageHandler.Json(200, LicensesJson());
            return FakeHttpMessageHandler.Empty(500);
        });

        var settings = fx.Store.Load();
        settings.Payment.Iban = "TR330006100519786457841326";
        fx.Store.Save(settings);

        // Must not throw — service logs warning and swallows
        var act = async () => await fx.Svc.SyncIfChangedAsync(CancellationToken.None);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Sync_api_failure_does_not_update_last_synced_values()
    {
        var postCount = 0;
        var fx = Build(req =>
        {
            var path = req.RequestUri!.PathAndQuery;
            if (path.StartsWith("/api/v1/me/licenses"))
                return FakeHttpMessageHandler.Json(200, LicensesJson());
            if (path.Contains("/payment-account"))
            {
                Interlocked.Increment(ref postCount);
                // Fail on first attempt, succeed on second
                return postCount == 1 ? FakeHttpMessageHandler.Empty(500) : FakeHttpMessageHandler.Empty(204);
            }
            return FakeHttpMessageHandler.Empty(404);
        });

        var settings = fx.Store.Load();
        settings.Payment.Iban = "TR330006100519786457841326";
        fx.Store.Save(settings);

        // First call: API fails → internal state NOT updated
        await fx.Svc.SyncIfChangedAsync(CancellationToken.None);

        // Second call: same IBAN → should retry because state was NOT cached on failure
        await fx.Svc.SyncIfChangedAsync(CancellationToken.None);

        postCount.Should().Be(2, "failed call must not advance cached state, so next call retries");
    }
}
