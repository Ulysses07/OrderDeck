using FluentAssertions;
using LiveDeck.Licensing.Api;
using LiveDeck.Licensing.Services;
using LiveDeck.Licensing.Storage;
using LiveDeck.Licensing.Tests.TestHelpers;
using LiveDeck.Licensing.Trial;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace LiveDeck.Licensing.Tests.Services;

public sealed class LicenseServiceTrialTests : IDisposable
{
    private readonly string _dir;
    private readonly AuthStore _authStore;
    private readonly LicenseStateStore _licenseStore;
    private readonly FakeTrialStorage _trialStorage = new();
    private readonly FakeHardwareIdProvider _hwId = new();
    private readonly IOptions<LicensingOptions> _opts =
        Options.Create(new LicensingOptions { OfflineGraceDays = 14, TrialDurationDays = 14 });

    public LicenseServiceTrialTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "LiveDeck.Licensing.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        var enc = new EncryptedStore();
        _authStore = new AuthStore(enc, Path.Combine(_dir, "auth.dat"));
        _licenseStore = new LicenseStateStore(enc, Path.Combine(_dir, "license.dat"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private sealed class FakeTrialStorage : ITrialStorage
    {
        public string Name => "fake";
        public TrialRecord? Stored { get; set; }
        public TrialRecord? TryRead() => Stored;
        public void Write(TrialRecord r) => Stored = r;
        public void Clear() { Stored = null; }
    }

    private (LicenseService svc, TrialService trial) Build(
        Func<HttpRequestMessage, HttpResponseMessage> responder,
        DateTimeOffset? now = null)
    {
        var handler = new FakeHttpMessageHandler(responder);
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://test.local") };
        var api = new LicenseApiClient(http);
        var clock = (Func<DateTimeOffset>)(() => now ?? DateTimeOffset.UtcNow);
        var trial = new TrialService(_trialStorage, _hwId, _opts, clock, NullLogger<TrialService>.Instance);
        var svc = new LicenseService(api, _authStore, _licenseStore, _hwId, _opts, trial, NullLogger<LicenseService>.Instance);
        return (svc, trial);
    }

    private void SeedAuth(DateTimeOffset? expiresAt = null) =>
        _authStore.Save(new AuthRecord(Guid.NewGuid(), "u@x", "u", "tok",
            expiresAt ?? DateTimeOffset.UtcNow.AddDays(7)));

    private void SeedLicense() =>
        _licenseStore.Save(new LicenseRecord("LDK", "STD",
            DateTimeOffset.UtcNow.AddDays(365), 365,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "Active"));

    // ─── Auth yok: trial path ─────────────────────────────────────────

    [Fact]
    public async Task Initialize_no_auth_no_trial_starts_new_trial_and_sets_TrialActive()
    {
        var (svc, _) = Build(_ => throw new InvalidOperationException("no http expected"));

        await svc.InitializeAsync();

        svc.CurrentStatus.Should().Be(LicenseStatus.TrialActive);
        svc.JustStartedTrial.Should().BeTrue();
        svc.CurrentTrial.Should().BeOfType<TrialState.Active>();
        _trialStorage.Stored.Should().NotBeNull();
    }

    [Fact]
    public async Task Initialize_no_auth_with_active_trial_record_continues_TrialActive()
    {
        _trialStorage.Stored = new TrialRecord(
            StartedAt: DateTimeOffset.UtcNow.AddDays(-3),
            ExpiresAt: DateTimeOffset.UtcNow.AddDays(11),
            HardwareFingerprint: _hwId.Id,
            Version: 1);
        var (svc, _) = Build(_ => throw new InvalidOperationException("no http"));

        await svc.InitializeAsync();

        svc.CurrentStatus.Should().Be(LicenseStatus.TrialActive);
        svc.JustStartedTrial.Should().BeFalse();
    }

    [Fact]
    public async Task Initialize_no_auth_with_expired_trial_record_sets_TrialExpired()
    {
        _trialStorage.Stored = new TrialRecord(
            StartedAt: DateTimeOffset.UtcNow.AddDays(-30),
            ExpiresAt: DateTimeOffset.UtcNow.AddDays(-16),
            HardwareFingerprint: _hwId.Id,
            Version: 1);
        var (svc, _) = Build(_ => throw new InvalidOperationException("no http"));

        await svc.InitializeAsync();

        svc.CurrentStatus.Should().Be(LicenseStatus.TrialExpired);
        svc.CurrentTrial.Should().BeOfType<TrialState.Expired>();
    }

    // ─── Auth var, license yok ───────────────────────────────────────

    [Fact]
    public async Task Initialize_auth_present_no_license_no_trial_sets_NoLicense()
    {
        SeedAuth();
        var (svc, _) = Build(_ => throw new InvalidOperationException("no http"));

        await svc.InitializeAsync();

        svc.CurrentStatus.Should().Be(LicenseStatus.NoLicense);
        // Trial başlatılmamalı
        _trialStorage.Stored.Should().BeNull();
    }

    [Fact]
    public async Task Initialize_auth_present_no_license_active_trial_sets_TrialActive()
    {
        SeedAuth();
        _trialStorage.Stored = new TrialRecord(
            StartedAt: DateTimeOffset.UtcNow.AddDays(-3),
            ExpiresAt: DateTimeOffset.UtcNow.AddDays(11),
            HardwareFingerprint: _hwId.Id,
            Version: 1);
        var (svc, _) = Build(_ => throw new InvalidOperationException("no http"));

        await svc.InitializeAsync();

        svc.CurrentStatus.Should().Be(LicenseStatus.TrialActive);
    }

    [Fact]
    public async Task Initialize_auth_present_no_license_expired_trial_sets_TrialExpired()
    {
        SeedAuth();
        _trialStorage.Stored = new TrialRecord(
            StartedAt: DateTimeOffset.UtcNow.AddDays(-30),
            ExpiresAt: DateTimeOffset.UtcNow.AddDays(-16),
            HardwareFingerprint: _hwId.Id,
            Version: 1);
        var (svc, _) = Build(_ => throw new InvalidOperationException("no http"));

        await svc.InitializeAsync();

        svc.CurrentStatus.Should().Be(LicenseStatus.TrialExpired);
    }

    // ─── Auth + license var (Phase 4b regression) ────────────────────

    [Fact]
    public async Task Initialize_auth_and_license_present_calls_validate_and_ignores_trial()
    {
        SeedAuth();
        SeedLicense();
        _trialStorage.Stored = new TrialRecord(
            StartedAt: DateTimeOffset.UtcNow.AddDays(-3),
            ExpiresAt: DateTimeOffset.UtcNow.AddDays(11),
            HardwareFingerprint: _hwId.Id,
            Version: 1);
        var (svc, _) = Build(_ => FakeHttpMessageHandler.Json(200,
            """{"status":"active","expiresAt":"2027-04-29T00:00:00Z","remainingDays":365,"sku":"STD","slotInfo":{"used":1,"total":1,"thisDeviceActive":true}}"""));

        await svc.InitializeAsync();

        svc.CurrentStatus.Should().Be(LicenseStatus.Active);  // license precedence
        svc.CurrentTrial.Should().BeNull();
    }

    // ─── Logout flow: trial preserve ─────────────────────────────────

    [Fact]
    public void Logout_clears_auth_and_license_but_preserves_trial_storage()
    {
        SeedAuth();
        SeedLicense();
        _trialStorage.Stored = new TrialRecord(
            DateTimeOffset.UtcNow.AddDays(-3),
            DateTimeOffset.UtcNow.AddDays(11),
            _hwId.Id, 1);
        var (svc, _) = Build(_ => throw new InvalidOperationException("no http"));

        svc.Logout();

        _authStore.IsPresent.Should().BeFalse();
        _licenseStore.IsPresent.Should().BeFalse();
        _trialStorage.Stored.Should().NotBeNull();  // trial NOT cleared
    }
}
