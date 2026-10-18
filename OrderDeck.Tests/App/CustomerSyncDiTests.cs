using System.Reflection;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OrderDeck.App.Services.Sync;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Sales;
using OrderDeck.Core.Settings;
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
    public void Kargo_ayari_saglayicisi_tekil_okuyucular_ayni_ornegi_alir_mod_Offta_canli_ayar()
    {
        // W3 (spec 2026-10-13 §6.2): kargo eşiğinin TEK okuma noktası. Bir okuyucu başka örnek ya da yerel ayar alsaydı
        // mod On'da yerel değerle tutar isterdi.
        using var host = new global::OrderDeck.App.AppHost();
        var shipping = host.Services.GetRequiredService<IShippingSettingsProvider>();

        host.Services.GetRequiredService<IShippingSettingsProvider>().Should().BeSameAs(shipping);
        PrivateField<IShippingSettingsProvider>(
                host.Services.GetRequiredService<global::OrderDeck.App.Services.PaymentRequestService>(), "_shipping")
            .Should().BeSameAs(shipping);
        PrivateField<IShippingSettingsProvider>(
                host.Services.GetRequiredService<global::OrderDeck.App.Services.PriceCorrection.CorrectedPaymentRequests>(),
                "_shipping")
            .Should().BeSameAs(shipping);
        PrivateField<IShippingSettingsProvider>(host.Services.GetRequiredService<ShipmentService>(), "_shipping")
            .Should().BeSameAs(shipping);
        PrivateField<IShippingSettingsProvider>(
                host.Services.GetRequiredService<OrderDeck.Core.Payments.PaymentMatcherService>(), "_shipping")
            .Should().BeSameAs(shipping);
        PrivateField<IShippingSettingsProvider>(
                host.Services.GetRequiredService<global::OrderDeck.App.ViewModels.DekontEkleViewModel>(), "_shipping")
            .Should().BeSameAs(shipping);

        // Geçici kökün yeni veritabanı mod Off: değer CANLI ayar örneğinin kendisi.
        var snapshot = shipping.Current();
        snapshot.Settings.Should().BeSameAs(host.Services.GetRequiredService<AppSettings>().Shipping);
        snapshot.Version.Should().BeNull();
    }

    [Fact]
    public void Kargo_ayari_yazicisi_tekil_satis_durumu_deposuna_ve_zarfa_bagli()
    {
        // W3-03 (§4.8, §6.2): ayar ekranının mod On'daki yazıcısı önbelleği sağlayıcının okuduğu depoya yazmalı; başka
        // bir depo örneği olsaydı (aynı veritabanı olsa da) DI kaydının bozulduğunu gösterirdi. Ayar ekranı yazıcıyı ve
        // sağlayıcıyı isteğe bağlı parametreyle alır: kayıt eksik olsaydı ekran sessizce mod Off gibi yerel yazardı.
        using var host = new global::OrderDeck.App.AppHost();
        var writer = host.Services.GetRequiredService<LicenseShippingSettingsWriter>();

        host.Services.GetRequiredService<LicenseShippingSettingsWriter>().Should().BeSameAs(writer);
        PrivateField<SalesSyncStateRepository>(writer, "_state")
            .Should().BeSameAs(host.Services.GetRequiredService<SalesSyncStateRepository>());
        PrivateField<SalesSyncEnvelope>(writer, "_envelope")
            .Should().BeSameAs(host.Services.GetRequiredService<SalesSyncEnvelope>());
        PrivateField<Action>(writer, "_requestModeOffRound").Should().NotBeNull();

        var parameters = typeof(global::OrderDeck.App.ViewModels.SettingsViewModel).GetConstructors()
            .Single().GetParameters().Select(p => p.ParameterType).ToList();
        parameters.Should().Contain(typeof(IShippingSettingsProvider)).And.Contain(typeof(LicenseShippingSettingsWriter));
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
        PrivateField<SalesSyncStateRepository>(host.Services.GetRequiredService<ShipmentSyncService>(), "_salesState")
            .Should().BeSameAs(state, "W2-08: kargo servisi yerel kipi okur — parametre isteğe bağlı; gelmezse mod On'da "
                + "da gönderirdi (§7.1)");
        PrivateField<ReplicaIdentityRepository>(service, "_identity").Should().BeSameAs(identity);
        PrivateField<SalesSyncStateRepository>(service, "_state").Should().BeSameAs(state);
        PrivateField<ICurrentLicenseProvider>(envelope, "_license").Should().BeSameAs(
            host.Services.GetRequiredService<ICurrentLicenseProvider>());
        envelope.ReplicaId.Should().NotBeNull("açılış göçü (049) kopya kimliğini tohumlar");

        // W2-03: kalp atışını (hello + state) satış akışı turu sürer — ayrı bir state zamanlayıcısı yok; 30 sn'de
        // tam bir state isteği gider (Satis_akisi_servisi_... testi).
        var hosted = host.Services.GetServices<IHostedService>().ToList();
        hosted.Should().NotContain(h => h.GetType().Name == "SalesSyncStateHostedService");
        PrivateField<SalesSyncStateService>(host.Services.GetRequiredService<SalesChangesPullService>(), "_state")
            .Should().BeSameAs(service);
    }

    [Fact]
    public void Satis_akisi_servisi_ortak_orneklerle_kurulur_tek_state_surucusu_ve_genel_dongude_baslatilir()
    {
        // Yayın ve etiket senkronu W2-03 (spec 2026-10-13 §5.4 Servis maddesi, §10.3 DI testi).
        using var host = new global::OrderDeck.App.AppHost();
        var pull = host.Services.GetRequiredService<SalesChangesPullService>();
        var busy = host.Services.GetRequiredService<CustomerBusySet>();

        host.Services.GetRequiredService<SalesChangesPullService>().Should().BeSameAs(pull);
        PrivateField<SalesSyncStateService>(pull, "_state").Should().BeSameAs(
            host.Services.GetRequiredService<SalesSyncStateService>(), "kalp atışı turun ilk adımı: aynı tur kilidi");
        PrivateField<SalesSyncEnvelope>(pull, "_envelope").Should().BeSameAs(
            host.Services.GetRequiredService<SalesSyncEnvelope>(), "hello'nun başarısı zarfta");
        PrivateField<CustomerChangesPullService>(pull, "_customerPull").Should().BeSameAs(
            host.Services.GetRequiredService<CustomerChangesPullService>(),
            "sayfalar müşteri akışının tur kilidi altında: kilit yalnız tek örnekte geçerli");
        PrivateField<WpfCustomerProjectionSyncService>(pull, "_customerPush").Should().BeSameAs(
            host.Services.GetRequiredService<WpfCustomerProjectionSyncService>(), "ön gönderim tek müşteri gönderim kapısından");
        PrivateField<SessionOrderSyncService>(pull, "_salesPush").Should().BeSameAs(
            host.Services.GetRequiredService<SessionOrderSyncService>(), "ön gönderim TEK gönderim kapısından geçer");
        var feed = PrivateField<SalesFeedRepository>(pull, "_feed")!;
        feed.Should().BeSameAs(host.Services.GetRequiredService<SalesFeedRepository>());
        PrivateField<CustomerBusySet>(feed, "_busy").Should().BeSameAs(busy,
            "U13: akış uygulaması ödeme akışıyla AYNI kümeyi görmeli");
        PrivateField<SalesFeedFailureRepository>(pull, "_failures").Should().BeSameAs(
            host.Services.GetRequiredService<SalesFeedFailureRepository>());
        PrivateField<RemoteLiveSessionRepository>(pull, "_liveSessions").Should().BeSameAs(
            host.Services.GetRequiredService<RemoteLiveSessionRepository>());
        PrivateField<SyncCursorRepository>(pull, "_cursors").Should().BeSameAs(
            host.Services.GetRequiredService<SyncCursorRepository>());

        // App.xaml.cs'e elle başlatma gerekmiyor: WpfStartupEnvironment kayıtlı bütün IHostedService'leri
        // başlatır (CLAUDE.md, PR #89; WpfStartupEnvironment.StartBackgroundServicesAsync).
        var hosted = host.Services.GetServices<IHostedService>().ToList();
        hosted.Should().ContainSingle(h => h is SalesChangesPullHostedService);
        PrivateField<SalesChangesPullService>(hosted.OfType<SalesChangesPullHostedService>().Single(), "_service")
            .Should().BeSameAs(pull);

        // Tek state sürücüsü: hiçbir arka plan işi state servisini doğrudan tutmaz — 30 sn'de iki ayrı kalp atışı
        // olmasın (satış akışı turu tam bir state çağırır; SalesChangesPullServiceTests sayar).
        hosted.Where(h => h.GetType()
                .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
                .Any(f => f.FieldType == typeof(SalesSyncStateService)))
            .Should().BeEmpty();
    }

    [Fact]
    public void Geri_doldurma_servisi_ortak_orneklerle_kurulur_arka_plan_isi_genel_dongude_baslatilir()
    {
        // Yayın ve etiket senkronu W1-09 (spec 2026-10-13 §8.2, §10.3 DI testi).
        using var host = new global::OrderDeck.App.AppHost();
        var backfill = host.Services.GetRequiredService<SalesBackfillService>();

        host.Services.GetRequiredService<SalesBackfillService>().Should().BeSameAs(backfill);
        PrivateField<SalesSyncEnvelope>(backfill, "_envelope").Should().BeSameAs(
            host.Services.GetRequiredService<SalesSyncEnvelope>(), "hello'nun başarısı zarfta: aynı örnek");
        PrivateField<SalesSyncStateService>(backfill, "_state").Should().BeSameAs(
            host.Services.GetRequiredService<SalesSyncStateService>(), "tamamlanma hello'su turla aynı kapıdan");
        PrivateField<SessionOrderSyncService>(backfill, "_push").Should().BeSameAs(
            host.Services.GetRequiredService<SessionOrderSyncService>(), "boşaltma TEK gönderim kapısından geçer");
        PrivateField<ShipmentSyncService>(backfill, "_shipments").Should().BeSameAs(
            host.Services.GetRequiredService<ShipmentSyncService>());
        PrivateField<OrderDeck.Core.Sessions.HandoverGate>(backfill, "_handover").Should().BeSameAs(
            host.Services.GetRequiredService<OrderDeck.Core.Sessions.HandoverGate>(),
            "Release kararı kapanış gönderimiyle aynı devir kilitlerini görmeli");
        PrivateField<SyncCursorRepository>(backfill, "_cursors").Should().BeSameAs(
            host.Services.GetRequiredService<SyncCursorRepository>());
        PrivateField<SalesBackfillRepository>(backfill, "_repo").Should().BeSameAs(
            host.Services.GetRequiredService<SalesBackfillRepository>());

        // App.xaml.cs'e elle başlatma gerekmiyor: WpfStartupEnvironment kayıtlı bütün IHostedService'leri
        // başlatır (CLAUDE.md, PR #89; WpfStartupEnvironment.StartBackgroundServicesAsync).
        var hosted = host.Services.GetServices<IHostedService>().ToList();
        hosted.Should().ContainSingle(h => h is SalesBackfillHostedService);
        PrivateField<SalesBackfillService>(hosted.OfType<SalesBackfillHostedService>().Single(), "_service")
            .Should().BeSameAs(backfill);
    }

    [Fact]
    public void Mod_Off_odeme_kaydi_yazicisi_odeme_servisine_bagli_tekil_gondericinin_kuyruguna_yazar()
    {
        // W3-06 (spec 2026-10-13 §6.4 "Ödeme geçmişi (mod Off'ta da)"): PaymentRequestService'in isteğe bağlı
        // parametresi — kayıt eksikse DI onu sessizce null bırakır ve mod Off'taki istekler sunucuya hiç geçmezdi.
        using var host = new global::OrderDeck.App.AppHost();
        var writer = host.Services.GetRequiredService<PaymentOffRecordWriter>();

        PrivateField<PaymentOffRecordWriter>(
                host.Services.GetRequiredService<global::OrderDeck.App.Services.PaymentRequestService>(), "_offRecords")
            .Should().BeSameAs(writer);
        PrivateField<PaymentRecordSender>(writer, "_sender").Should().BeSameAs(
            host.Services.GetRequiredService<PaymentRecordSender>(), "kayıt TEK göndericinin kuyruğuna");
        PrivateField<SalesSyncStateRepository>(writer, "_state").Should().BeSameAs(
            host.Services.GetRequiredService<SalesSyncStateRepository>());
        PrivateField<ICurrentLicenseProvider>(writer, "_license").Should().BeSameAs(
            host.Services.GetRequiredService<ICurrentLicenseProvider>());
    }

    [Fact]
    public void Odeme_kirasi_sarmali_odeme_servisine_bagli_on_gonderimi_tekil_orneklerden()
    {
        // W3-07 (spec 2026-10-13 §6.4): PaymentRequestService'in isteğe bağlı parametresi — kayıt eksikse DI onu sessizce
        // null bırakır ve mod On'da istek kirasız, ön gönderimsiz giderdi (M9'un fail-closed'ı hiç çalışmazdı).
        using var host = new global::OrderDeck.App.AppHost();
        var lease = host.Services.GetRequiredService<PaymentLeaseCoordinator>();

        host.Services.GetRequiredService<PaymentLeaseCoordinator>().Should().BeSameAs(lease);
        PrivateField<PaymentLeaseCoordinator>(
                host.Services.GetRequiredService<global::OrderDeck.App.Services.PaymentRequestService>(), "_lease")
            .Should().BeSameAs(lease);
        PrivateField<SalesChangesPullService>(lease, "_pull").Should().BeSameAs(
            host.Services.GetRequiredService<SalesChangesPullService>(), "bekleyen komutlar tek göndericiden");
        PrivateField<PaymentRecordSender>(lease, "_records").Should().BeSameAs(
            host.Services.GetRequiredService<PaymentRecordSender>(), "finish'ler TEK göndericinin kuyruğundan");
        PrivateField<WpfCustomerProjectionSyncService>(lease, "_customerPush").Should().BeSameAs(
            host.Services.GetRequiredService<WpfCustomerProjectionSyncService>(), "müşteri gönderiminin tek tur kilidi");
        PrivateField<SessionOrderSyncService>(lease, "_salesPush").Should().BeSameAs(
            host.Services.GetRequiredService<SessionOrderSyncService>(), "gönderim kapısı tek örnekte");
        PrivateField<PendingSalesCommandRepository>(lease, "_pendingCommands").Should().BeSameAs(
            host.Services.GetRequiredService<PendingSalesCommandRepository>());
        PrivateField<SalesSyncStateRepository>(lease, "_state").Should().BeSameAs(
            host.Services.GetRequiredService<SalesSyncStateRepository>());
    }

    [Fact]
    public void Odeme_gecmisi_geri_doldurmasi_ortak_orneklerle_kurulur_geri_doldurma_turu_surer()
    {
        // W3-06 (spec 2026-10-13 §6.4 "Ödeme geçmişi", §8.2-6, §10.3 DI testi): kayıtlar TEK göndericinin kuyruğuna,
        // tamamlanma hello'su turla aynı kapıdan (aynı durum servisi), bekleme kararı aynı zarftan. Sürücü geri doldurma
        // turu: ayrı hosted service yok, mevcut olanın genel döngüde başlatılması yeter (CLAUDE.md, PR #89).
        using var host = new global::OrderDeck.App.AppHost();
        var history = host.Services.GetRequiredService<PaymentHistoryBackfill>();

        host.Services.GetRequiredService<PaymentHistoryBackfill>().Should().BeSameAs(history);
        PrivateField<PaymentRecordSender>(history, "_sender").Should().BeSameAs(
            host.Services.GetRequiredService<PaymentRecordSender>());
        PrivateField<PendingPaymentRecordRepository>(history, "_pending").Should().BeSameAs(
            host.Services.GetRequiredService<PendingPaymentRecordRepository>());
        PrivateField<SalesSyncStateService>(history, "_state").Should().BeSameAs(
            host.Services.GetRequiredService<SalesSyncStateService>(), "tamamlanma hello'su turla aynı kapıdan");
        PrivateField<SalesSyncEnvelope>(history, "_envelope").Should().BeSameAs(
            host.Services.GetRequiredService<SalesSyncEnvelope>());
        PrivateField<SyncCursorRepository>(history, "_cursors").Should().BeSameAs(
            host.Services.GetRequiredService<SyncCursorRepository>());
        PrivateField<ICurrentLicenseProvider>(history, "_licenseProvider").Should().BeSameAs(
            host.Services.GetRequiredService<ICurrentLicenseProvider>());
        PrivateField<PaymentJobRepository>(history, "_jobs").Should().BeSameAs(
            host.Services.GetRequiredService<IPaymentJobStore>(), "ödeme akışıyla aynı depo örneği");

        var hosted = host.Services.GetServices<IHostedService>().ToList();
        PrivateField<PaymentHistoryBackfill>(hosted.OfType<SalesBackfillHostedService>().Single(), "_history")
            .Should().BeSameAs(history);
        hosted.Should().NotContain(h => h.GetType().Name.Contains("PaymentHistory"), "ayrı zamanlayıcı yok");
    }

    [Fact]
    public void Odeme_kaydi_gondericisi_tekil_geri_doldurma_turu_ve_acilisin_birinci_adimiyla_paylasilir()
    {
        // W3-05 (spec 2026-10-13 §6.4, §8.4-1): bekleyen ödeme kayıtlarını geri doldurma turu (60 sn, iki kipte) ve mod On
        // açılışının 1. adımı (İLK kuyruk) gönderir. Kapı tek örnekte geçerli: iki örnek aynı kaydı aynı anda iki kez
        // gönderirdi. Zarf tekil örnek: geri yüklemeden sonra kip tazelenene kadar istek gitmez (§8.7).
        using var host = new global::OrderDeck.App.AppHost();
        var sender = host.Services.GetRequiredService<PaymentRecordSender>();

        host.Services.GetRequiredService<PaymentRecordSender>().Should().BeSameAs(sender);
        PrivateField<PaymentRecordSender>(host.Services.GetRequiredService<SalesBackfillService>(), "_paymentRecords")
            .Should().BeSameAs(sender);
        var flush = PrivateField<ISalesOpeningFlush>(host.Services.GetRequiredService<SalesModeCoordinator>(), "_flush")
            .Should().BeOfType<SalesOpeningFlush>().Subject;
        PrivateField<PaymentRecordSender>(flush, "_paymentRecords").Should().BeSameAs(sender);
        PrivateField<PendingPaymentRecordRepository>(sender, "_repo").Should().BeSameAs(
            host.Services.GetRequiredService<PendingPaymentRecordRepository>());
        PrivateField<SalesSyncEnvelope>(sender, "_envelope").Should().BeSameAs(
            host.Services.GetRequiredService<SalesSyncEnvelope>(), "hello, state ve kip adımının kararı zarfta: aynı örnek");
        PrivateField<ICurrentLicenseProvider>(sender, "_license").Should().BeSameAs(
            host.Services.GetRequiredService<ICurrentLicenseProvider>());
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
                typeof(SessionOrderSyncService), typeof(SalesChangesPullService),
            });
        foreach (var p in syncParameters)
            host.Services.GetService(p.ParameterType).Should().NotBeNull(p.Name);
    }

    [Fact]
    public void SalesCommandClient_tekil_akisla_ayni_depo_zarf_ve_guncelleme_sorusunu_alir()
    {
        // W2-04 (spec 2026-10-13 §4.6 adım 7, §5.3): komut yanıtı akışla AYNI depodan (aynı CustomerBusySet, U13)
        // yazılır; zarf hello kararını tutan tekil örnek; güncelleme sorusu açılıştakiyle aynı.
        using var host = new global::OrderDeck.App.AppHost();
        var client = host.Services.GetRequiredService<SalesCommandClient>();

        host.Services.GetRequiredService<SalesCommandClient>().Should().BeSameAs(client);
        PrivateField<SalesFeedRepository>(client, "_feed").Should().BeSameAs(
            host.Services.GetRequiredService<SalesFeedRepository>());
        PrivateField<SalesSyncEnvelope>(client, "_envelope").Should().BeSameAs(
            host.Services.GetRequiredService<SalesSyncEnvelope>());
        PrivateField<global::OrderDeck.App.Services.IAppUpdatePrompt>(client, "_updatePrompt").Should().BeSameAs(
            host.Services.GetRequiredService<global::OrderDeck.App.Services.IAppUpdatePrompt>());
        PrivateField<SalesConflictLogRepository>(client, "_conflicts").Should().NotBeNull();
    }

    [Fact]
    public void Bekleyen_satis_komutu_gondericisi_tekil_tur_ve_komut_istemcisiyle_paylasilir()
    {
        // W2-07 (spec 2026-10-13 §5.5): satış akışı turu bekleyen komutları akıştan önce gönderir; isteğe bağlı kurucu
        // parametresi DI'dan gelmezse tur onları hiç göndermez, örtü hiç kalkmazdı. Kapı tek örnekte geçerli.
        using var host = new global::OrderDeck.App.AppHost();
        var sender = host.Services.GetRequiredService<PendingSalesCommandSender>();

        host.Services.GetRequiredService<PendingSalesCommandSender>().Should().BeSameAs(sender);
        PrivateField<PendingSalesCommandSender>(host.Services.GetRequiredService<SalesChangesPullService>(),
            "_pendingCommands").Should().BeSameAs(sender);
        PrivateField<SalesCommandClient>(sender, "_commands").Should().BeSameAs(
            host.Services.GetRequiredService<SalesCommandClient>());
        PrivateField<PendingSalesCommandRepository>(sender, "_repo").Should().BeSameAs(
            host.Services.GetRequiredService<PendingSalesCommandRepository>());
        PrivateField<global::OrderDeck.App.Services.Sales.SalesConnectivity>(sender, "_connectivity").Should().BeSameAs(
            host.Services.GetRequiredService<global::OrderDeck.App.Services.Sales.SalesConnectivity>());
    }

    [Fact]
    public void Uzak_yayin_cipleri_tekil_ve_kabugun_istege_bagli_parametresi_DIda_kayitli()
    {
        // W2-09 (spec 2026-10-13 §5.7): kabuk çipleri isteğe bağlı parametreyle alır — kayıt eksikse DI sessizce null
        // bırakır, çipler ve "Yayını bitir…" hiç görünmezdi. Komut istemcisi, ayna ve bağlantı ölçüsü tekil örnekler.
        using var host = new global::OrderDeck.App.AppHost();
        var vm = host.Services.GetRequiredService<global::OrderDeck.App.ViewModels.RemoteSessionsViewModel>();

        host.Services.GetRequiredService<global::OrderDeck.App.ViewModels.RemoteSessionsViewModel>().Should().BeSameAs(vm);
        PrivateField<SalesCommandClient>(vm, "_commands").Should().BeSameAs(
            host.Services.GetRequiredService<SalesCommandClient>());
        PrivateField<RemoteLiveSessionRepository>(vm, "_liveSessions").Should().BeSameAs(
            host.Services.GetRequiredService<RemoteLiveSessionRepository>());
        PrivateField<global::OrderDeck.App.Services.Sales.SalesConnectivity>(vm, "_connectivity").Should().BeSameAs(
            host.Services.GetRequiredService<global::OrderDeck.App.Services.Sales.SalesConnectivity>());
        typeof(global::OrderDeck.App.ViewModels.MainShellViewModel).GetConstructors().Single().GetParameters()
            .Should().ContainSingle(p => p.IsOptional
                && p.ParameterType == typeof(global::OrderDeck.App.ViewModels.RemoteSessionsViewModel));
    }

    [Fact]
    public void Off_turunun_yerel_adimlari_canli_ayar_ornegine_ve_ayni_etiket_servisine_baglidir()
    {
        // W2-11 (spec 2026-10-13 §8.5, §6.2): kargo ayarının kopyası CANLI AppSettings örneğine yazılmalı — başka bir
        // örneğe yazılsaydı dekont eşiği ve ödeme isteği yeniden açılışa dek eski yerel değeri okurdu. Bekleyen komutlar
        // tekil LabelService ile yerel yazıma çevrilir.
        using var host = new global::OrderDeck.App.AppHost();
        var round = host.Services.GetRequiredService<SalesModeOffRound>();

        host.Services.GetRequiredService<SalesModeOffRound>().Should().BeSameAs(round);
        PrivateField<AppSettings>(round, "_liveSettings").Should().BeSameAs(host.Services.GetRequiredService<AppSettings>());
        PrivateField<SettingsStore>(round, "_settingsStore").Should().BeSameAs(host.Services.GetRequiredService<SettingsStore>());
        PrivateField<LabelService>(round, "_labelService").Should().BeSameAs(host.Services.GetRequiredService<LabelService>());
        PrivateField<SalesFeedRepository>(round, "_feed").Should().BeSameAs(
            host.Services.GetRequiredService<SalesFeedRepository>(), "akışla aynı CustomerBusySet (U13)");
        // Kip adımı turu isteğe bağlı parametreyle alır — kayıt eksikse DI sessizce null bırakır, Off turu hiç başlamazdı.
        PrivateField<SalesModeOffRound>(host.Services.GetRequiredService<SalesModeCoordinator>(), "_modeOff")
            .Should().BeSameAs(round);
        // Yerel kip 0'daki turun kargo ön gönderimi ve duran dosyanın gönderimi de isteğe bağlı parametrelerle: eksikse
        // gönderilmemiş dosya kararı son akışta dururdu, tur hiç bitmezdi.
        var pull = host.Services.GetRequiredService<SalesChangesPullService>();
        PrivateField<ShipmentSyncService>(pull, "_shipmentPush").Should().BeSameAs(
            host.Services.GetRequiredService<ShipmentSyncService>());
        PrivateField<ShipmentRepository>(pull, "_shipments").Should().BeSameAs(
            host.Services.GetRequiredService<ShipmentRepository>());
    }

    [Fact]
    public void Komut_istemcisinin_mode_offu_satis_akisi_turunu_tetikler()
    {
        // W2-11 (spec 2026-10-13 §5.3): satış akışı turu komut istemcisini isteğe bağlı parametreyle dinler — kayıt
        // eksikse mode-off yalnız sonraki 30 sn'lik turda görülürdü. Tekil istemci: bekleyen komutların göndericisi de
        // aynı örneği kullanır.
        using var host = new global::OrderDeck.App.AppHost();
        var commands = host.Services.GetRequiredService<SalesCommandClient>();
        var pull = host.Services.GetRequiredService<SalesChangesPullService>();

        var listeners = PrivateField<EventHandler>(commands, "ModeOffDetected");
        listeners.Should().NotBeNull("satış akışı turu kurulurken mode-off'a abone olur");
        listeners!.GetInvocationList().Select(d => d.Target).Should().Contain(pull,
            "tekil komut istemcisinin mode-off'u tekil satış akışı turuna ulaşır");
        PrivateField<SalesCommandClient>(host.Services.GetRequiredService<PendingSalesCommandSender>(), "_commands")
            .Should().BeSameAs(commands);
    }

    [Fact]
    public void Kip_adimi_tekil_satis_akisi_turuna_bagli_acilis_ayni_gonderim_servislerini_bosaltir()
    {
        // W2-10 (spec 2026-10-13 §8.4): satış akışı turu kip adımını isteğe bağlı parametreyle alır — kayıt eksikse DI
        // sessizce null bırakır, mod On hiç açılmazdı. Açılışın 1. adımı giden kuyrukları AYNI gönderim servisleriyle
        // boşaltır (oturum ve etiket tek gönderim kapısından).
        using var host = new global::OrderDeck.App.AppHost();
        var coordinator = host.Services.GetRequiredService<SalesModeCoordinator>();

        host.Services.GetRequiredService<SalesModeCoordinator>().Should().BeSameAs(coordinator);
        PrivateField<SalesModeCoordinator>(host.Services.GetRequiredService<SalesChangesPullService>(), "_modeCoordinator")
            .Should().BeSameAs(coordinator);
        PrivateField<SalesSyncStateService>(coordinator, "_state").Should().BeSameAs(
            host.Services.GetRequiredService<SalesSyncStateService>(), "turun state'i: aynı son durum");
        var flush = PrivateField<ISalesOpeningFlush>(coordinator, "_flush").Should().BeOfType<SalesOpeningFlush>().Subject;
        PrivateField<WpfCustomerProjectionSyncService>(flush, "_customers").Should().BeSameAs(
            host.Services.GetRequiredService<WpfCustomerProjectionSyncService>());
        PrivateField<SessionOrderSyncService>(flush, "_sales").Should().BeSameAs(
            host.Services.GetRequiredService<SessionOrderSyncService>(), "gönderim kapısı tek örnekte geçerli");
        PrivateField<ShipmentSyncService>(flush, "_shipments").Should().BeSameAs(
            host.Services.GetRequiredService<ShipmentSyncService>());
        PrivateField<PaymentSyncService>(flush, "_payments").Should().BeSameAs(
            host.Services.GetRequiredService<PaymentSyncService>());
    }

    [Fact]
    public void Devredilemeyen_yayin_kurali_tur_ve_dikkat_sayacina_bagli()
    {
        // W2-09 (spec 2026-10-13 §5.7, §12 risk 4): satış akışı turu devri bekleyen yayınları oturum deposundan okur;
        // isteğe bağlı parametre DI'dan gelmezse kural hiç işlemez. Dikkat sayacı turun ölçüsünü okur.
        using var host = new global::OrderDeck.App.AppHost();
        PrivateField<SessionRepository>(host.Services.GetRequiredService<SalesChangesPullService>(), "_sessions")
            .Should().BeSameAs(host.Services.GetRequiredService<SessionRepository>());
        var counter = host.Services.GetRequiredService<SyncPendingCounter>();
        host.Services.GetRequiredService<SyncPendingCounter>().Should().BeSameAs(counter);
        PrivateField<Func<int>>(counter, "_stuckHandovers").Should().NotBeNull();
        counter.Attention().StuckHandovers.Should().Be(0, "tur koşmadı");
    }

    [Fact]
    public void Guncelleme_sorusu_tekil_Velopack_ornegi()
    {
        // W2-04 (spec 2026-10-13 §5.3): açılış ve satış komutunun client-update-required'ı AYNI soruyu açar —
        // ikinci örnek "sürerken yenisini açma" kilidini paylaşmazdı.
        using var host = new global::OrderDeck.App.AppHost();
        var prompt = host.Services.GetRequiredService<global::OrderDeck.App.Services.IAppUpdatePrompt>();

        prompt.Should().BeOfType<global::OrderDeck.App.Services.VelopackUpdatePrompt>();
        host.Services.GetRequiredService<global::OrderDeck.App.Services.IAppUpdatePrompt>().Should().BeSameAs(prompt);
    }

    [Fact]
    public void Duzenleme_yonlendiricisi_tekil_devir_kapisini_komut_istemcisini_ve_yerel_servisi_paylasir()
    {
        // W2-05 (spec 2026-10-13 §5.2, §5.3): yerel yolun kapısı kapanış gönderiminin kilitleriyle AYNI örnek olmalı
        // (yoksa "boş" okuması ile devir arasına yerel yazım girer); kart, Basılanlar ve yedek çekmecesi aynı
        // yönlendiriciyi alır — isteğe bağlı parametre DI'dan gelmezse sessizce yalnız yerel düzenleyiciye düşerdi.
        using var host = new global::OrderDeck.App.AppHost();
        var editor = host.Services.GetRequiredService<global::OrderDeck.App.Services.Sales.ILabelEditor>();

        editor.Should().BeOfType<global::OrderDeck.App.Services.Sales.SalesEditRouter>();
        host.Services.GetRequiredService<global::OrderDeck.App.Services.Sales.ILabelEditor>().Should().BeSameAs(editor);
        PrivateField<OrderDeck.Core.Sessions.HandoverGate>(editor, "_handover").Should().BeSameAs(
            host.Services.GetRequiredService<OrderDeck.Core.Sessions.HandoverGate>());
        PrivateField<SalesCommandClient>(editor, "_commands").Should().BeSameAs(
            host.Services.GetRequiredService<SalesCommandClient>());
        PrivateField<OrderDeck.Core.Sales.LabelService>(editor, "_labels").Should().BeSameAs(
            host.Services.GetRequiredService<OrderDeck.Core.Sales.LabelService>());
        PrivateField<global::OrderDeck.App.Services.Sales.SalesConnectivity>(editor, "_connectivity").Should().BeSameAs(
            host.Services.GetRequiredService<global::OrderDeck.App.Services.Sales.SalesConnectivity>());

        var detail = host.Services.GetRequiredService<global::OrderDeck.App.ViewModels.CustomerDetailViewModel>();
        PrivateField<global::OrderDeck.App.Services.Sales.ILabelEditor>(detail, "_editor").Should().BeSameAs(editor,
            "müşteri kartı isteğe bağlı parametreyle alır; gelmezse sessizce yalnız yerel düzenleyiciye düşerdi");
        // W2-08: dekontun kargo adımı da yönlendiriciden — gelmezse mod On'da yerel kargo yazımı ABORT eder, adım
        // sessizce düşerdi.
        var dekont = host.Services.GetRequiredService<global::OrderDeck.App.ViewModels.DekontEkleViewModel>();
        PrivateField<global::OrderDeck.App.Services.Sales.IShipmentEditor>(dekont, "_shipmentEditor").Should().BeSameAs(editor);
        PrivateField<OrderDeck.Core.Sales.ShipmentService>(editor, "_shipments").Should().BeSameAs(
            host.Services.GetRequiredService<OrderDeck.Core.Sales.ShipmentService>());
        // Kabuk ViewModel'i kurulmadan (zamanlayıcılar) türüyle: Basılanlar'ın parametresi isteğe bağlı ve kayıtlı.
        typeof(global::OrderDeck.App.ViewModels.MainShellViewModel).GetConstructors().Single().GetParameters()
            .Should().ContainSingle(p => p.IsOptional
                && p.ParameterType == typeof(global::OrderDeck.App.Services.Sales.ILabelEditor));
    }
}
