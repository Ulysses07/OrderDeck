using Dapper;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.App.Services;
using OrderDeck.Core;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using Xunit;

namespace OrderDeck.Tests.App;

/// <summary>AppHost'u paylaşılan geçici kökte (TestAssemblyInit) tek başına kuran testler: başka bir sınıfın paralel
/// AppHost'u aynı veritabanında işareti tüketse ya da damgaları yazsa test yanlışlıkla yeşil kalırdı.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AppHostBootCollection
{
    public const string Name = "AppHost açılış adımları (paralel değil)";
}

/// <summary>
/// Açılışın satış senkronu adımları (spec 2026-10-13 §8.7, §6.1, §7.4, §8.2-7/-8): AppHost göçten sonra geri yükleme
/// işaretini uygular (<see cref="RestoreReplicaMarker.ApplyAtBoot"/>), müşteri toplamlarını
/// (<see cref="CustomerAggregatesBoot"/>) ve açık kargo dosyalarını (<see cref="ShipmentAmountsBoot"/>) türetir.
/// Yardımcıların kendisi ayrı testli; bu test AppHost'taki çağrıların gerçekten koştuğunu sabitler — silinen ya da
/// devre dışı kalan çağrı burada düşer. Sıraları <c>ShipmentAmountsBootTests</c>'te.
/// </summary>
[Collection(AppHostBootCollection.Name)]
public sealed class SalesSyncBootTests
{
    [Fact]
    public void AppHost_acilista_geri_yukleme_isaretini_uygular_toplamlari_ve_dosyalari_turetir()
    {
        // Göç ilk AppHost'ta; sonra geri yükleme sonrası yeniden başlatma gibi: damgalar boş, işaret duruyor.
        using (new global::OrderDeck.App.AppHost()) { }
        var factory = new SqliteConnectionFactory(AppPaths.DatabaseFile);
        using (var c = factory.Open())
            c.Execute("UPDATE SalesSyncState SET AggregatesDerivedAt = NULL, ShipmentsDerivedAt = NULL WHERE Id = 1");
        var identity = new ReplicaIdentityRepository(factory);
        var stalePending = identity.GetPendingPreviousReplicaId();
        if (stalePending is { } stale) identity.ClearPendingPreviousReplicaId(stale);
        var previous = Guid.NewGuid();
        var marker = RestoreReplicaMarker.PathFor(AppPaths.DatabaseFile);
        File.WriteAllText(marker, previous.ToString("N"));

        try
        {
            using var host = new global::OrderDeck.App.AppHost();

            File.Exists(marker).Should().BeFalse("işaret açılışta uygulandı ve silindi");
            host.Services.GetRequiredService<ReplicaIdentityRepository>().GetPendingPreviousReplicaId()
                .Should().Be(previous, "ilk sales/hello önceki kopya kimliğini taşır (§8.7)");
            var state = new SalesSyncStateRepository(factory).Get();
            state.AggregatesDerivedAt.Should().NotBeNull("müşteri toplamları açılışta türetildi (§6.1)");
            state.ShipmentsDerivedAt.Should().NotBeNull("açık kargo dosyaları açılışta türetildi (§7.4)");
        }
        finally
        {
            // Paylaşılan veritabanı: sonraki AppHost testleri geri yüklenmiş kopya görmesin.
            identity.ClearPendingPreviousReplicaId(previous);
            if (File.Exists(marker)) File.Delete(marker);
        }
    }
}
