using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Data;

/// <summary>NetgsmDeparture BİLİNÇLİ FK'sız (IysConsentEvent'teki kararın
/// aynısı): lisans KVKK ile silinse bile 3 yıllık imha randevusu yaşamalı.
/// Birisi "iyileştirme" diye navigasyon eklerse bu test kırılır.</summary>
public sealed class NetgsmDepartureModelTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public NetgsmDepartureModelTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public void NetgsmDeparture_hicbir_foreign_key_tasimaz()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var entityType = db.Model.FindEntityType(typeof(NetgsmDeparture))!;
        entityType.GetForeignKeys().Should().BeEmpty(
            "lisans silinse bile imha randevusu yaşamalı — FK cascade bunu bozar");
    }
}
