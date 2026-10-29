using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace OrderDeck.LicenseServer.Tests.TestHelpers;

/// <summary>
/// Ortak bakiye (spec 2026-10-11) testlerinin InMemory fabrikası: <c>PersonBalance:MinClientVersion</c> (Wh) ve
/// <c>SalesSync:MinClientVersion</c> (Ws) dolu ve eşit — Wh ⊇ Ws (§7.2-4). Kitin varsayılan User-Agent'ı
/// (<c>SalesCommandTestKit.WpfUserAgent</c>, 0.9.11) iki kapıyı da geçer. Uygulamanın günlükleri <see cref="Log"/>'da.
/// </summary>
public sealed class PersonBalanceConfiguredApiFactory : ApiFactory
{
    /// <summary>Testte Wh = Ws.</summary>
    public const string MinClientVersion = "0.9.11";

    public LogRecorderProvider Log { get; } = new();

    protected override IDictionary<string, string?> ExtraConfig => new Dictionary<string, string?>
    {
        ["PersonBalance:MinClientVersion"] = MinClientVersion,
        ["SalesSync:MinClientVersion"] = MinClientVersion,
    };

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureLogging(l => l.AddProvider(Log));
    }
}

/// <summary>
/// <see cref="PersonBalanceConfiguredApiFactory"/>'nin gerçek SQL Server hâli (Testcontainers): aynı ayarlar + kayıt
/// kancası (<see cref="RaceSaveHook"/>), okuma kancası (<see cref="ReadQueryHook"/>) ve günlük toplayıcı. Spec §8.2'nin
/// havuz kipi yarışları bu fabrikayla kurulur. Kancalar kurulmadıkça etkisizdir.
/// </summary>
public sealed class PersonBalanceRelationalApiFactory : RelationalApiFactory
{
    public PersonBalanceRelationalApiFactory(string connectionString) : base(connectionString) { }

    public RaceSaveHook Hook { get; } = new();

    public ReadQueryHook Queries { get; } = new();

    public LogRecorderProvider Log { get; } = new();

    protected override IDictionary<string, string?> ExtraConfig => new Dictionary<string, string?>
    {
        ["PersonBalance:MinClientVersion"] = PersonBalanceConfiguredApiFactory.MinClientVersion,
        ["SalesSync:MinClientVersion"] = PersonBalanceConfiguredApiFactory.MinClientVersion,
    };

    protected override void ConfigureDbContextOptions(DbContextOptionsBuilder opt) => opt.AddInterceptors(Hook, Queries);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureLogging(l => l.AddProvider(Log));
    }
}
