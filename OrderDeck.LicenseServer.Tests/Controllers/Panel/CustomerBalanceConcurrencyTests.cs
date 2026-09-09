using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Panel;

/// <summary>
/// CustomerBalance kayıp-güncelleme koruması (F02, 2026-09-09 denetimi).
///
/// Bu testler neden gerçek SQL Server istiyor: InMemory'de eşzamanlılık
/// semantiği yok — read-check-write açığı orada hiçbir zaman görünmez.
/// Token öncesi davranış gerçek SQL Server'da tekrar üretilmişti: paralel
/// iadeler birbirinin bakiye yazımını eziyor, cache ledger toplamından
/// kopuyordu (invariant: Balance = SUM(Transactions.Amount)).
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class CustomerBalanceConcurrencyTests : IAsyncLifetime
{
    private const int ParallelAttempts = 8;

    private readonly SqlServerContainerFixture _sql;
    private RelationalApiFactory _factory = null!;

    public CustomerBalanceConcurrencyTests(SqlServerContainerFixture sql) => _sql = sql;

    public async Task InitializeAsync()
        => _factory = new RelationalApiFactory(await _sql.CreateDatabaseAsync());

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Paralel_iadeler_bakiyeyi_ledger_toplamindan_koparamaz()
    {
        var (_, _, wpfCustomerId, licenseId, jwt) = await SeedAsync(initialBalance: 0m);
        var clients = BuildClients(jwt);

        // Isınma turu: var olmayan müşteriye istek — yönlendirme, model bağlama
        // ve EF sorgusu JIT edilsin ki gerçek turda istekler ayrışmasın.
        await Task.WhenAll(clients.Select(c => c.PostAsJsonAsync(
            $"/api/panel/customers/{Guid.NewGuid()}/balance/refund-full",
            new { Amount = 10m, Reason = "isinma" })));

        // Gerçek tur: 8 paralel iade, hepsi aynı bakiye satırına. 8 yazar ×
        // 3 deneme yeterli değil — bir kısmı retry tükenmesiyle düşer (TestServer
        // bunu istisna olarak fırlatır, prod'da 500 olur ve istemci yeniden
        // dener). Asıl güvence: başarılı sayısı ne olursa olsun hiçbir başarı
        // kaybolmaz ve cache == ledger toplamı == başarı × tutar.
        var responses = await Task.WhenAll(clients.Select(async c =>
        {
            try
            {
                var r = await c.PostAsJsonAsync(
                    $"/api/panel/customers/{wpfCustomerId}/balance/refund-full",
                    new { Amount = 100m, Reason = "yaris" });
                return r.StatusCode;
            }
            catch (Exception)
            {
                return HttpStatusCode.InternalServerError;
            }
        }));

        var succeeded = responses.Count(s => s == HttpStatusCode.OK);
        succeeded.Should().BeGreaterThan(0);

        var (balance, ledgerSum, txCount) = await ReadStateAsync(licenseId, wpfCustomerId);
        txCount.Should().Be(succeeded, "kaybeden isteklerin transaction'ı da geri alınmalı");
        balance.Should().Be(succeeded * 100m,
            "token öncesi davranışta paralel iadeler birbirini ezip bakiyeyi düşük bırakıyordu");
        balance.Should().Be(ledgerSum, "invariant: Balance = SUM(ledger)");
    }

    [Fact]
    public async Task Esazamanli_iki_negatif_ayarlamadan_yalnizca_biri_gecer()
    {
        // Bakiye 100 — iki paralel -100 manuel ayar. Token öncesi ikisi de
        // ön kontrolde 100 görüp geçiyordu → bakiye -100 (kural delindi).
        var (_, _, wpfCustomerId, licenseId, jwt) = await SeedAsync(initialBalance: 100m);
        var clients = BuildClients(jwt);

        await Task.WhenAll(clients.Select(c => c.PostAsJsonAsync(
            $"/api/panel/customers/{Guid.NewGuid()}/balance/manual-adjustment",
            new { Amount = -10m, Reason = "isinma" })));

        var responses = await Task.WhenAll(clients.Select(c => c.PostAsJsonAsync(
            $"/api/panel/customers/{wpfCustomerId}/balance/manual-adjustment",
            new { Amount = -100m, Reason = "yaris" })));

        responses.Count(r => r.StatusCode == HttpStatusCode.OK).Should().Be(1,
            "bakiye 100 iken -100'lük ayarlardan yalnızca biri geçebilir");
        responses.Count(r => r.StatusCode == HttpStatusCode.Conflict)
            .Should().Be(ParallelAttempts - 1, "kaybedenler insufficient-balance (409) almalı");

        var (balance, ledgerSum, _) = await ReadStateAsync(licenseId, wpfCustomerId);
        balance.Should().Be(0m, "bakiye asla sıfırın altına düşmemeli");
        // Seed bakiyesi ledger'sız yazıldığı için toplam yalnız -100 olmalı.
        ledgerSum.Should().Be(-100m);
    }

    private HttpClient[] BuildClients(string jwt) =>
        Enumerable.Range(0, ParallelAttempts)
            .Select(_ =>
            {
                var c = _factory.CreateClient();
                c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
                return c;
            })
            .ToArray();

    private async Task<(decimal Balance, decimal LedgerSum, int TxCount)> ReadStateAsync(
        Guid licenseId, Guid wpfCustomerId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var balance = await db.CustomerBalances
            .Where(b => b.LicenseId == licenseId && b.WpfCustomerId == wpfCustomerId)
            .Select(b => b.Balance)
            .SingleAsync();
        var ledger = await db.CustomerBalanceTransactions
            .Where(t => t.LicenseId == licenseId && t.WpfCustomerId == wpfCustomerId)
            .Select(t => t.Amount)
            .ToListAsync();
        return (balance, ledger.Sum(), ledger.Count);
    }

    private async Task<(HttpClient client, Guid customerId, Guid wpfCustomerId, Guid licenseId, string jwt)>
        SeedAsync(decimal initialBalance)
    {
        var (client, customerId, jwt) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();

        var licenseId = Guid.NewGuid();
        db.Licenses.Add(new License
        {
            Id = licenseId,
            LicenseKey = "balc-" + Guid.NewGuid().ToString("N"),
            CustomerId = customerId,
            SkuCode = "STD",
            ActivationSlots = 1,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
        });
        var wpfCustomerId = Guid.NewGuid();
        db.WpfCustomerProjections.Add(new WpfCustomerProjection
        {
            Id = wpfCustomerId,
            LicenseId = licenseId,
            Platform = "youtube",
            Username = "u" + wpfCustomerId.ToString("N")[..6],
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        // Bakiye satırını baştan yaz: testler UPDATE yolundaki token'ı hedefliyor
        // (insert yarışı unique index'e bırakıldı — bilinçli kapsam dışı).
        db.CustomerBalances.Add(new CustomerBalance
        {
            Id = Guid.NewGuid(),
            LicenseId = licenseId,
            WpfCustomerId = wpfCustomerId,
            Balance = initialBalance,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        return (client, customerId, wpfCustomerId, licenseId, jwt);
    }
}
