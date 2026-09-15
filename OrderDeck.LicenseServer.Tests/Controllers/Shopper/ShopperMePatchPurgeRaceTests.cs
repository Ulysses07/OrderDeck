using System.Net;
using System.Net.Http.Headers;
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

namespace OrderDeck.LicenseServer.Tests.Controllers.Shopper;

/// <summary>
/// R10-S02: PATCH /shopper/me, shopper satırını purge'DEN ÖNCE okuyup
/// purge'den SONRA kaydederse kişisel veriyi (FullName/Address/Email) geri
/// doldurabiliyordu. Erişim kontrolü sağlamdı (purge sonrası yeni istek 401);
/// açık yalnızca, yetkisi purge'den önce doğrulanmış UÇUŞTAKİ isteğin geç
/// tamamlanmasıydı. Kapanış: Shopper.DeletedAt eşzamanlılık jetonu — geç
/// UPDATE "WHERE DeletedAt IS NULL" ile 0 satır bulur, controller 409 döner.
///
/// Kapı deseni WpfCustomerProjectionPurgeConcurrencyTests'ten: interceptor
/// PATCH'in SaveChanges'ını tutar, arada purge commit'lenir, sonra bırakılır.
/// </summary>
public sealed class ShopperMePatchPurgeRaceTests : IDisposable
{
    private readonly FirstShopperSaveGate _gate = new();
    private readonly GatedApiFactory _factory;

    public ShopperMePatchPurgeRaceTests() => _factory = new GatedApiFactory(_gate);

    public void Dispose() => _factory.Dispose();

    private sealed record RegisterRequest(
        string BroadcasterCode,
        string FullName,
        string Phone,
        string Password,
        string Address,
        string Platform,
        string Username);

    private sealed record AuthResponse(
        string AccessToken,
        DateTimeOffset AccessTokenExpiresAt,
        string RefreshToken,
        DateTimeOffset RefreshTokenExpiresAt,
        Guid ShopperId,
        object[] Broadcasters);

    [Fact]
    public async Task Purge_patch_yarisinda_gecikmis_patch_409_alir_ve_veri_temiz_kalir()
    {
        var client = _factory.CreateClient();
        var (token, shopperId) = await RegisterShopperAsync(client);
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);

        // Uçuştaki PATCH: shopper'ı okur, SaveChanges kapıda tutulur.
        var patchTask = client.PatchAsJsonAsync("/api/v1/shopper/me", new
        {
            fullName = "Bayat Kişisel Ad",
            address = "Bayat kişisel adres",
            email = "bayat@geri-dolan.test",
        });

        try
        {
            await _gate.Arrived.WaitAsync(TimeSpan.FromSeconds(30));

            // PATCH okuması ile yazması arasında gerçek purge tamamlanır.
            using var purgeScope = _factory.Services.CreateScope();
            var purge = purgeScope.ServiceProvider
                .GetRequiredService<ShopperPurgeService>();
            (await purge.PurgeAsync(shopperId, default)).Should().NotBeNull();
        }
        catch
        {
            _gate.Release();
            await ObserveAsync(patchTask);
            throw;
        }
        finally
        {
            _gate.Release();
        }

        var patch = await patchTask;
        patch.StatusCode.Should().Be(HttpStatusCode.Conflict,
            "purge'den sonra tamamlanan bayat PATCH kontrollü reddedilmeli");

        using var verifyScope = _factory.Services.CreateScope();
        var db = verifyScope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var shopper = await db.Shoppers.AsNoTracking().SingleAsync(s => s.Id == shopperId);
        shopper.FullName.Should().Be("[Silindi]");
        shopper.Address.Should().Be("");
        shopper.Email.Should().BeNull();
        shopper.DeletedAt.Should().NotBeNull();
    }

    private static async Task ObserveAsync(Task task)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(30));
        }
        catch
        {
            // Asıl hatayı korurken arka plan isteğini gözlemle.
        }
    }

    private async Task<(string Token, Guid ShopperId)> RegisterShopperAsync(HttpClient client)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();

        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            Email = $"cust-{Guid.NewGuid():N}@x.test",
            Name = "PurgeRace-" + Guid.NewGuid().ToString("N")[..6],
            PasswordHash = "ph",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Customers.Add(customer);

        var code = ("prace" + Guid.NewGuid().ToString("N"))[..16];
        db.Licenses.Add(new License
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            SkuCode = "STD",
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddYears(1),
            LicenseKey = "key-" + Guid.NewGuid().ToString("N")[..12],
            ShopperCode = code,
            ShopperCodeUpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        var phone = "+9055" + Random.Shared.Next(10_000_000, 99_999_999);
        var resp = await client.PostAsJsonAsync("/api/v1/shopper/auth/register",
            new RegisterRequest(code, "Silinecek Kişi", phone, "Pass1234!",
                "Silinecek adres", "youtube", "purgerace"));
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await resp.Content.ReadFromJsonAsync<AuthResponse>();
        return (body!.AccessToken, body.ShopperId);
    }

    private sealed class FirstShopperSaveGate
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

    /// <summary>
    /// Shopper satırını DEĞİŞTİREN (Modified) ilk SaveChanges'ı tutar.
    /// Register Added ürettiği için yakalanmaz; ilk Modified PATCH'inki,
    /// purge'ünki ikinci geldiği için kapıdan bekletilmeden geçer.
    /// </summary>
    private sealed class FirstShopperSaveInterceptor : SaveChangesInterceptor
    {
        private readonly FirstShopperSaveGate _gate;
        public FirstShopperSaveInterceptor(FirstShopperSaveGate gate) => _gate = gate;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (eventData.Context is not null
                && eventData.Context.ChangeTracker
                    .Entries<OrderDeck.LicenseServer.Domain.Shopper>()
                    .Any(e => e.State == EntityState.Modified))
            {
                await _gate.HoldFirstAsync(cancellationToken);
            }

            return await base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    private sealed class GatedApiFactory : ApiFactory
    {
        private readonly FirstShopperSaveGate _gate;
        public GatedApiFactory(FirstShopperSaveGate gate) => _gate = gate;

        protected override void ConfigureDbContextOptions(DbContextOptionsBuilder opt)
            => opt.AddInterceptors(new FirstShopperSaveInterceptor(_gate));
    }
}
