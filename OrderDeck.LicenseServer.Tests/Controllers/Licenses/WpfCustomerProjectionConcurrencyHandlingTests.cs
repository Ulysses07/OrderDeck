using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Licenses;

public sealed class WpfCustomerProjectionConcurrencyHandlingTests : IDisposable
{
    private readonly ConflictFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    /// <summary>
    /// Çakışmada parti BİR KEZ taze okumayla yeniden uygulanır (A5); çakışma
    /// yeniden denemede de sürerse 409 döner — sonsuz döngü yok, üçüncü deneme
    /// yok. Tek seferlik çakışmanın yeniden denemeyle geçtiği gerçek SQL Server
    /// testi: <see cref="WpfCustomerSyncRetryTests"/>.
    /// </summary>
    [Fact]
    public async Task Sync_yazma_catismasi_yeniden_denemede_de_surerse_409_dondurur()
    {
        var (client, customerId, _) =
            await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);
        var now = DateTimeOffset.UtcNow;
        var licenseId = Guid.NewGuid();
        var projectionId = Guid.NewGuid();
        var phone = "+9055" + Random.Shared.Next(10_000_000, 99_999_999);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            db.Licenses.Add(new License
            {
                Id = licenseId,
                CustomerId = customerId,
                LicenseKey = $"sync-conflict-{Guid.NewGuid():N}",
                SkuCode = "STD",
                ActivationSlots = 1,
                IssuedAt = now,
                ExpiresAt = now.AddDays(30),
            });
            db.WpfCustomerProjections.Add(new WpfCustomerProjection
            {
                Id = projectionId,
                LicenseId = licenseId,
                Platform = "youtube",
                Username = "conflict-user",
                FullName = "Eski ad",
                Phone = phone,
                Address = "Eski adres",
                UpdatedAt = now.AddMinutes(-1),
            });
            await db.SaveChangesAsync();
        }

        var response = await client.PostAsJsonAsync(
            $"/api/v1/licenses/{licenseId}/wpf-customers/sync",
            new
            {
                customers = new[]
                {
                    new
                    {
                        id = projectionId,
                        platform = "youtube",
                        username = "conflict-user",
                        fullName = "Bayat ad",
                        phone,
                        address = "Bayat adres",
                        updatedAt = now,
                    }
                }
            });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync()).Should().Contain("sync-conflict");
        _factory.Conflicts.Thrown.Should().Be(2, "ilk deneme + tam bir yeniden deneme");
    }

    private sealed class ConflictOnProjectionUpdateInterceptor : SaveChangesInterceptor
    {
        private int _thrown;

        /// <summary>Fırlatılan çakışma sayısı — her kaydetme denemesi bir tane.</summary>
        public int Thrown => Volatile.Read(ref _thrown);

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (eventData.Context?.ChangeTracker.Entries<WpfCustomerProjection>()
                    .Any(e => e.State == EntityState.Modified) == true)
            {
                Interlocked.Increment(ref _thrown);
                throw new DbUpdateConcurrencyException("Tombstone yazma yarışı");
            }

            return base.SavingChangesAsync(
                eventData, result, cancellationToken);
        }
    }

    private sealed class ConflictFactory : ApiFactory
    {
        // Tek örnek: seçenek yapılandırması kapsam başına koşuyor; sayaç
        // isteğin kapsamından okunamazdı.
        public ConflictOnProjectionUpdateInterceptor Conflicts { get; } = new();

        protected override void ConfigureDbContextOptions(DbContextOptionsBuilder opt)
            => opt.AddInterceptors(Conflicts);
    }
}
