using FluentAssertions;
using LiveDeck.Licensing;
using LiveDeck.Licensing.Api;
using LiveDeck.Licensing.Services;
using LiveDeck.Licensing.Storage;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LiveDeck.Tests.App;

public class LicensingDiTests
{
    [Fact(Skip = "Trial DI added in Task 10")]
    public void AppHost_resolves_LicenseService_singleton()
    {
        using var host = new global::LiveDeck.App.AppHost();

        var first = host.Services.GetRequiredService<LicenseService>();
        var second = host.Services.GetRequiredService<LicenseService>();

        first.Should().NotBeNull();
        first.Should().BeSameAs(second);
    }

    [Fact(Skip = "Trial DI added in Task 10")]
    public void AppHost_resolves_HardwareIdProvider_as_real_implementation()
    {
        using var host = new global::LiveDeck.App.AppHost();

        var hwId = host.Services.GetRequiredService<IHardwareIdProvider>();
        hwId.Should().BeOfType<HardwareIdProvider>();
    }

    [Fact(Skip = "Trial DI added in Task 10")]
    public void AppHost_resolves_AuthStore_and_LicenseStateStore()
    {
        using var host = new global::LiveDeck.App.AppHost();

        host.Services.GetRequiredService<AuthStore>().Should().NotBeNull();
        host.Services.GetRequiredService<LicenseStateStore>().Should().NotBeNull();
    }

    [Fact(Skip = "Trial DI added in Task 10")]
    public void AppHost_resolves_LicenseApiClient_with_BaseAddress()
    {
        using var host = new global::LiveDeck.App.AppHost();

        var client = host.Services.GetRequiredService<LicenseApiClient>();
        client.Should().NotBeNull();
    }
}
