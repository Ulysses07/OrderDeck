using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Tests.Controllers.Licenses;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Data;

/// <summary>
/// <see cref="CustomerBalance.UpdatedAt"/> kişi kilidinin jetonudur (spec 2026-10-11 §4.11, M6): yalnız "değişti"
/// işaretlenen satırın jetonu da <c>max(UtcNow, özgün + 1 tick)</c> ilerler; bir dokunuş kaybolmaz.
/// </summary>
public sealed class CustomerBalanceVersionStampTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public CustomerBalanceVersionStampTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Yalniz_degisti_isaretlenen_satirin_jetonu_ozgunun_bir_tik_otesine_ilerler()
    {
        var kit = await SalesCommandTestKit.CreateAsync(_factory);
        var wpfCustomerId = Guid.NewGuid();
        await kit.SeedProjectionAsync(wpfCustomerId);
        // Saati ileri bir yazar: özgün değer şimdiden büyük — UtcNow ataması jetonu GERİ alırdı.
        var future = DateTimeOffset.UtcNow.AddDays(1);
        await kit.WithDbAsync(db =>
        {
            db.CustomerBalances.Add(new CustomerBalance
            {
                Id = Guid.NewGuid(), LicenseId = kit.LicenseId, WpfCustomerId = wpfCustomerId,
                Balance = 10m, UpdatedAt = future,
            });
            return Task.CompletedTask;
        });

        await kit.WithDbAsync(async db =>
        {
            var row = await db.CustomerBalances.SingleAsync(b => b.WpfCustomerId == wpfCustomerId);
            db.Entry(row).Property(b => b.UpdatedAt).IsModified = true;
        });

        var after = await kit.ReadDbAsync(db => db.CustomerBalances.AsNoTracking()
            .SingleAsync(b => b.WpfCustomerId == wpfCustomerId));
        after.UpdatedAt.Should().Be(future.AddTicks(1));
        after.Balance.Should().Be(10m);
    }
}
