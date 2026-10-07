using System.Reflection;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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

    [Fact]
    public void Degisiklik_akisi_servisi_cozulur_arka_plan_isi_kayitli_eski_ingest_yok()
    {
        using var host = new global::OrderDeck.App.AppHost();
        var pull = host.Services.GetRequiredService<CustomerChangesPullService>();
        var tracker = host.Services.GetRequiredService<SyncStatusTracker>();

        host.Services.GetRequiredService<CustomerChangesPullService>().Should().BeSameAs(pull);
        host.Services.GetRequiredService<SyncStatusTracker>().Should().BeSameAs(tracker);
        PrivateField<SyncStatusTracker>(pull, "_tracker").Should().BeSameAs(tracker,
            "durum satırı (D2) ve form oynatması (C10) akışın yazdığı AYNI izleyiciyi okumalı");
        PrivateField<WpfCustomerProjectionSyncService>(pull, "_push").Should().BeSameAs(
            host.Services.GetRequiredService<WpfCustomerProjectionSyncService>(),
            "gönderimin tek tur kilidi yalnız tek örnekte geçerli");
        PrivateField<CustomerSyncRepository>(pull, "_sync").Should().BeSameAs(
            host.Services.GetRequiredService<CustomerSyncRepository>());

        // App.xaml.cs'e elle başlatma gerekmiyor: WpfStartupEnvironment kayıtlı bütün
        // IHostedService'leri başlatır (CLAUDE.md, PR #89).
        var hosted = host.Services.GetServices<IHostedService>().ToList();
        hosted.Should().ContainSingle(h => h is CustomerChangesPullHostedService);
        PrivateField<CustomerChangesPullService>(hosted.OfType<CustomerChangesPullHostedService>().Single(), "_service")
            .Should().BeSameAs(pull);
        hosted.Should().NotContain(h => h.GetType().Name == "ShopperRegistrationIngestHostedService");
    }

    [Fact]
    public void Odeme_servisi_ayni_mesgul_musteri_kumesini_ve_musteri_deposunu_alir()
    {
        using var host = new global::OrderDeck.App.AppHost();
        var payment = host.Services.GetRequiredService<global::OrderDeck.App.Services.PaymentRequestService>();

        PrivateField<CustomerBusySet>(payment, "_busy").Should().BeSameAs(
            host.Services.GetRequiredService<CustomerBusySet>(),
            "U13: TEK paylaşılan küme — ikinci örnek kirayı senkrona görünmez yapardı");
        PrivateField<CustomerBusySet>(host.Services.GetRequiredService<CustomerSyncRepository>(), "_busy")
            .Should().BeSameAs(PrivateField<CustomerBusySet>(payment, "_busy"),
                "kirayı alan (ödeme) ile ona bakan (senkron) aynı kümede");
        PrivateField<CustomerRepository>(payment, "_customers").Should().BeSameAs(
            host.Services.GetRequiredService<CustomerRepository>(), "U12: ödeme girişi Id'yi çözer");
    }

    [Fact]
    public void Musteri_detay_penceresi_kargo_deposunu_DIdan_alir()
    {
        using var host = new global::OrderDeck.App.AppHost();
        var vm = host.Services.GetRequiredService<global::OrderDeck.App.ViewModels.CustomerDetailViewModel>();

        PrivateField<ShipmentRepository>(vm, "_shipments").Should().BeSameAs(
            host.Services.GetRequiredService<ShipmentRepository>(),
            "C8: isteğe bağlı parametre DI'dan gelmezse iki açık kargo uyarısı sessizce kapanırdı");
    }
}
