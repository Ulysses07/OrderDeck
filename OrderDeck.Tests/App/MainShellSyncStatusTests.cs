using System;
using System.Data;
using System.Threading.Tasks;
using Dapper;
using FluentAssertions;
using OrderDeck.App.Services.Sync;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.Tests.App;

/// <summary>Faz 0: kenar çubuğu durum satırı (D3) ve yetişilmeden yayın başlatma uyarısı (D4).</summary>
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

        for (var i = 0; i < 5; i++) h.Vm.RefreshHeroStats();      // sayım beş çağrıda bir
        h.Vm.SyncStatusText.Should().Be("Güncelleniyor…");
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

        for (var i = 0; i < 5; i++) h.Vm.RefreshHeroStats();      // oturum yok: metot erken döner
        h.Vm.SyncStatusText.Should().StartWith("Güncel ✓", "durum satırı yayından bağımsız tazelenir");
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
}
