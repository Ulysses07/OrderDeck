using System.Reflection;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.App.Services.Sync;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Storage.Repositories;
using Xunit;

namespace OrderDeck.Tests.App;

/// <summary>
/// Bölüm C ve D'nin DI kayıtları. Her görev kendi kaydının testini buraya ekler (C6, C7, C9, C10, D1, D5):
/// eksik kayıt kullanıcı makinesinde açılış çökmesi, ikinci bir CustomerBusySet örneği ise U13'ün
/// sessizce çalışmaması olurdu. Alanlar yansımayla okunur — ad değişirse test yüksek sesle düşer.
/// AppHost geçici kökte kurulur (C0, TestAssemblyInit): geliştiricinin veritabanı göç etmez.
/// </summary>
public sealed class CustomerSyncDiTests
{
    internal static T? PrivateField<T>(object owner, string name) where T : class
        => (T?)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner);

    [Fact]
    public void Senkron_deposu_tekil_ve_mesgul_musteri_kumesini_DIdan_alir()
    {
        using var host = new global::OrderDeck.App.AppHost();
        var busy = host.Services.GetRequiredService<CustomerBusySet>();
        var sync = host.Services.GetRequiredService<CustomerSyncRepository>();

        host.Services.GetRequiredService<CustomerSyncRepository>().Should().BeSameAs(sync);
        host.Services.GetRequiredService<CustomerBusySet>().Should().BeSameAs(busy);
        PrivateField<CustomerBusySet>(sync, "_busy").Should().BeSameAs(busy,
            "U13: senkron deposu ödeme akışıyla AYNI kümeyi görmeli");
        PrivateField<CustomerSyncRepository>(host.Services.GetRequiredService<WpfCustomerProjectionSyncService>(), "_sync")
            .Should().BeSameAs(sync);
    }
}
