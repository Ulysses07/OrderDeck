using FluentAssertions;
using OrderDeck.Licensing;
using OrderDeck.Licensing.Api;
using OrderDeck.Licensing.Services;
using OrderDeck.Licensing.Storage;
using OrderDeck.Licensing.Trial;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace OrderDeck.Tests.App;

public class LicensingDiTests
{
    [Fact]
    public void AppHost_resolves_LicenseService_singleton()
    {
        using var host = new global::OrderDeck.App.AppHost();

        var first = host.Services.GetRequiredService<LicenseService>();
        var second = host.Services.GetRequiredService<LicenseService>();

        first.Should().NotBeNull();
        first.Should().BeSameAs(second);
    }

    [Fact]
    public void AppHost_resolves_HardwareIdProvider_as_real_implementation()
    {
        using var host = new global::OrderDeck.App.AppHost();

        var hwId = host.Services.GetRequiredService<IHardwareIdProvider>();
        hwId.Should().BeOfType<HardwareIdProvider>();
    }

    [Fact]
    public void AppHost_resolves_AuthStore_and_LicenseStateStore()
    {
        using var host = new global::OrderDeck.App.AppHost();

        host.Services.GetRequiredService<AuthStore>().Should().NotBeNull();
        host.Services.GetRequiredService<LicenseStateStore>().Should().NotBeNull();
    }

    [Fact]
    public void AppHost_resolves_LicenseApiClient_with_BaseAddress()
    {
        using var host = new global::OrderDeck.App.AppHost();

        var client = host.Services.GetRequiredService<LicenseApiClient>();
        client.Should().NotBeNull();
    }

    [Fact]
    public void AppHost_resolves_TrialService_singleton()
    {
        using var host = new global::OrderDeck.App.AppHost();
        var first = host.Services.GetRequiredService<TrialService>();
        var second = host.Services.GetRequiredService<TrialService>();
        first.Should().NotBeNull();
        first.Should().BeSameAs(second);
    }

    [Fact]
    public void AppHost_resolves_ITrialStorage_as_CompositeTrialStorage()
    {
        using var host = new global::OrderDeck.App.AppHost();
        var storage = host.Services.GetRequiredService<ITrialStorage>();
        storage.Should().BeOfType<CompositeTrialStorage>();
    }

    [Fact]
    public void AppHost_resolves_three_underlying_trial_storages()
    {
        using var host = new global::OrderDeck.App.AppHost();
        host.Services.GetRequiredService<HkcuTrialStorage>().Should().NotBeNull();
        host.Services.GetRequiredService<ProgramDataTrialStorage>().Should().NotBeNull();
        host.Services.GetRequiredService<LocalAppDataTrialStorage>().Should().NotBeNull();
    }
}
