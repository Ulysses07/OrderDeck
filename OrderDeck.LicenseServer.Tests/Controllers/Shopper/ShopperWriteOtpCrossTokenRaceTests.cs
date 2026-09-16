using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Auth;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Shopper;

/// <summary>
/// R10-S03'ün yan etkisi: Shopper.LastResetCodeIssuedAt jetonu HER Shopper
/// UPDATE'inin WHERE'ine girer. S02 catch'leri "çakışma = hesap silindi"
/// varsayıyordu; OTP üretimiyle çakışan bir istek yanlış yola düşerdi —
/// PATCH sahte 409 "silindi" alır, DELETE ise 204 döner ama hesabı
/// SİLMEMİŞ olurdu (silme talebi satırı da açılmaz). Kapanış:
/// ShopperSaveConflict.DeletedWonAsync — çakışmada DB'ye bakılır; gerçekten
/// silinmişse eski davranış (diriltme yasağı korunur), değilse jetonlar
/// tazelenip kayıt yeniden denenir ve isteğin amacı yerine gelir.
///
/// Kapı deseni ShopperMePatchPurgeRaceTests ile aynı: isteğin SaveChanges'ı
/// tutulur, arada OTP üretimi commit'lenir, sonra bırakılır.
/// </summary>
public sealed class ShopperWriteOtpCrossTokenRaceTests : IDisposable
{
    private readonly FirstShopperSaveGate _gate = new();
    private readonly GatedApiFactory _factory;

    public ShopperWriteOtpCrossTokenRaceTests() => _factory = new GatedApiFactory(_gate);

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
    public async Task Patch_otp_uretimiyle_yarisirsa_sahte_409_yerine_basarir()
    {
        var client = _factory.CreateClient();
        var (token, shopperId) = await RegisterShopperAsync(client);
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);

        var patchTask = client.PatchAsJsonAsync("/api/v1/shopper/me", new
        {
            fullName = "Yeni Ad",
            address = "Yeni adres",
        });

        await RunOtpIssueWhileHeldAsync(patchTask, shopperId);

        var patch = await patchTask;
        patch.StatusCode.Should().Be(HttpStatusCode.OK,
            "OTP üretimiyle çakışma silinme değildir; PATCH amacına ulaşmalı");

        using var verifyScope = _factory.Services.CreateScope();
        var db = verifyScope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var shopper = await db.Shoppers.AsNoTracking().SingleAsync(s => s.Id == shopperId);
        shopper.FullName.Should().Be("Yeni Ad");
        shopper.Address.Should().Be("Yeni adres");
        // Retry, OTP üretiminin jeton damgasını bayat null ile EZMEMELİ
        // (istek dışı alanlarda DB kazanır).
        shopper.LastResetCodeIssuedAt.Should().NotBeNull();
        (await db.ShopperPasswordResetCodes.CountAsync(c => c.ShopperId == shopperId))
            .Should().Be(1, "araya giren OTP üretimi kalıcı olmalı");
    }

    [Fact]
    public async Task Delete_otp_uretimiyle_yarisirsa_hesap_gercekten_silinir()
    {
        var client = _factory.CreateClient();
        var (token, shopperId) = await RegisterShopperAsync(client);
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);

        var deleteTask = client.DeleteAsync("/api/v1/shopper/me");

        await RunOtpIssueWhileHeldAsync(deleteTask, shopperId);

        var delete = await deleteTask;
        delete.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Eski davranışta buradaki 204 YALANDI: çakışma "zaten silinmiş"
        // sanılıyor, hesap açık kalıyor ve KVKK silme talebi hiç açılmıyordu.
        using var verifyScope = _factory.Services.CreateScope();
        var db = verifyScope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var shopper = await db.Shoppers.AsNoTracking().SingleAsync(s => s.Id == shopperId);
        shopper.DeletedAt.Should().NotBeNull("204 verildiyse hesap kapanmış olmalı");
        (await db.ShopperDeletionRequests.CountAsync(r => r.ShopperId == shopperId))
            .Should().Be(1, "KVKK silme talebi kuyruğa girmiş olmalı");
    }

    /// <summary>
    /// İstek SaveChanges kapısında beklerken aynı shopper için OTP üretimini
    /// commit'ler (OTP kaydı da Shopper'ı Modified yapar ama kapı yalnız İLK
    /// geleni tutar), sonra kapıyı bırakır.
    /// </summary>
    private async Task RunOtpIssueWhileHeldAsync(Task inFlightRequest, Guid shopperId)
    {
        try
        {
            await _gate.Arrived.WaitAsync(TimeSpan.FromSeconds(30));

            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var resetCodes = scope.ServiceProvider
                .GetRequiredService<PasswordResetCodeService>();
            var shopper = await db.Shoppers.SingleAsync(s => s.Id == shopperId);
            (await resetCodes.IssueWithHandleAsync(shopper, "7.7.7.7"))
                .Should().NotBeNull("arka plandaki OTP üretimi engelsiz commit'lenmeli");
        }
        catch
        {
            _gate.Release();
            await ObserveAsync(inFlightRequest);
            throw;
        }
        finally
        {
            _gate.Release();
        }
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
            Name = "OtpXTok-" + Guid.NewGuid().ToString("N")[..6],
            PasswordHash = "ph",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Customers.Add(customer);

        var code = ("otpxtok" + Guid.NewGuid().ToString("N"))[..16];
        db.Licenses.Add(new License
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            SkuCode = "STD",
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddYears(1),
            LicenseKey = "otpx-" + Guid.NewGuid().ToString("N")[..12],
            ShopperCode = code,
            ShopperCodeUpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        var phone = "+9055" + Random.Shared.Next(10_000_000, 99_999_999);
        var resp = await client.PostAsJsonAsync("/api/v1/shopper/auth/register",
            new RegisterRequest(code, "Yarış Kişisi", phone, "Pass1234!",
                "Yarış adresi", "youtube", "otpxtok"));
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
    /// Register Added ürettiği için yakalanmaz; ilk Modified istek ucununki,
    /// OTP üretimininki ikinci geldiği için beklemeden geçer. İsteğin retry
    /// kaydı da (üçüncü geliş) beklemez.
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
