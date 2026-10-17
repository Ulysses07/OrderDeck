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
    public void SessionOrderSync_yanit_uygulayicisi_tekil_ve_DIdan_gelir()
    {
        // W1-08: ignored/rejected uygulaması (current) tek örnek; kabuğun gönderim sonucunu dinlemesi
        // Kabugun_istege_bagli_senkron_parametreleri_DIda_kayitli'de.
        using var host = new global::OrderDeck.App.AppHost();
        var apply = host.Services.GetRequiredService<OrderDeck.Core.Sales.SalesPushApply>();

        host.Services.GetRequiredService<OrderDeck.Core.Sales.SalesPushApply>().Should().BeSameAs(apply);
        PrivateField<OrderDeck.Core.Sales.SalesPushApply>(
                host.Services.GetRequiredService<SessionOrderSyncService>(), "_pushApply")
            .Should().BeSameAs(apply);
    }

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

    [Fact]
    public void Form_senkronu_durum_izleyicisini_DIdan_alir()
    {
        using var host = new global::OrderDeck.App.AppHost();
        var intake = host.Services.GetRequiredService<global::OrderDeck.App.Services.IntakeForm.IntakeFormSyncService>();

        PrivateField<SyncStatusTracker>(intake, "_tracker").Should().BeSameAs(
            host.Services.GetRequiredService<SyncStatusTracker>(),
            "U14: form işleme ilk tam akışı aynı izleyiciden bekler — isteğe bağlı parametre DI'dan " +
            "gelmezse taze bilgisayar eski formları akıştan önce oynatırdı");
    }

    [Fact]
    public void Bekleyen_kayit_sayaci_DIdan_cozulur()
    {
        using var host = new global::OrderDeck.App.AppHost();
        var counter = host.Services.GetRequiredService<SyncPendingCounter>();

        counter.Should().NotBeNull("D3'ün isteğe bağlı kurucu parametresi kayıt eksikse sessizce null olurdu");
        host.Services.GetRequiredService<SyncPendingCounter>().Should().BeSameAs(counter);
        PrivateField<SyncOutboxRepository>(counter, "_outbox").Should().BeSameAs(
            host.Services.GetRequiredService<SyncOutboxRepository>());
    }

    [Fact]
    public void Bekleyen_sayiya_giren_her_gonderim_durum_izleyicisine_yazar()
    {
        using var host = new global::OrderDeck.App.AppHost();
        var tracker = host.Services.GetRequiredService<SyncStatusTracker>();
        object[] pushes =
        [
            host.Services.GetRequiredService<WpfCustomerProjectionSyncService>(),
            host.Services.GetRequiredService<SessionOrderSyncService>(),
            host.Services.GetRequiredService<PaymentSyncService>(),
            host.Services.GetRequiredService<ShipmentSyncService>(),
        ];

        foreach (var push in pushes)
            PrivateField<SyncStatusTracker>(push, "_tracker").Should().BeSameAs(tracker,
                $"I-3: {push.GetType().Name} isteğe bağlı parametreyi DI'dan almazsa düşen gönderimi durum satırı göremezdi");
        tracker.Snapshot().PushOkAt!.Keys.Should().BeEquivalentTo(
            WpfCustomerProjectionSyncService.PushStatusName, SessionOrderSyncService.PushStatusName,
            PaymentSyncService.PushStatusName, ShipmentSyncService.PushStatusName);
    }

    [Fact]
    public void Gonder_ve_kapat_servisi_DIdan_cozulur_bekleyen_sayiya_giren_her_gonderimi_kosar()
    {
        using var host = new global::OrderDeck.App.AppHost();
        var flush = host.Services.GetRequiredService<SyncFlushService>();

        host.Services.GetRequiredService<SyncFlushService>().Should().BeSameAs(flush);
        var steps = PrivateField<IReadOnlyList<FlushStep>>(flush, "_steps")!;
        steps.Select(s => s.Name).Should().Equal(new[] { "müşteri", "oturum ve etiket", "ödeme", "kargo" },
            "D5: bekleyen sayıya (D1) giren her gönderim; müşteriler siparişlerden önce yönlenir");
        steps.Select(s => s.MaxShare).Should().Equal(SyncFlushService.CustomerShare, null, null, null);
    }

    [Fact]
    public void Satis_durumu_servisleri_tekil_ve_arka_plan_isi_genel_dongude_baslatilir()
    {
        // Yayın ve etiket senkronu W1-02 (spec 2026-10-13 §5.4 Servis maddesi, §10.3 DI testi).
        using var host = new global::OrderDeck.App.AppHost();
        var envelope = host.Services.GetRequiredService<SalesSyncEnvelope>();
        var service = host.Services.GetRequiredService<SalesSyncStateService>();
        var identity = host.Services.GetRequiredService<ReplicaIdentityRepository>();
        var state = host.Services.GetRequiredService<SalesSyncStateRepository>();

        host.Services.GetRequiredService<SalesSyncEnvelope>().Should().BeSameAs(envelope);
        host.Services.GetRequiredService<SalesSyncStateService>().Should().BeSameAs(service);
        host.Services.GetRequiredService<ReplicaIdentityRepository>().Should().BeSameAs(identity);
        host.Services.GetRequiredService<SalesSyncStateRepository>().Should().BeSameAs(state);
        PrivateField<SalesSyncEnvelope>(service, "_envelope").Should().BeSameAs(envelope,
            "hello'nun başarısını zarf tutar; gönderim servisleri IsReadyForPush'u AYNI örnekten okumalı");
        PrivateField<SalesSyncEnvelope>(host.Services.GetRequiredService<SessionOrderSyncService>(), "_envelope")
            .Should().BeSameAs(envelope, "W1-06: etiket gönderimi zarfı ve hello kararını aynı örnekten okur");
        PrivateField<SalesSyncEnvelope>(host.Services.GetRequiredService<ShipmentSyncService>(), "_envelope")
            .Should().BeSameAs(envelope, "W1-06: kargo gönderimi de");
        PrivateField<ReplicaIdentityRepository>(service, "_identity").Should().BeSameAs(identity);
        PrivateField<SalesSyncStateRepository>(service, "_state").Should().BeSameAs(state);
        PrivateField<ICurrentLicenseProvider>(envelope, "_license").Should().BeSameAs(
            host.Services.GetRequiredService<ICurrentLicenseProvider>());
        envelope.ReplicaId.Should().NotBeNull("açılış göçü (049) kopya kimliğini tohumlar");

        // App.xaml.cs'e elle başlatma gerekmiyor: WpfStartupEnvironment kayıtlı bütün IHostedService'leri
        // başlatır (CLAUDE.md, PR #89; WpfStartupEnvironment.StartBackgroundServicesAsync).
        var hosted = host.Services.GetServices<IHostedService>().ToList();
        hosted.Should().ContainSingle(h => h is SalesSyncStateHostedService);
        PrivateField<SalesSyncStateService>(hosted.OfType<SalesSyncStateHostedService>().Single(), "_service")
            .Should().BeSameAs(service);
    }

    [Fact]
    public void Kabugun_istege_bagli_senkron_parametreleri_DIda_kayitli()
    {
        // Kabuk ViewModel'i kurulmadan (22 servis, zamanlayıcılar) türleriyle sınanır: kayıt eksikse
        // DI isteğe bağlı parametreyi sessizce null bırakır — durum satırı (D3), kapanış uyarısı (D5)
        // ya da destek eylemi (D5b) hiç görünmezdi; W1-08: gönderim sonucunun uyarı şeridi de.
        using var host = new global::OrderDeck.App.AppHost();
        var syncParameters = typeof(global::OrderDeck.App.ViewModels.MainShellViewModel).GetConstructors().Single()
            .GetParameters()
            .Where(p => p.IsOptional && p.ParameterType.Namespace == typeof(SyncStatusTracker).Namespace)
            .ToList();

        syncParameters.Select(p => p.ParameterType).Should().BeEquivalentTo(
            new[]
            {
                typeof(SyncStatusTracker), typeof(SyncPendingCounter), typeof(CustomerChangesPullService),
                typeof(SessionOrderSyncService),
            });
        foreach (var p in syncParameters)
            host.Services.GetService(p.ParameterType).Should().NotBeNull(p.Name);
    }
}
