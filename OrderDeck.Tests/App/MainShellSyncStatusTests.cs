using System;
using System.Data;
using System.Net.Http;
using System.Threading.Tasks;
using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OrderDeck.App.Services;
using OrderDeck.App.Services.Sync;
using OrderDeck.App.ViewModels;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Core.Time;
using OrderDeck.Licensing.Api;
using OrderDeck.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.Tests.App;

/// <summary>Faz 0: kenar çubuğu durum satırı (D3), yetişilmeden yayın başlatma uyarısı (D4),
/// kapanışta gönderilmemiş kayıt uyarısı (D5) ve müşteri senkronunu baştan alma (D5b).</summary>
public sealed class MainShellSyncStatusTests
{
    /// <summary>Bağlantı açılışlarını sayar: durum satırının kaç sorgu koştuğu.</summary>
    private sealed class CountingFactory(IDbConnectionFactory inner) : IDbConnectionFactory
    {
        public int Opens { get; private set; }
        public IDbConnection Open() { Opens++; return inner.Open(); }
    }

    /// <summary>Sayacın lisans okumaları: <see cref="SyncPendingCounter.Count"/> çağrıldı mı.</summary>
    private sealed class CountingLicenseProvider : ICurrentLicenseProvider
    {
        public int Reads { get; private set; }
        public string? CurrentLicenseKey { get { Reads++; return null; } }
    }

    /// <summary>Yerel veritabanı hatası: <see cref="Failing"/> açıkken her açılış fırlatır.</summary>
    private sealed class FailingFactory(IDbConnectionFactory inner) : IDbConnectionFactory
    {
        public bool Failing { get; set; } = true;
        public IDbConnection Open() => Failing ? throw new SqliteException("disk I/O error", 10) : inner.Open();
    }

    private sealed class WarningCounter : ILogger<MainShellViewModel>
    {
        public int Warnings { get; private set; }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning) Warnings++;
        }
    }

    /// <summary>Ayrı, boş veritabanında sayan sayaç (bekleyen 0); harness'in veritabanına dokunmaz.</summary>
    private static SyncPendingCounter EmptyCounter(IDbConnectionFactory db, ICurrentLicenseProvider? license = null)
        => new(new SyncOutboxRepository(db), new SyncCursorRepository(db),
            license ?? new PaymentRequestServiceTestHelpers.NullLicenseProvider());

    private static InMemorySqlite MigratedDb()
    {
        var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        return db;
    }

    [Fact]
    public void Senkron_durumu_hero_tazelemesinde_guncellenir()
    {
        using var outboxDb = MigratedDb();
        var tracker = new SyncStatusTracker();
        using var h = MainShellTestHarness.Build(syncStatus: tracker, pendingCounter: EmptyCounter(outboxDb));

        h.Vm.SyncStatusText.Should().Be("Güncelleniyor…", "kurucudaki ilk tazeleme satırı hemen doldurur");
        h.Vm.IsSyncHealthy.Should().BeFalse();

        tracker.MarkPullSucceeded(DateTimeOffset.UtcNow, h.LicenseKey!);
        h.Vm.RefreshSyncStatus();
        h.Vm.SyncStatusText.Should().StartWith("Güncel ✓");
        h.Vm.IsSyncHealthy.Should().BeTrue();
        h.Vm.SyncStatusTooltip.Should().Be(h.Vm.SyncStatusText);
    }

    [Fact]
    public async Task Yayin_yokken_de_durum_satiri_tazelenir()
    {
        using var outboxDb = MigratedDb();
        var tracker = new SyncStatusTracker();
        using var h = MainShellTestHarness.Build(syncStatus: tracker, pendingCounter: EmptyCounter(outboxDb));
        h.Dialogs.ConfirmResult = _ => true;
        await h.Vm.EndStreamCommand.ExecuteAsync(null);           // harness yayını açık kurar
        tracker.MarkPullSucceeded(DateTimeOffset.UtcNow, h.LicenseKey!);
        var t = Environment.TickCount64 + 1_000_000;              // son tazelemeden çok sonra
        h.Vm.SyncTicks = () => t;

        h.Vm.RefreshHeroStats();                                  // oturum yok: metot erken döner
        h.Vm.SyncStatusText.Should().StartWith("Güncel ✓", "durum satırı yayından bağımsız tazelenir");
    }

    [Fact]
    public void Durum_satiri_kac_cagiran_olursa_olsun_en_cok_bes_saniyede_bir_tazelenir()
    {
        // Hero zamanlayıcısı, toplu baskı ve kuyruğa ekleme hepsi RefreshHeroStats'ı tetikler:
        // çağrı sayısı değil zaman sınırlar.
        using var outboxDb = MigratedDb();
        var counting = new CountingFactory(outboxDb);
        using var h = MainShellTestHarness.Build(syncStatus: new SyncStatusTracker(),
            pendingCounter: EmptyCounter(counting));
        var t = Environment.TickCount64 + 1_000_000;
        h.Vm.SyncTicks = () => t;
        h.Vm.RefreshHeroStats();                                  // kapı açık: tazeler
        var opens = counting.Opens;

        for (var i = 0; i < 20; i++) h.Vm.RefreshHeroStats();
        MainShellTestHarness.EnqueueLabel(h.Vm, "ornek_musteri", 100m);   // kuyruk değişimi de tetikler
        t += 4_999;
        h.Vm.RefreshHeroStats();
        counting.Opens.Should().Be(opens, "son tazelemeden beri 5 sn geçmedi");

        t += 1;
        h.Vm.RefreshHeroStats();
        h.Vm.RefreshHeroStats();
        counting.Opens.Should().Be(opens + 1, "5 sn dolunca bir kez (dikkat sayımı; bekleyen gösterilmiyor)");
    }

    [Fact]
    public void Senkron_sorgusu_patlarsa_kabuk_acilir_etiket_yazimi_tamamlanir_uyari_seri_basina_bir_kez()
    {
        // Durum satırı kurucudan, zamanlayıcıdan ve PrintQueue.CollectionChanged'den (WriteOrder'ın
        // ortasında) çağrılır: senkron sorgusunun hatası kabuğu açılmaz bırakmamalı, üst üste
        // MessageBox açmamalı, etiket yazımını yarıda kesmemeli.
        using var outboxDb = MigratedDb();
        var failing = new FailingFactory(outboxDb);
        var log = new WarningCounter();
        using var h = MainShellTestHarness.Build(syncStatus: new SyncStatusTracker(),
            pendingCounter: EmptyCounter(failing), log: log);

        h.Vm.SyncStatusText.Should().Be("Senkron durumu okunamadı");
        h.Vm.IsSyncHealthy.Should().BeFalse();
        h.Vm.SyncStatusTooltip.Should().Be("Senkron durumu okunamadı\nSürerse destekle iletişime geç.");

        var t = Environment.TickCount64 + 1_000_000;
        h.Vm.SyncTicks = () => t += 5_000;                        // her çağrı tazeler
        MainShellTestHarness.EnqueueLabel(h.Vm, "ornek_musteri", 100m);
        MainShellTestHarness.EnqueueLabel(h.Vm, "ornek_musteri", 150m);
        h.Vm.RefreshHeroStats();

        h.Vm.PrintQueue.Should().HaveCount(2, "iki etiket yazımı da tamamlandı");
        h.Labels.GetQueue(h.Sessions.GetActive()!.Id).Should().HaveCount(2);
        log.Warnings.Should().Be(1, "hata serisi başına tek uyarı — günlük her 5 sn'de dolmasın");

        failing.Failing = false;
        h.Vm.RefreshSyncStatus();
        h.Vm.SyncStatusText.Should().Be("Güncelleniyor…");
        failing.Failing = true;
        h.Vm.RefreshSyncStatus();
        log.Warnings.Should().Be(2, "düzelip yeniden bozulan yeni bir seri");
    }

    [Fact]
    public void Lisans_yoksa_durum_satiri_notr_ve_sorgu_kosmaz()
    {
        // Deneme sürümü: StartupFlow lisanssız içeri alır, senkron hiç koşmaz — kalıcı turuncu
        // "Çevrimdışı — N" yanlış olurdu (bütün müşteriler "bekliyor" sayılırdı).
        using var outboxDb = MigratedDb();
        var counting = new CountingFactory(outboxDb);
        using var h = MainShellTestHarness.Build(syncStatus: new SyncStatusTracker(),
            pendingCounter: EmptyCounter(counting), licensed: false);

        h.Vm.RefreshSyncStatus();

        h.Vm.SyncStatusText.Should().Be("Senkron kapalı (lisans yok)");
        h.Vm.IsSyncHealthy.Should().BeTrue();
        counting.Opens.Should().Be(0, "lisanssızken bekleyen ve dikkat sayımları atlanır");
    }

    [Fact]
    public void Bekleyen_sayi_gosterilmeyecekse_sayilmaz()
    {
        using var outboxDb = MigratedDb();
        var license = new CountingLicenseProvider();
        var tracker = new SyncStatusTracker();
        using var h = MainShellTestHarness.Build(syncStatus: tracker, pendingCounter: EmptyCounter(outboxDb, license));
        var reads = license.Reads;

        h.Vm.RefreshSyncStatus();
        h.Vm.SyncStatusText.Should().Be("Güncelleniyor…");
        license.Reads.Should().Be(reads, "\"Güncelleniyor…\" sayı göstermez — sayım SQL'i koşmaz");

        tracker.MarkPullSucceeded(DateTimeOffset.UtcNow, h.LicenseKey!);
        h.Vm.RefreshSyncStatus();
        license.Reads.Should().Be(reads + 1);
    }

    [Fact]
    public void Kalici_uyarida_satir_sari_ipucu_ne_yapilacagini_soyler()
    {
        using var outboxDb = MigratedDb();
        using (var c = outboxDb.Open())
            c.Execute("INSERT INTO CustomerFeedFailure (ItemId, ChangeSeq, Attempts, LastError, FirstFailedAt, SkippedAt) " +
                      "VALUES (@id, 3, 5, 'x', 100, 200)", new { id = Guid.NewGuid().ToString("N") });
        var tracker = new SyncStatusTracker();
        using var h = MainShellTestHarness.Build(syncStatus: tracker, pendingCounter: EmptyCounter(outboxDb));
        tracker.MarkPullSucceeded(DateTimeOffset.UtcNow, h.LicenseKey!);

        h.Vm.RefreshSyncStatus();

        h.Vm.SyncStatusText.Should().EndWith("1 müşteri değişikliği uygulanamadı");
        h.Vm.IsSyncHealthy.Should().BeFalse();
        h.Vm.SyncStatusTooltip.Should().StartWith(h.Vm.SyncStatusText + "\n").And.Contain("destek");
    }

    // ── D4: yetişilmeden yayın başlatma (harness yayını açık kurar; önce bitirilir) ─────

    [Fact]
    public async Task Yetisilmeden_yayin_baslatilirsa_onay_sorulur_hayir_derse_baslamaz()
    {
        using var h = MainShellTestHarness.Build(syncStatus: new SyncStatusTracker());
        h.Dialogs.ConfirmResult = _ => true;
        await h.Vm.EndStreamCommand.ExecuteAsync(null);
        h.Dialogs.ConfirmResult = title => title != "Güncelleniyor";

        h.Vm.StartStreamCommand.Execute(null);

        h.Dialogs.Confirmations.Should().ContainSingle(c => c.Title == "Güncelleniyor")
            .Which.Message.Should().Be(
                "Müşteri bilgilerindeki son değişiklikler henüz inmedi (diğer bilgisayarlar / müşteri uygulaması). " +
                "İnternet yoksa yine de başlatabilirsin. Yayını başlatayım mı?");
        h.Sessions.GetActive().Should().BeNull("operatör hayır dedi");
    }

    [Fact]
    public async Task Yetisilmeden_yayin_evet_derse_baslar()
    {
        // Engellemiyoruz — internet yokken de yayın yapılabilmeli.
        using var h = MainShellTestHarness.Build(syncStatus: new SyncStatusTracker());
        h.Dialogs.ConfirmResult = _ => true;
        await h.Vm.EndStreamCommand.ExecuteAsync(null);

        h.Vm.StartStreamCommand.Execute(null);

        h.Dialogs.Confirmations.Should().ContainSingle(c => c.Title == "Güncelleniyor");
        h.Sessions.GetActive().Should().NotBeNull();
    }

    [Fact]
    public async Task Yetisildiyse_yayin_sorusuz_baslar()
    {
        var tracker = new SyncStatusTracker();
        using var h = MainShellTestHarness.Build(syncStatus: tracker);
        tracker.MarkPullSucceeded(DateTimeOffset.UtcNow, h.LicenseKey!);
        h.Dialogs.ConfirmResult = _ => true;
        await h.Vm.EndStreamCommand.ExecuteAsync(null);

        h.Vm.StartStreamCommand.Execute(null);

        h.Dialogs.Confirmations.Should().NotContain(c => c.Title == "Güncelleniyor");
        h.Sessions.GetActive().Should().NotBeNull();
    }

    [Fact]
    public async Task Baska_lisansin_yetismesi_bu_lisansi_yetismis_saymaz()
    {
        // Lisans değişimini akış servisi bir sonraki turunda görür (≤ 30 sn): o arada önceki
        // lisansın yetişmesi yeni lisansın verisini anlatmaz (C10 incelemesi).
        var tracker = new SyncStatusTracker();
        tracker.MarkPullSucceeded(DateTimeOffset.UtcNow, $"lisans-{Guid.NewGuid():N}");
        using var h = MainShellTestHarness.Build(syncStatus: tracker);
        h.Dialogs.ConfirmResult = _ => true;
        await h.Vm.EndStreamCommand.ExecuteAsync(null);
        h.Dialogs.ConfirmResult = title => title != "Güncelleniyor";

        h.Vm.StartStreamCommand.Execute(null);

        h.Dialogs.Confirmations.Should().Contain(c => c.Title == "Güncelleniyor");
        h.Sessions.GetActive().Should().BeNull();
    }

    [Fact]
    public async Task Lisans_yoksa_yetisme_sorulmaz()
    {
        // Deneme sürümünde senkron hiç koşmaz — soru her yayında çıkar ve hiç geçmezdi.
        using var h = MainShellTestHarness.Build(syncStatus: new SyncStatusTracker(), licensed: false);
        h.LicenseKey.Should().BeNull();
        h.Dialogs.ConfirmResult = _ => true;
        await h.Vm.EndStreamCommand.ExecuteAsync(null);
        h.Vm.StartStreamCommand.CanExecute(null).Should().BeTrue("deneme sürümü yazabilir — yoksa test bir şey sınamazdı");

        h.Vm.StartStreamCommand.Execute(null);

        h.Dialogs.Confirmations.Should().NotContain(c => c.Title == "Güncelleniyor");
        h.Sessions.GetActive().Should().NotBeNull();
    }

    // ── D5: kapanışta gönderilmemiş kayıt uyarısı ──────────────────────

    /// <summary>Sayacın veritabanına gönderilmemiş bir müşteri (imleç 0 → bekleyen 1).</summary>
    private static void SeedUnsent(IDbConnectionFactory db)
        => new CustomerRepository(db).Insert(new Customer(Guid.NewGuid().ToString("N"), "tiktok", "ornek_musteri",
            "Örnek Müşteri", null, 1, 1, false, null, null, 0, 0m, null, null, null));

    [Fact]
    public void Kapanista_bekleyen_yoksa_sorulmaz()
    {
        using var outboxDb = MigratedDb();
        using var h = MainShellTestHarness.Build(syncStatus: new SyncStatusTracker(), pendingCounter: EmptyCounter(outboxDb));

        h.Vm.ConfirmCloseWithUnsentRecords().Should().Be(CloseSyncChoice.Close);

        h.Dialogs.ThreeWayConfirmations.Should().BeEmpty();
    }

    [Theory]
    [InlineData(true, CloseSyncChoice.FlushThenClose)]
    [InlineData(false, CloseSyncChoice.Close)]
    [InlineData(null, CloseSyncChoice.Cancel)]
    public void Kapanista_bekleyen_varsa_sorulur_cevap_secimi_belirler(bool? answer, CloseSyncChoice expected)
    {
        using var outboxDb = MigratedDb();
        SeedUnsent(outboxDb);
        var tracker = new SyncStatusTracker();
        using var h = MainShellTestHarness.Build(syncStatus: tracker, pendingCounter: EmptyCounter(outboxDb));
        tracker.MarkPullSucceeded(DateTimeOffset.UtcNow, h.LicenseKey!);
        h.Dialogs.ThreeWayResult = _ => answer;

        h.Vm.ConfirmCloseWithUnsentRecords().Should().Be(expected);

        var asked = h.Dialogs.ThreeWayConfirmations.Should().ContainSingle().Subject;
        asked.Title.Should().Be("Gönderilmemiş kayıt var");
        asked.Message.Should().Be(
            "1 kayıt henüz sunucuya gitmedi.\n" +
            "Senkron durumu: Gönderiliyor (1)\n\n" +
            "Evet: gönder ve kapat (en fazla 30 sn)\n" +
            "Hayır: yine de kapat — kayıtlar kaybolmaz, bu bilgisayar bir sonraki açılışta gönderir\n" +
            "İptal: kapatma");
    }

    [Fact]
    public void Kapanis_uyarisi_durum_satiriyla_ayni_sayaci_ve_anlik_goruntuyu_kullanir()
    {
        // Çevrimdışı bilgisayarda "gönder ve kapat"ın işe yaramayacağı uyarının kendisinden okunur;
        // kalıcı uyarılar da kenar çubuğundaki gibi eklenir (inceleme küçük 4).
        using var outboxDb = MigratedDb();
        SeedUnsent(outboxDb);
        using (var c = outboxDb.Open())
            c.Execute("INSERT INTO CustomerFeedFailure (ItemId, ChangeSeq, Attempts, LastError, FirstFailedAt, SkippedAt) " +
                      "VALUES (@id, 3, 5, 'x', 100, 200)", new { id = Guid.NewGuid().ToString("N") });
        var tracker = new SyncStatusTracker();
        using var h = MainShellTestHarness.Build(syncStatus: tracker, pendingCounter: EmptyCounter(outboxDb));
        tracker.MarkPullSucceeded(DateTimeOffset.UtcNow - TimeSpan.FromMinutes(10), h.LicenseKey!);
        h.Vm.RefreshSyncStatus();
        h.Vm.SyncStatusText.Should().Be("Çevrimdışı — 1 değişiklik bekliyor · 1 müşteri değişikliği uygulanamadı");

        h.Vm.ConfirmCloseWithUnsentRecords();

        h.Dialogs.ThreeWayConfirmations.Should().ContainSingle()
            .Which.Message.Should().Contain("\nSenkron durumu: " + h.Vm.SyncStatusText + "\n");
    }

    [Fact]
    public void Kapanista_lisans_yoksa_sorulmaz_sayim_kosmaz()
    {
        // Deneme sürümü: senkron hiç koşmaz — imleç yok, bütün müşteriler "bekliyor" sayılır ve
        // uyarı her kapanışta çıkıp hiç geçmezdi.
        using var outboxDb = MigratedDb();
        SeedUnsent(outboxDb);
        var counting = new CountingFactory(outboxDb);
        using var h = MainShellTestHarness.Build(syncStatus: new SyncStatusTracker(),
            pendingCounter: EmptyCounter(counting), licensed: false);
        var opens = counting.Opens;

        h.Vm.ConfirmCloseWithUnsentRecords().Should().Be(CloseSyncChoice.Close);

        h.Dialogs.ThreeWayConfirmations.Should().BeEmpty();
        counting.Opens.Should().Be(opens);
    }

    [Fact]
    public void Kapanista_sayim_okunamazsa_sorulmadan_kapanir_uyari_gunluge()
    {
        // Yerel veritabanı hatası kapanışı engellemez: gönderim de aynı veritabanını okuyacaktı;
        // kayıtlar yerelde kalır, sonraki açılışta gider.
        using var outboxDb = MigratedDb();
        var failing = new FailingFactory(outboxDb) { Failing = false };
        var log = new WarningCounter();
        var tracker = new SyncStatusTracker();
        using var h = MainShellTestHarness.Build(syncStatus: tracker, pendingCounter: EmptyCounter(failing), log: log);
        tracker.MarkPullSucceeded(DateTimeOffset.UtcNow, h.LicenseKey!);
        var warnings = log.Warnings;
        failing.Failing = true;

        h.Vm.ConfirmCloseWithUnsentRecords().Should().Be(CloseSyncChoice.Close);

        h.Dialogs.ThreeWayConfirmations.Should().BeEmpty();
        log.Warnings.Should().Be(warnings + 1);
    }

    [Fact]
    public void Kapanista_senkron_bagli_degilse_sorulmaz()
    {
        using var h = MainShellTestHarness.Build();

        h.Vm.ConfirmCloseWithUnsentRecords().Should().Be(CloseSyncChoice.Close);

        h.Dialogs.ThreeWayConfirmations.Should().BeEmpty();
    }

    // ── D5b: müşteri senkronunu baştan al (destek eylemi) ──────────────

    private const string ResyncTitle = "Senkronu baştan al";

    private sealed class FixedLicense(string? key) : ICurrentLicenseProvider
    {
        public string? CurrentLicenseKey { get; } = key;
    }

    /// <summary>Gerçek akış servisi; sıfırlama yerel olduğu için ağa gitmez (sahte istemci 404).</summary>
    private static CustomerChangesPullService PullService(IDbConnectionFactory db, string? licenseKey)
    {
        var api = new LicenseApiClient(
            new HttpClient(new FakeHttpMessageHandler(_ => FakeHttpMessageHandler.Empty(404)))
                { BaseAddress = new Uri("https://test.local") },
            new LicenseTokenStore());
        var license = new FixedLicense(licenseKey);
        var clock = new SystemClock();
        var tracker = new SyncStatusTracker();
        var sync = new CustomerSyncRepository(db);
        var cursors = new SyncCursorRepository(db);
        var push = new WpfCustomerProjectionSyncService(api, sync, cursors, license, clock,
            NullLogger<WpfCustomerProjectionSyncService>.Instance, tracker);
        return new CustomerChangesPullService(api, new CustomerRepository(db), sync, cursors, push, license, clock,
            tracker, NullLogger<CustomerChangesPullService>.Instance);
    }

    private const string ResyncConfirmText =
        "Müşteri senkronu baştan alınacak: bu bilgisayardaki bütün müşteriler sunucuya yeniden " +
        "gönderilir ve diğer bilgisayarların değişiklikleri baştan indirilir. Hiçbir kayıt silinmez; " +
        "birkaç dakika durum satırında \"Gönderiliyor\" ve \"Güncelleniyor\" görünmesi normal.\n\n" +
        "Yalnız destek istediğinde kullan. Devam edilsin mi?";

    /// <summary>Harness yayını açık kurar; destek eylemi yayın sürerken kullanılamaz.</summary>
    private static async Task EndStreamAsync(MainShellTestHarness.Harness h)
    {
        h.Dialogs.ConfirmResult = _ => true;
        await h.Vm.EndStreamCommand.ExecuteAsync(null);
        h.Dialogs.ConfirmResult = _ => false;
        h.Dialogs.Confirmations.Clear();
    }

    [Fact]
    public async Task Senkronu_bastan_al_onay_sorar_hayir_derse_hicbir_sey_degismez()
    {
        using var syncDb = MigratedDb();
        var key = $"lisans-{Guid.NewGuid():N}";
        var cursors = new SyncCursorRepository(syncDb);
        cursors.Upsert(CustomerChangesPullService.CursorName, key, seq: 9);
        cursors.Upsert(WpfCustomerProjectionSyncService.CursorName, key, seq: 9);
        using var h = MainShellTestHarness.Build(customerPull: PullService(syncDb, key));
        await EndStreamAsync(h);

        await h.Vm.ResyncCustomersCommand.ExecuteAsync(null);

        h.Dialogs.Confirmations.Should().ContainSingle(c => c.Title == ResyncTitle)
            .Which.Message.Should().Be(ResyncConfirmText);
        cursors.Get(CustomerChangesPullService.CursorName, key)!.Seq.Should().Be(9);
        cursors.Get(WpfCustomerProjectionSyncService.CursorName, key)!.Seq.Should().Be(9);
    }

    [Fact]
    public async Task Senkronu_bastan_al_evet_derse_imlecler_sifirlanir_durum_satiri_tazelenir()
    {
        using var syncDb = MigratedDb();
        SeedUnsent(syncDb);
        var key = $"lisans-{Guid.NewGuid():N}";
        var cursors = new SyncCursorRepository(syncDb);
        cursors.Upsert(CustomerChangesPullService.CursorName, key, seq: 1_000_000);
        cursors.Upsert(WpfCustomerProjectionSyncService.CursorName, key, seq: 1_000_000);
        var tracker = new SyncStatusTracker();
        using var h = MainShellTestHarness.Build(syncStatus: tracker,
            pendingCounter: EmptyCounter(syncDb, new FixedLicense(key)), customerPull: PullService(syncDb, key));
        tracker.MarkPullSucceeded(DateTimeOffset.UtcNow, h.LicenseKey!);
        await EndStreamAsync(h);
        h.Vm.RefreshSyncStatus();
        h.Vm.SyncStatusText.Should().StartWith("Güncel ✓");
        h.Dialogs.ConfirmResult = title => title == ResyncTitle;

        await h.Vm.ResyncCustomersCommand.ExecuteAsync(null);

        cursors.Get(CustomerChangesPullService.CursorName, key)!.Seq.Should().Be(0);
        cursors.Get(WpfCustomerProjectionSyncService.CursorName, key)!.Seq.Should().Be(0);
        h.Vm.SyncStatusText.Should().Be("Gönderiliyor (1)", "bütün müşteriler yeniden gönderilecek — beklemeden görünür");
        h.Dialogs.Shown.Should().BeEmpty();
    }

    [Fact]
    public async Task Senkronu_bastan_al_yayin_surerken_kullanilamaz_nedeni_soylenir()
    {
        // Bütün müşterilerin yeniden gönderimi sipariş senkronuyla sunucunun hız sınırını paylaşır:
        // yayın sürerken menü öğesi kapalı; yine de çağrılırsa tek satırlık neden, soru yok.
        using var syncDb = MigratedDb();
        var key = $"lisans-{Guid.NewGuid():N}";
        var cursors = new SyncCursorRepository(syncDb);
        cursors.Upsert(CustomerChangesPullService.CursorName, key, seq: 9);
        using var h = MainShellTestHarness.Build(customerPull: PullService(syncDb, key));
        h.Sessions.GetActive().Should().NotBeNull("harness yayını açık kurar");
        h.Dialogs.ConfirmResult = _ => true;

        h.Vm.ResyncCustomersCommand.CanExecute(null).Should().BeFalse();
        await h.Vm.ResyncCustomersCommand.ExecuteAsync(null);

        h.Dialogs.Confirmations.Should().NotContain(c => c.Title == ResyncTitle);
        h.Dialogs.Shown.Should().ContainSingle().Which.Should().Be((ResyncTitle,
            "Yayın sürerken müşteri senkronu baştan alınamaz — yayını bitirdikten sonra dene.", DialogSeverity.Info));
        cursors.Get(CustomerChangesPullService.CursorName, key)!.Seq.Should().Be(9);

        await EndStreamAsync(h);
        h.Vm.ResyncCustomersCommand.CanExecute(null).Should().BeTrue("yayın bitti");
        h.Dialogs.ConfirmResult = _ => true;
        h.Vm.StartStreamCommand.Execute(null);
        h.Vm.ResyncCustomersCommand.CanExecute(null).Should().BeFalse("yeni yayın başladı");
    }

    [Fact]
    public async Task Senkronu_bastan_al_lisans_yoksa_soyler()
    {
        using var syncDb = MigratedDb();
        using var h = MainShellTestHarness.Build(customerPull: PullService(syncDb, licenseKey: null));
        await EndStreamAsync(h);
        h.Dialogs.ConfirmResult = _ => true;

        await h.Vm.ResyncCustomersCommand.ExecuteAsync(null);

        h.Dialogs.Shown.Should().ContainSingle().Which.Should().Be(
            (ResyncTitle, "Lisans bulunamadı — senkron baştan alınamadı.", DialogSeverity.Warning));
    }

    [Fact]
    public async Task Senkronu_bastan_al_hatasi_operatore_soylenir_kabuk_etkilenmez()
    {
        using var syncDb = MigratedDb();
        var log = new WarningCounter();
        using var h = MainShellTestHarness.Build(log: log,
            customerPull: PullService(new FailingFactory(syncDb), $"lisans-{Guid.NewGuid():N}"));
        await EndStreamAsync(h);
        h.Dialogs.ConfirmResult = _ => true;
        var warnings = log.Warnings;

        await h.Vm.ResyncCustomersCommand.ExecuteAsync(null);

        h.Dialogs.Shown.Should().ContainSingle().Which.Severity.Should().Be(DialogSeverity.Warning);
        h.Dialogs.Shown[0].Title.Should().Be(ResyncTitle);
        h.Dialogs.Shown[0].Message.Should().StartWith("Müşteri senkronu baştan alınamadı");
        log.Warnings.Should().Be(warnings + 1);
    }
}
