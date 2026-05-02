using FluentAssertions;
using OrderDeck.Licensing.Api;
using OrderDeck.Licensing.Services;
using OrderDeck.Licensing.Storage;
using OrderDeck.Licensing.Tests.TestHelpers;
using OrderDeck.Licensing.Trial;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace OrderDeck.Licensing.Tests.Services;

public sealed class LicenseServiceTests : IDisposable
{
    private readonly string _dir;
    private readonly AuthStore _authStore;
    private readonly LicenseStateStore _licenseStore;
    private readonly FakeHardwareIdProvider _hwId = new();
    private readonly IOptions<LicensingOptions> _opts =
        Options.Create(new LicensingOptions { OfflineGraceDays = 14 });

    public LicenseServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "OrderDeck.Licensing.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        var enc = new EncryptedStore();
        _authStore = new AuthStore(enc, Path.Combine(_dir, "auth.dat"));
        _licenseStore = new LicenseStateStore(enc, Path.Combine(_dir, "license.dat"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private LicenseService Build(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var handler = new FakeHttpMessageHandler(responder);
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://test.local") };
        var api = new LicenseApiClient(http, new OrderDeck.Licensing.Api.LicenseAuthHandler());
        var trialStorage = new FakeTrialStorageStub();
        var trialOpts = Options.Create(new LicensingOptions { TrialDurationDays = 14 });
        var trial = new TrialService(trialStorage, _hwId, trialOpts, () => DateTimeOffset.UtcNow, NullLogger<TrialService>.Instance);
        return new LicenseService(api, _authStore, _licenseStore, _hwId, _opts, trial, NullLogger<LicenseService>.Instance);
    }

    private sealed class FakeTrialStorageStub : ITrialStorage
    {
        public string Name => "stub";
        public TrialRecord? TryRead() => null;
        public void Write(TrialRecord r) { }
        public void Clear() { }
    }

    private void SeedAuth(DateTimeOffset? tokenExp = null) =>
        _authStore.Save(new AuthRecord(
            CustomerId: Guid.NewGuid(),
            Email: "u@x",
            Name: "u",
            Token: "tok",
            TokenExpiresAt: tokenExp ?? DateTimeOffset.UtcNow.AddDays(7)));

    private void SeedLicense(DateTimeOffset? lastSuccessful = null, string status = "Active") =>
        _licenseStore.Save(new LicenseRecord(
            LicenseKey: "LDK-X",
            SkuCode: "STD",
            ExpiresAt: DateTimeOffset.UtcNow.AddDays(365),
            RemainingDaysAtLastCheck: 365,
            LastValidatedAt: DateTimeOffset.UtcNow,
            LastSuccessfulOnlineAt: lastSuccessful ?? DateTimeOffset.UtcNow,
            LastKnownStatus: status));

    // ─── Initialize: no auth ──────────────────────────────────────────

    [Fact]
    public async Task Initialize_with_no_auth_starts_trial_and_sets_TrialActive()
    {
        var svc = Build(_ => throw new InvalidOperationException("should not call api"));

        await svc.InitializeAsync();

        // Phase 4c: no auth → trial path → new trial started
        svc.CurrentStatus.Should().Be(LicenseStatus.TrialActive);
        svc.JustStartedTrial.Should().BeTrue();
    }

    [Fact]
    public async Task Initialize_with_expired_token_clears_auth_and_starts_trial()
    {
        SeedAuth(tokenExp: DateTimeOffset.UtcNow.AddDays(-1));
        var svc = Build(_ => throw new InvalidOperationException("should not call api"));

        await svc.InitializeAsync();

        // Phase 4c: expired token → trial path → new trial started
        svc.CurrentStatus.Should().Be(LicenseStatus.TrialActive);
        _authStore.IsPresent.Should().BeFalse();
    }

    // ─── Initialize: auth + no license cache ──────────────────────────

    [Fact]
    public async Task Initialize_with_auth_but_no_license_cache_sets_NoLicense()
    {
        SeedAuth();
        var svc = Build(_ => throw new InvalidOperationException("should not call api when no license"));

        await svc.InitializeAsync();

        svc.CurrentStatus.Should().Be(LicenseStatus.NoLicense);
    }

    // ─── Initialize: validate paths ────────────────────────────────────

    [Fact]
    public async Task Initialize_active_response_sets_Active_and_persists_license()
    {
        SeedAuth();
        SeedLicense();
        var svc = Build(_ => FakeHttpMessageHandler.Json(200,
            """{"status":"active","expiresAt":"2027-04-29T00:00:00Z","remainingDays":365,"sku":"STD","slotInfo":{"used":1,"total":1,"thisDeviceActive":true}}"""));

        await svc.InitializeAsync();

        svc.CurrentStatus.Should().Be(LicenseStatus.Active);
        var saved = _licenseStore.Load();
        saved!.LastKnownStatus.Should().Be("active");
    }

    [Fact]
    public async Task Initialize_revoked_response_sets_Revoked()
    {
        SeedAuth();
        SeedLicense();
        var svc = Build(_ => FakeHttpMessageHandler.Json(200,
            """{"status":"revoked","expiresAt":"2027-01-01T00:00:00Z","remainingDays":0,"sku":"STD","slotInfo":null}"""));

        await svc.InitializeAsync();

        svc.CurrentStatus.Should().Be(LicenseStatus.Revoked);
    }

    [Fact]
    public async Task Initialize_expired_response_sets_ExpiredOnline()
    {
        SeedAuth();
        SeedLicense();
        var svc = Build(_ => FakeHttpMessageHandler.Json(200,
            """{"status":"expired","expiresAt":"2024-01-01T00:00:00Z","remainingDays":0,"sku":"STD","slotInfo":null}"""));

        await svc.InitializeAsync();

        svc.CurrentStatus.Should().Be(LicenseStatus.ExpiredOnline);
    }

    [Fact]
    public async Task Initialize_notactivated_response_clears_license_and_sets_NoLicense()
    {
        SeedAuth();
        SeedLicense();
        var svc = Build(_ => FakeHttpMessageHandler.Json(200,
            """{"status":"notactivated","expiresAt":"2027-01-01T00:00:00Z","remainingDays":300,"sku":"STD","slotInfo":{"used":1,"total":1,"thisDeviceActive":false}}"""));

        await svc.InitializeAsync();

        svc.CurrentStatus.Should().Be(LicenseStatus.NoLicense);
        _licenseStore.IsPresent.Should().BeFalse();
    }

    // ─── Initialize: offline grace ────────────────────────────────────

    [Fact]
    public async Task Initialize_network_fail_inside_grace_window_sets_OfflineGrace()
    {
        SeedAuth();
        SeedLicense(lastSuccessful: DateTimeOffset.UtcNow.AddDays(-7));
        var svc = Build(_ => throw new HttpRequestException("dns fail"));

        await svc.InitializeAsync();

        svc.CurrentStatus.Should().Be(LicenseStatus.OfflineGrace);
    }

    [Fact]
    public async Task Initialize_network_fail_outside_grace_window_sets_OfflineExpired()
    {
        SeedAuth();
        SeedLicense(lastSuccessful: DateTimeOffset.UtcNow.AddDays(-15));
        var svc = Build(_ => throw new HttpRequestException("dns fail"));

        await svc.InitializeAsync();

        svc.CurrentStatus.Should().Be(LicenseStatus.OfflineExpired);
    }

    // ─── Initialize: 401 token expired by server ───────────────────────

    [Fact]
    public async Task Initialize_server_401_clears_auth_and_sets_NoLicense()
    {
        SeedAuth();
        SeedLicense();
        var svc = Build(_ => FakeHttpMessageHandler.Problem(401, "token-expired"));

        await svc.InitializeAsync();

        svc.CurrentStatus.Should().Be(LicenseStatus.NoLicense);
        _authStore.IsPresent.Should().BeFalse();
    }

    // ─── ActivateAsync ─────────────────────────────────────────────────

    [Fact]
    public async Task ActivateAsync_persists_license_record_and_sets_Active()
    {
        SeedAuth();
        var responder = (HttpRequestMessage req) =>
        {
            if (req.RequestUri!.AbsolutePath == "/api/v1/licenses/activate")
                return FakeHttpMessageHandler.Json(201,
                    $$"""{"activationId":"{{Guid.NewGuid()}}","expiresAt":"2027-04-29T00:00:00Z"}""");
            if (req.RequestUri.AbsolutePath == "/api/v1/licenses/validate")
                return FakeHttpMessageHandler.Json(200,
                    """{"status":"active","expiresAt":"2027-04-29T00:00:00Z","remainingDays":365,"sku":"STD","slotInfo":{"used":1,"total":1,"thisDeviceActive":true}}""");
            throw new InvalidOperationException(req.RequestUri.ToString());
        };
        var svc = Build(responder);

        await svc.ActivateAsync("LDK-NEW", machineName: "PC-1");

        svc.CurrentStatus.Should().Be(LicenseStatus.Active);
        var saved = _licenseStore.Load();
        saved.Should().NotBeNull();
        saved!.LicenseKey.Should().Be("LDK-NEW");
    }

    [Fact]
    public async Task ActivateAsync_throws_SlotFull_when_server_returns_409()
    {
        SeedAuth();
        var svc = Build(_ => FakeHttpMessageHandler.Problem(409, "slot-full"));

        var act = async () => await svc.ActivateAsync("LDK-NEW", machineName: null);
        await act.Should().ThrowAsync<SlotFullException>();
        svc.CurrentStatus.Should().NotBe(LicenseStatus.Active);
    }
}
