using FluentAssertions;
using Xunit;

namespace LiveDeck.Licensing.Tests;

public class SmokeTests
{
    [Fact]
    public void LicensingOptions_default_values_are_sane()
    {
        var opt = new LicensingOptions();
        opt.ServerBaseUrl.Should().StartWith("https://");
        opt.RequestTimeoutSeconds.Should().BeGreaterThan(0);
        opt.OfflineGraceDays.Should().Be(14);
        opt.HeartbeatIntervalHours.Should().Be(24);
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
}
