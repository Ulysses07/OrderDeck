using LiveDeck.LicenseServer.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace LiveDeck.LicenseServer.Tests.TestHelpers;

public sealed class ApiFactory : WebApplicationFactory<Program>
{
    private readonly string _dbName = Guid.NewGuid().ToString();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        builder.ConfigureServices(services =>
        {
            // Aggressively remove all descriptors related to LicenseDbContext
            // to avoid EF Core's internal service provider conflict between SqlServer and InMemory.
            var toRemove = services
                .Where(d =>
                    d.ServiceType.FullName != null &&
                    (d.ServiceType.FullName.Contains("LicenseDbContext") ||
                     d.ServiceType.FullName.Contains("DbContextOptions") ||
                     d.ImplementationType?.FullName?.Contains("LicenseDbContext") == true))
                .ToList();

            foreach (var d in toRemove)
                services.Remove(d);

            services.AddDbContext<LicenseDbContext>(opt =>
                opt.UseInMemoryDatabase(_dbName));
        });
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        var host = base.CreateHost(builder);

        // EnsureCreated after the host is fully built so DI is resolved correctly.
        // With InMemory provider, EnsureCreated applies HasData seeds from OnModelCreating.
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        db.Database.EnsureCreated();

        return host;
    }
}
