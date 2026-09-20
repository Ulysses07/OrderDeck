using Microsoft.EntityFrameworkCore;

namespace OrderDeck.LicenseServer.Tests.TestHelpers;

/// <summary>
/// <see cref="ApiFactory"/> + paylaşılan bir <see cref="SaveHookInterceptor"/>.
/// Kanca dolduruluncaya kadar davranış <see cref="ApiFactory"/> ile
/// birebir aynıdır, bu yüzden bir test sınıfı bunu fixture olarak alıp
/// testlerinin yalnız birinde kancayı kullanabilir.
/// </summary>
public sealed class HookedApiFactory : ApiFactory
{
    public SaveHookInterceptor Hook { get; } = new();

    protected override void ConfigureDbContextOptions(DbContextOptionsBuilder opt)
        => opt.AddInterceptors(Hook);
}
