using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Shopper;

/// <summary>
/// R11-S01-response (AC-26): <c>POST /shopper/auth/refresh</c>, rotasyonun
/// DOĞRULADIĞI parola neslini kullanmak yerine shopper satırını rotasyondan
/// SONRA yeniden okuyup access token'ı o anki nesille üretiyordu. Rotasyon
/// commit'i ile bu yeniden okuma arasına bir parola değişikliği girerse, eski
/// oturumun isteği YENİ nesle ait — yani parola değişikliğinin iptal etmesi
/// gereken oturuma bir access token veriyordu. Dönen refresh token bir sonraki
/// kullanımda zaten reddedilir, ama access token'ın ömrü boyunca (≤15 dk)
/// hesap açık kalıyordu.
///
/// Shopper şeması nesli HER istekte doğruladığı için (Program.cs
/// OnTokenValidated), doğru nesille damgalanmış bir token anında reddedilir —
/// testin ölçtüğü şey bu: rotasyondan dönen access token, yarışı kaybetmiş
/// olmalı.
///
/// Kapı deseni ShopperMePatchPurgeRaceTests'ten: interceptor rotasyonun
/// SaveChanges'ından hemen SONRA isteği tutar, arada parola nesli ilerletilir.
/// </summary>
public sealed class ShopperAuthRefreshAuthVersionRaceTests : IDisposable
{
    private readonly RotationCommitGate _gate = new();
    private readonly GatedApiFactory _factory;

    public ShopperAuthRefreshAuthVersionRaceTests() => _factory = new GatedApiFactory(_gate);

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

    private sealed record RefreshResponse(
        string AccessToken,
        DateTimeOffset AccessTokenExpiresAt,
        string RefreshToken,
        DateTimeOffset RefreshTokenExpiresAt);

    [Fact]
    public async Task Rotasyondan_sonra_nesil_ilerlerse_verilen_access_token_reddedilir()
    {
        var client = _factory.CreateClient();
        var (refreshToken, shopperId) = await RegisterShopperAsync(client);

        // Uçuştaki yenileme: rotasyon commit olur, SaveChanges kapıda tutulur.
        var refreshTask = client.PostAsJsonAsync("/api/v1/shopper/auth/refresh",
            new { refreshToken });

        try
        {
            await _gate.Arrived.WaitAsync(TimeSpan.FromSeconds(30));

            // Rotasyon commit'i ile controller'ın shopper'ı yeniden okuması
            // ARASINDA parola değişir: nesil ilerler.
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var shopper = await db.Shoppers.SingleAsync(s => s.Id == shopperId);
            shopper.AuthVersion += 1;
            shopper.PasswordHash = $"hash-{Guid.NewGuid():N}";
            await db.SaveChangesAsync();
        }
        catch
        {
            _gate.Release();
            await ObserveAsync(refreshTask);
            throw;
        }
        finally
        {
            _gate.Release();
        }

        var refresh = await refreshTask;
        refresh.StatusCode.Should().Be(HttpStatusCode.OK,
            "rotasyon parola değişmeden ÖNCE doğrulandı; istek başarısız değil");
        var body = await refresh.Content.ReadFromJsonAsync<RefreshResponse>();
        body!.AccessToken.Should().NotBeNullOrEmpty();

        var probe = _factory.CreateClient();
        probe.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", body.AccessToken);
        var me = await probe.GetAsync("/api/v1/shopper/me");

        me.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "access token rotasyonun DOĞRULADIĞI nesille damgalanmalı; o nesil " +
            "artık bayat olduğu için token ilk kullanımda reddedilir. Token " +
            "yeni nesille üretilirse parola değişikliği oturumu kapatmamış olur");
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

    private async Task<(string RefreshToken, Guid ShopperId)> RegisterShopperAsync(HttpClient client)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();

        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            Email = $"cust-{Guid.NewGuid():N}@x.test",
            Name = "NesilYarisi-" + Guid.NewGuid().ToString("N")[..6],
            PasswordHash = $"hash-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Customers.Add(customer);

        var code = ("nesil" + Guid.NewGuid().ToString("N"))[..16];
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
            new RegisterRequest(code, "Nesil Yarışı", phone, $"Pw-{Guid.NewGuid():N}",
                "İstanbul", "youtube", "nesilyarisi"));
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await resp.Content.ReadFromJsonAsync<AuthResponse>();
        return (body!.RefreshToken, body.ShopperId);
    }

    private sealed class RotationCommitGate
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
    /// Rotasyonun SaveChanges'ından SONRA tutar. Rotasyon, eski satırı
    /// Modified (RevokedAt) yaptığı için kayıttan ayırt edilebilir — kayıt
    /// yalnızca Added üretir. Tutma işlemi commit'ten sonra olmalı: açığın
    /// penceresi tam olarak "rotasyon yazıldı, controller shopper'ı henüz
    /// yeniden okumadı" aralığı.
    /// </summary>
    private sealed class AfterRotationCommitInterceptor : SaveChangesInterceptor
    {
        private readonly RotationCommitGate _gate;
        private readonly ConditionalWeakTable<DbContext, object> _rotations = new();

        public AfterRotationCommitInterceptor(RotationCommitGate gate) => _gate = gate;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (eventData.Context is not null
                && eventData.Context.ChangeTracker
                    .Entries<ShopperRefreshToken>()
                    .Any(e => e.State == EntityState.Modified))
            {
                _rotations.AddOrUpdate(eventData.Context, new object());
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }

        public override async ValueTask<int> SavedChangesAsync(
            SaveChangesCompletedEventData eventData,
            int result,
            CancellationToken cancellationToken = default)
        {
            if (eventData.Context is not null && _rotations.TryGetValue(eventData.Context, out _))
            {
                _rotations.Remove(eventData.Context);
                await _gate.HoldFirstAsync(cancellationToken);
            }

            return await base.SavedChangesAsync(eventData, result, cancellationToken);
        }
    }

    private sealed class GatedApiFactory : ApiFactory
    {
        private readonly RotationCommitGate _gate;
        public GatedApiFactory(RotationCommitGate gate) => _gate = gate;

        protected override void ConfigureDbContextOptions(DbContextOptionsBuilder opt)
            => opt.AddInterceptors(new AfterRotationCommitInterceptor(_gate));
    }
}
