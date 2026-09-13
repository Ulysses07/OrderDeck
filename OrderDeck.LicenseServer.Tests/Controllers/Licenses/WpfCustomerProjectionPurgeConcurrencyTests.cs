using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Shoppers;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Licenses;

/// <summary>
/// Sync'in okumasıyla yazması arasına gerçek SQL purge commit'i sokar.
/// Testcontainers kullandığı için canlı yayın sırasında ÇALIŞTIRILMAZ.
/// </summary>
[Collection(SqlServerCollection.Name)]
[Trait("Category", "Testcontainers")]
public sealed class WpfCustomerProjectionPurgeConcurrencyTests : IAsyncLifetime
{
    private readonly SqlServerContainerFixture _sql;
    private readonly FirstProjectionSaveGate _gate = new();
    private TombstoneRelationalFactory _factory = null!;

    public WpfCustomerProjectionPurgeConcurrencyTests(SqlServerContainerFixture sql)
        => _sql = sql;

    public async Task InitializeAsync()
        => _factory = new TombstoneRelationalFactory(
            await _sql.CreateDatabaseAsync(), _gate);

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Purge_sync_yarisinda_tombstone_kisisel_veriyi_kazanir()
    {
        var (client, customerId, _) =
            await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);
        var seed = await SeedAsync(customerId);

        var syncTask = client.PostAsJsonAsync(
            $"/api/v1/licenses/{seed.LicenseId}/wpf-customers/sync",
            new
            {
                customers = new[]
                {
                    new
                    {
                        id = seed.ProjectionId,
                        platform = "youtube",
                        username = "tombstone-race",
                        fullName = "Bayat kişisel ad",
                        phone = seed.Phone,
                        address = "Bayat kişisel adres",
                        updatedAt = DateTimeOffset.UtcNow,
                    }
                }
            });

        try
        {
            await _gate.Arrived.WaitAsync(TimeSpan.FromSeconds(30));
            using var purgeScope = _factory.Services.CreateScope();
            var purge = purgeScope.ServiceProvider
                .GetRequiredService<ShopperPurgeService>();
            (await purge.PurgeAsync(seed.ShopperId, default)).Should().NotBeNull();
        }
        catch
        {
            _gate.Release();
            await ObserveAsync(syncTask);
            throw;
        }
        finally
        {
            _gate.Release();
        }

        var sync = await syncTask;
        sync.StatusCode.Should().Be(HttpStatusCode.Conflict);

        using var verifyScope = _factory.Services.CreateScope();
        var db = verifyScope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var projection = await db.WpfCustomerProjections
            .AsNoTracking().SingleAsync(p => p.Id == seed.ProjectionId);
        projection.FullName.Should().BeNull();
        projection.Phone.Should().BeNull();
        projection.Address.Should().BeNull();
        projection.PurgedAt.Should().NotBeNull();
    }

    private static async Task ObserveAsync(Task task)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(30));
        }
        catch
        {
            // Asıl yarış/timeout hatasını korurken arka plan isteğini gözlemle.
        }
    }

    private sealed record Seed(
        Guid ShopperId, Guid LicenseId, Guid ProjectionId, string Phone);

    private async Task<Seed> SeedAsync(Guid customerId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var now = DateTimeOffset.UtcNow;
        var phone = "+9055" + Random.Shared.Next(10_000_000, 99_999_999);
        var license = new License
        {
            Id = Guid.NewGuid(),
            CustomerId = customerId,
            LicenseKey = $"tombstone-{Guid.NewGuid():N}",
            SkuCode = "STD",
            ActivationSlots = 1,
            IssuedAt = now,
            ExpiresAt = now.AddDays(30),
        };
        var shopper = new OrderDeck.LicenseServer.Domain.Shopper
        {
            Id = Guid.NewGuid(),
            FullName = "Silinecek shopper",
            Phone = phone,
            PasswordHash = $"hash-{Guid.NewGuid():N}",
            Address = "Silinecek adres",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var projection = new WpfCustomerProjection
        {
            Id = Guid.NewGuid(),
            LicenseId = license.Id,
            Platform = "youtube",
            Username = "tombstone-race",
            FullName = shopper.FullName,
            Phone = phone,
            Address = shopper.Address,
            UpdatedAt = now,
        };
        db.AddRange(license, shopper, projection);
        db.ShopperBroadcasterLinks.Add(new ShopperBroadcasterLink
        {
            Id = Guid.NewGuid(),
            ShopperId = shopper.Id,
            LicenseId = license.Id,
            Platform = projection.Platform,
            Username = projection.Username,
            WpfCustomerId = projection.Id,
            JoinedAt = now,
        });
        await db.SaveChangesAsync();
        return new Seed(shopper.Id, license.Id, projection.Id, phone);
    }

    private sealed class FirstProjectionSaveGate
    {
        private readonly TaskCompletionSource<bool> _arrived =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _seen;

        public Task Arrived => _arrived.Task;
        public void Release() => _release.TrySetResult(true);

        public async Task HoldFirstAsync(CancellationToken ct)
        {
            if (Interlocked.Increment(ref _seen) != 1) return;
            _arrived.TrySetResult(true);
            await _release.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
        }
    }

    private sealed class FirstProjectionSaveInterceptor : SaveChangesInterceptor
    {
        private readonly FirstProjectionSaveGate _gate;
        public FirstProjectionSaveInterceptor(FirstProjectionSaveGate gate) => _gate = gate;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (eventData.Context is not null
                && eventData.Context.ChangeTracker.Entries<WpfCustomerProjection>()
                    .Any(e => e.State == EntityState.Modified))
            {
                await _gate.HoldFirstAsync(cancellationToken);
            }

            return await base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    private sealed class TombstoneRelationalFactory : RelationalApiFactory
    {
        private readonly FirstProjectionSaveGate _gate;

        public TombstoneRelationalFactory(
            string connectionString, FirstProjectionSaveGate gate)
            : base(connectionString) => _gate = gate;

        protected override void ConfigureDbContextOptions(DbContextOptionsBuilder opt)
            => opt.AddInterceptors(new FirstProjectionSaveInterceptor(_gate));
    }
}
