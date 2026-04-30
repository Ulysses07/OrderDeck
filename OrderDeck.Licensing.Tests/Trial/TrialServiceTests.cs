using FluentAssertions;
using LiveDeck.Licensing;
using LiveDeck.Licensing.Tests.TestHelpers;
using LiveDeck.Licensing.Trial;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace LiveDeck.Licensing.Tests.Trial;

public class TrialServiceTests
{
    private sealed class FakeStorage : ITrialStorage
    {
        public string Name => "fake";
        public TrialRecord? Stored { get; set; }
        public TrialRecord? TryRead() => Stored;
        public void Write(TrialRecord r) => Stored = r;
        public void Clear() { Stored = null; }
    }

    private static (TrialService svc, FakeStorage storage, FakeHardwareIdProvider hw) Build(
        DateTimeOffset now, int trialDays = 14)
    {
        var storage = new FakeStorage();
        var hw = new FakeHardwareIdProvider { Id = "current-hw" };
        var opts = Options.Create(new LicensingOptions { TrialDurationDays = trialDays });
        var svc = new TrialService(storage, hw, opts, () => now, NullLogger<TrialService>.Instance);
        return (svc, storage, hw);
    }

    [Fact]
    public void GetState_returns_NoTrial_when_storage_empty()
    {
        var (svc, _, _) = Build(DateTimeOffset.UtcNow);
        svc.GetState().Should().Be(TrialState.NoTrial.Instance);
    }

    [Fact]
    public void GetState_returns_Active_when_record_within_window_and_hw_matches()
    {
        var now = new DateTimeOffset(2026, 4, 29, 12, 0, 0, TimeSpan.Zero);
        var (svc, storage, _) = Build(now);
        storage.Stored = new TrialRecord(now.AddDays(-3), now.AddDays(11), "current-hw", 1);

        var state = svc.GetState();
        state.Should().BeOfType<TrialState.Active>();
        ((TrialState.Active)state).RemainingDays.Should().Be(11);
    }

    [Fact]
    public void GetState_returns_Expired_when_record_exceeded_expiry()
    {
        var now = new DateTimeOffset(2026, 5, 20, 12, 0, 0, TimeSpan.Zero);
        var (svc, storage, _) = Build(now);
        var expiresAt = now.AddDays(-7);
        storage.Stored = new TrialRecord(expiresAt.AddDays(-14), expiresAt, "current-hw", 1);

        var state = svc.GetState();
        state.Should().BeOfType<TrialState.Expired>();
        ((TrialState.Expired)state).ExpiredAt.Should().Be(expiresAt);
    }

    [Fact]
    public void GetState_returns_Expired_when_hardware_fingerprint_mismatch()
    {
        var now = DateTimeOffset.UtcNow;
        var (svc, storage, _) = Build(now);
        storage.Stored = new TrialRecord(now.AddDays(-3), now.AddDays(11), "DIFFERENT-HW", 1);

        var state = svc.GetState();
        state.Should().BeOfType<TrialState.Expired>();
    }

    [Fact]
    public void StartNewTrial_writes_record_and_returns_Active()
    {
        var now = new DateTimeOffset(2026, 4, 29, 12, 0, 0, TimeSpan.Zero);
        var (svc, storage, _) = Build(now, trialDays: 14);

        var state = svc.StartNewTrial();

        state.Should().BeOfType<TrialState.Active>();
        ((TrialState.Active)state).RemainingDays.Should().Be(14);
        storage.Stored.Should().NotBeNull();
        storage.Stored!.HardwareFingerprint.Should().Be("current-hw");
        storage.Stored.ExpiresAt.Should().Be(now.AddDays(14));
    }

    [Fact]
    public void StartNewTrial_uses_TrialDurationDays_from_options()
    {
        var now = DateTimeOffset.UtcNow;
        var (svc, storage, _) = Build(now, trialDays: 7);

        svc.StartNewTrial();

        storage.Stored!.ExpiresAt.Should().BeCloseTo(now.AddDays(7), TimeSpan.FromSeconds(1));
    }
}
