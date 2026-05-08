using FluentAssertions;
using Xunit;

namespace OrderDeck.Licensing.Tests;

public class SmokeTests
{
    [Fact]
    public void LicensingOptions_default_values_are_sane()
    {
        var opt = new LicensingOptions();
        opt.ServerBaseUrl.Should().StartWith("https://");
        opt.RequestTimeoutSeconds.Should().BeGreaterThan(0);
        opt.OfflineGraceDays.Should().Be(14);
        // Was 24h; lowered to 1h so license expiry / server outage banners
        // surface within an hour instead of a day.
        opt.HeartbeatIntervalHours.Should().Be(1);
    }

    [Fact]
    public void LicenseStatus_active_and_offline_grace_are_writable()
    {
        LicenseStatus.Active.IsWritable().Should().BeTrue();
        LicenseStatus.OfflineGrace.IsWritable().Should().BeTrue();
    }

    [Fact]
    public void LicenseStatus_other_states_are_not_writable()
    {
        LicenseStatus.Initializing.IsWritable().Should().BeFalse();
        LicenseStatus.OfflineExpired.IsWritable().Should().BeFalse();
        LicenseStatus.ExpiredOnline.IsWritable().Should().BeFalse();
        LicenseStatus.Revoked.IsWritable().Should().BeFalse();
        LicenseStatus.NoLicense.IsWritable().Should().BeFalse();
    }

    [Fact]
    public void LicenseStatus_TrialActive_is_writable()
    {
        LicenseStatus.TrialActive.IsWritable().Should().BeTrue();
    }

    [Fact]
    public void LicenseStatus_TrialExpired_is_not_writable()
    {
        LicenseStatus.TrialExpired.IsWritable().Should().BeFalse();
    }

    [Fact]
    public void LicenseStatus_TrialActive_and_TrialExpired_are_trial_mode()
    {
        LicenseStatus.TrialActive.IsTrialMode().Should().BeTrue();
        LicenseStatus.TrialExpired.IsTrialMode().Should().BeTrue();
    }

    [Fact]
    public void LicenseStatus_non_trial_states_are_not_trial_mode()
    {
        LicenseStatus.Active.IsTrialMode().Should().BeFalse();
        LicenseStatus.OfflineGrace.IsTrialMode().Should().BeFalse();
        LicenseStatus.OfflineExpired.IsTrialMode().Should().BeFalse();
        LicenseStatus.ExpiredOnline.IsTrialMode().Should().BeFalse();
        LicenseStatus.Revoked.IsTrialMode().Should().BeFalse();
        LicenseStatus.NoLicense.IsTrialMode().Should().BeFalse();
        LicenseStatus.Initializing.IsTrialMode().Should().BeFalse();
    }
}
