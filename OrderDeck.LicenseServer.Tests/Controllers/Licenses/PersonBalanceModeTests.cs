using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.CustomerSync;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;
using Kit = OrderDeck.LicenseServer.Tests.Controllers.Licenses.PersonBalanceTestKit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Licenses;

/// <summary>
/// Kip okuması — spec 2026-10-11 §4.0 "Kip", §3.1. Kip lisans başına, her istekte <c>License.PersonBalancePoolSince</c>'ten
/// okunur: boş = satır kipi. Bilinmeyen lisans satır kipidir; başka lisansın bayrağı bu lisansı etkilemez. Kitin
/// <see cref="Kit.EnablePoolAsync"/> / <see cref="Kit.DisablePoolAsync"/> yardımcılarını da sınar (sonraki görevler havuz
/// kipini bunlarla açar).
/// </summary>
public sealed class PersonBalanceModeTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public PersonBalanceModeTests(ApiFactory factory) => _factory = factory;

    private async Task<bool> IsPoolAsync(Guid licenseId)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<PersonBalance>()
            .IsPoolModeAsync(licenseId, CancellationToken.None);
    }

    private Task<DateTimeOffset?> PoolSinceAsync(Kit kit) =>
        kit.Sales.ReadDbAsync(db => db.Licenses.AsNoTracking()
            .Where(l => l.Id == kit.LicenseId)
            .Select(l => l.PersonBalancePoolSince)
            .SingleAsync());

    [Fact]
    public async Task Bayrak_bos_lisans_satir_kipinde()
    {
        var kit = await Kit.CreateAsync(_factory);

        (await IsPoolAsync(kit.LicenseId)).Should().BeFalse("S2 deploy'unda hiçbir lisans havuz kipinde değil (§7.1)");
        (await kit.Sales.ReadDbAsync(db => db.Licenses.AsNoTracking()
                .Where(l => l.Id == kit.LicenseId)
                .Select(l => l.SalesSyncMode)
                .SingleAsync()))
            .Should().Be(SalesSyncMode.Off,
                "kitin varsayılanı Off: havuz kapısının 409'u satış senkronu kapısının 409'uyla karışmaz (OldUserAgent özeti)");
    }

    [Fact]
    public async Task EnablePool_sonrasi_havuz_DisablePool_sonrasi_satir_kipi()
    {
        var kit = await Kit.CreateAsync(_factory);
        var since = DateTimeOffset.UtcNow.AddHours(-2);

        await kit.EnablePoolAsync(since);

        (await IsPoolAsync(kit.LicenseId)).Should().BeTrue();
        (await PoolSinceAsync(kit)).Should().Be(since);

        await kit.DisablePoolAsync();

        (await IsPoolAsync(kit.LicenseId)).Should().BeFalse("disable kill switch'tir: lisans satır kipine iner (§7.2)");
        (await PoolSinceAsync(kit)).Should().BeNull();
    }

    [Fact]
    public async Task Baska_lisansin_bayragi_bu_lisansi_etkilemez()
    {
        var kit = await Kit.CreateAsync(_factory);
        var other = await kit.Sales.SecondLicenseAsync(SalesSyncMode.Off);
        await other.WithDbAsync(async db =>
        {
            var license = await db.Licenses.SingleAsync(l => l.Id == other.LicenseId);
            license.PersonBalancePoolSince = DateTimeOffset.UtcNow.AddMinutes(-1);
        });

        (await IsPoolAsync(other.LicenseId)).Should().BeTrue();
        (await IsPoolAsync(kit.LicenseId)).Should().BeFalse("kip lisans başına (§4.0, M1)");
    }

    [Fact]
    public async Task Bilinmeyen_lisans_satir_kipinde()
    {
        (await IsPoolAsync(Guid.NewGuid())).Should().BeFalse();
    }
}
