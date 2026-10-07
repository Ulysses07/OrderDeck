using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using OrderDeck.App.Services.Sync;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Core.Time;
using OrderDeck.Licensing.Api;
using OrderDeck.Licensing.Api.Models;
using OrderDeck.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.Tests.Services.Sync;

/// <summary>
/// Müşteri değişiklik akışı (Bölüm C7, kural 8). Eski <c>since</c> ingest'inin KVKK sözleşmesi
/// korunur: silinen kişi yerelde boşaltılır (satır silinmez, mali kayıt ve kara liste kalır),
/// yerelde hiç olmayan kişi için "[Silindi]" kartı açılmaz.
/// </summary>
public sealed class CustomerChangesPullServiceTests
{
    private sealed class FakeLicenseProvider : ICurrentLicenseProvider
    {
        public string? CurrentLicenseKey { get; set; }
    }

    private sealed class FixedClock : IClock
    {
        public long UnixNow() => 1_791_000_000L;
    }

    /// <summary>Kurulan hata, fabrikanın SONRAKİ ilk <c>Open</c>'ında bir kez fırlatılır
    /// (yalnız senkron deposunun fabrikası sarılır).</summary>
    private sealed class FaultyFactory(IDbConnectionFactory inner) : IDbConnectionFactory
    {
        private Exception? _next;

        public void FailNextOpen(Exception ex) => _next = ex;

        public System.Data.IDbConnection Open()
        {
            var ex = Interlocked.Exchange(ref _next, null);
            if (ex is not null) throw ex;
            return inner.Open();
        }
    }

    private static readonly Guid LicenseId = Guid.NewGuid();
    private static readonly string Lisans = $"lisans-{Guid.NewGuid():N}";
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static readonly DateTimeOffset T1 = new(2026, 10, 1, 10, 0, 0, TimeSpan.Zero);

    private sealed record Fixture(
        CustomerChangesPullService Svc, WpfCustomerProjectionSyncService Push,
        CustomerRepository Customers, SyncCursorRepository Cursors, SyncStatusTracker Tracker,
        InMemorySqlite Db, FakeHttpMessageHandler Http, List<WpfCustomerSyncRequest> Pushed,
        FaultyFactory Faults) : IDisposable
    {
        public long FeedCursor => Cursors.Get(CustomerChangesPullService.CursorName, Lisans)?.Seq ?? 0L;
        public int Posts => Http.Requests.Count(r => r.RequestUri!.AbsolutePath.EndsWith("/wpf-customers/sync"));
        public int Pulls => Http.Requests.Count(r => r.RequestUri!.AbsolutePath.EndsWith("/wpf-customers/changes"));
        public void Dispose() => Db.Dispose();
    }

    /// <param name="changes">afterSeq → sayfa yanıtı.</param>
    /// <param name="sync">Gönderim yanıtı (varsayılan: yönlendirmesiz).</param>
    private static Fixture Build(Func<long, HttpResponseMessage> changes, Func<HttpResponseMessage>? sync = null,
        bool license = true, CustomerBusySet? busy = null)
    {
        var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var customers = new CustomerRepository(db);
        var faults = new FaultyFactory(db);
        var syncRepo = new CustomerSyncRepository(faults, busy);
        var cursors = new SyncCursorRepository(db);
        var pushed = new List<WpfCustomerSyncRequest>();
        var http = new FakeHttpMessageHandler(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path == "/api/v1/me/licenses")
                return FakeHttpMessageHandler.Json(200, $"[{{\"id\":\"{LicenseId}\",\"licenseKey\":\"{Lisans}\"}}]");
            if (path.EndsWith("/wpf-customers/changes"))
                return changes(AfterSeq(req));
            if (path.EndsWith("/wpf-customers/sync"))
            {
                lock (pushed)
                    pushed.Add(JsonSerializer.Deserialize<WpfCustomerSyncRequest>(
                        req.Content!.ReadAsStringAsync().GetAwaiter().GetResult(), Web)!);
                return sync?.Invoke()
                    ?? FakeHttpMessageHandler.Json(200, """{"synced":1,"retroactiveMatches":0,"redirects":[]}""");
            }
            return FakeHttpMessageHandler.Empty(404);
        });
        var api = new LicenseApiClient(new HttpClient(http) { BaseAddress = new Uri("https://test.local") }, new LicenseTokenStore());
        var lic = new FakeLicenseProvider { CurrentLicenseKey = license ? Lisans : null };
        var clock = new FixedClock();
        var push = new WpfCustomerProjectionSyncService(api, syncRepo, cursors, lic, clock,
            NullLogger<WpfCustomerProjectionSyncService>.Instance);
        var tracker = new SyncStatusTracker();
        var svc = new CustomerChangesPullService(api, customers, syncRepo, cursors, push, lic, clock, tracker,
            NullLogger<CustomerChangesPullService>.Instance);
        return new Fixture(svc, push, customers, cursors, tracker, db, http, pushed, faults);
    }

    private static Fixture Build(Func<long, string> changes, Func<string>? sync = null, bool license = true,
        CustomerBusySet? busy = null)
        => Build(after => FakeHttpMessageHandler.Json(200, changes(after)),
            sync is null ? null : () => FakeHttpMessageHandler.Json(200, sync()),
            license, busy);

    private static long AfterSeq(HttpRequestMessage r)
        => long.Parse(Regex.Match(r.RequestUri!.Query, @"afterSeq=(-?\d+)").Groups[1].Value);

    private static string Page(long next, params WpfCustomerChangeItem[] items)
        => JsonSerializer.Serialize(new WpfCustomerChangesPage(items, next), Web);

    private static string ResetPage(long next, params WpfCustomerChangeItem[] items)
        => JsonSerializer.Serialize(new WpfCustomerChangesPage(items, next, CursorReset: true), Web);

    /// <summary>Boş asıl kayıt satırı; testler `with` ile doldurur.</summary>
    private static WpfCustomerChangeItem Item(Guid id, string username, long seq) => new(
        id, "tiktok", username, null, null,
        null, null, null, null, null, null,
        null, null, null, null, false, null,
        null, null, null, null, null, null,
        false, null, false, null,
        false, null, null, null, null, null,
        seq);

    private static WpfCustomerChangeItem Alias(Guid id, string username, Guid target, long seq)
        => Item(id, username, seq) with { MergedIntoId = target };

    private static string LocalRow(Fixture fx, string username, Guid? id = null)
    {
        var key = (id ?? Guid.NewGuid()).ToString("N");
        fx.Customers.Insert(new Customer(key, "tiktok", username, "takma", null, 100, 200,
            false, null, null, 3, 450m, null, null, null));
        return key;
    }

    /// <summary>Ham varlık denetimi: <c>GetById</c> yönlendirmeyi izler (U12, C8).</summary>
    private static bool Exists(Fixture fx, string id)
    {
        using var c = fx.Db.Open();
        return c.ExecuteScalar<int>("SELECT COUNT(*) FROM Customer WHERE Id = @id", new { id }) == 1;
    }

    private static long MaxSyncSeq(Fixture fx)
    {
        using var c = fx.Db.Open();
        return c.ExecuteScalar<long>("SELECT COALESCE(MAX(SyncSeq), 0) FROM Customer");
    }

    // ── temel ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Lisans_yoksa_istek_yapilmaz()
    {
        using var fx = Build(_ => Page(0), license: false);
        (await fx.Svc.PullOnceAsync(CancellationToken.None)).Should().Be(CustomerPullOutcome.NoLicense);
        fx.Http.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Bos_sayfaya_kadar_sayfalar_imleci_yazar_ve_durum_izleyicisine_isler()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        using var fx = Build(after => after switch
        {
            0 => Page(5, Item(a, "ornek_a", 5)),
            5 => Page(9, Item(b, "ornek_b", 9)),
            _ => Page(after),
        });

        (await fx.Svc.PullOnceAsync(CancellationToken.None)).Should().Be(CustomerPullOutcome.CaughtUp);

        fx.Customers.GetById(a.ToString("N")).Should().NotBeNull();
        fx.Customers.GetById(b.ToString("N")).Should().NotBeNull();
        fx.FeedCursor.Should().Be(9);
        fx.Pulls.Should().Be(3, "dolu olmayan sayfa son sayılmaz — boş sayfaya kadar");
        fx.Tracker.IsInitialCatchUpDone.Should().BeTrue();
    }

    [Fact]
    public async Task Baska_bilgisayarin_musterisi_tum_birimleriyle_damgalariyla_iner()
    {
        var id = Guid.NewGuid();
        using var fx = Build(after => after == 0
            ? Page(7, Item(id, "ornek", 7) with
            {
                FullName = "Örnek Müşteri", FullNameChangedAt = T1,
                City = "İzmir", District = "Bornova", AddressChangedAt = T1.AddMinutes(1),
                IsBlacklisted = true, BlacklistReason = "ödemedi", BlacklistedAt = T1, BlacklistChangedAt = T1,
            })
            : Page(after));

        await fx.Svc.PullOnceAsync(CancellationToken.None);

        var c = fx.Customers.GetById(id.ToString("N"))!;
        c.FullName.Should().Be("Örnek Müşteri");
        c.City.Should().Be("İzmir");
        c.IsBlacklisted.Should().BeTrue();
        c.BlacklistedAt.Should().Be(T1.ToUnixTimeSeconds());
        using var conn = fx.Db.Open();
        conn.ExecuteScalar<long?>("SELECT AddressChangedAt FROM Customer WHERE Id = @id", new { id = id.ToString("N") })
            .Should().Be(T1.AddMinutes(1).ToUnixTimeMilliseconds());
    }

    [Fact]
    public async Task Eklenen_satir_turun_sonunda_hemen_gonderilir()
    {
        // U2: sunucudan eklenen satırın tek yankısı 60 sn'lik gönderim turunu beklemez.
        var id = Guid.NewGuid();
        using var fx = Build(after => after == 0 ? Page(4, Item(id, "ornek", 4)) : Page(after));

        await fx.Svc.PullOnceAsync(CancellationToken.None);

        fx.Pushed.SelectMany(p => p.Customers).Should().Contain(i => i.Id == id);
        fx.Push.Watermark(Lisans).Should().Be(MaxSyncSeq(fx));
    }

    [Fact]
    public async Task Hata_imleci_son_uygulanan_ogede_birakir()
    {
        var a = Guid.NewGuid();
        using var fx = Build(after => after == 0
            ? Page(5, Item(a, "ornek", 5))
            : throw new HttpRequestException("ağ yok"));      // ikinci sayfa düşer

        (await fx.Svc.PullOnceAsync(CancellationToken.None)).Should().Be(CustomerPullOutcome.Failed);

        fx.FeedCursor.Should().Be(5);
        fx.Tracker.IsInitialCatchUpDone.Should().BeFalse();
    }

    [Fact]
    public async Task Hiz_siniri_429_turu_basarisiz_sayar_ilerleme_sayfa_basina_kalir()
    {
        // C5 incelemesi: sunucu IP başına dakikada ~100 istek kabul eder; ilk büyük yetişme 429'a
        // takılabilir. Tur başarısız, imleç son tamamlanan sayfada — sonraki tur oradan sürer.
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var limited = true;
        using var fx = Build(after => after switch
        {
            0 => FakeHttpMessageHandler.Json(200, Page(5, Item(a, "ornek_a", 5))),
            5 when limited => FakeHttpMessageHandler.Empty(429),
            5 => FakeHttpMessageHandler.Json(200, Page(8, Item(b, "ornek_b", 8))),
            _ => FakeHttpMessageHandler.Json(200, Page(after)),
        });

        (await fx.Svc.PullOnceAsync(CancellationToken.None)).Should().Be(CustomerPullOutcome.Failed);
        fx.FeedCursor.Should().Be(5);
        fx.Tracker.IsInitialCatchUpDone.Should().BeFalse();

        limited = false;
        (await fx.Svc.PullOnceAsync(CancellationToken.None)).Should().Be(CustomerPullOutcome.CaughtUp);
        fx.Customers.GetById(b.ToString("N")).Should().NotBeNull();
        fx.FeedCursor.Should().Be(8);
        fx.Http.Requests.Count(r => r.RequestUri!.AbsolutePath.EndsWith("/wpf-customers/changes")
                                    && AfterSeq(r) == 0).Should().Be(1, "tamamlanan sayfa yeniden istenmez");
    }

    // ── KVKK (eski ingest'in sözleşmesi + kural 8) ──────────────────────

    [Fact]
    public async Task Silinen_kisi_yerelde_bosaltilir_mali_kayit_ve_kara_liste_kalir()
    {
        var id = Guid.NewGuid();
        using var fx = Build(after => after == 0
            ? Page(3, Item(id, "SILINEN", 3) with { PurgedAt = T1 })
            : Page(after));
        var local = id.ToString("N");
        fx.Customers.Insert(new Customer(local, "tiktok", "silinen", "Örnek M.", "https://cdn.example/a.jpg",
            1000, 2000, true, "ödeme yapmadı", null, 3, 450m, 1500, "Adres", TestPhone.NewE164(),
            Email: "ornek@example.test", FullName: "Örnek Müşteri", City: "Ankara"));

        await fx.Svc.PullOnceAsync(CancellationToken.None);

        var c = fx.Customers.GetById(local)!;
        c.FullName.Should().BeNull();
        c.Phone.Should().BeNull();
        c.DisplayName.Should().Be("[Silindi]");
        c.TotalAmount.Should().Be(450m);
        c.IsBlacklisted.Should().BeTrue("silme talebi kara listeden çıkmanın yolu olmamalı");
        c.LastSeenAt.Should().Be(2000);
    }

    [Fact]
    public async Task Yerelde_hic_olmayan_silinen_kisi_icin_kart_acilmaz_ama_karar_kalir()
    {
        var id = Guid.NewGuid();
        using var fx = Build(after => after == 0
            ? Page(3, Item(id, "hicgormedigimiz", 3) with { PurgedAt = T1 })
            : Page(after));

        await fx.Svc.PullOnceAsync(CancellationToken.None);

        fx.Customers.GetById(id.ToString("N")).Should().BeNull();
        // Sonradan açılan satır (sohbet) mezar taşına takılır.
        var later = LocalRow(fx, "hicgormedigimiz");
        fx.Customers.GetById(later)!.DisplayName.Should().Be("[Silindi]");
    }

    [Fact]
    public async Task Gecici_kaydin_silinmesi_kimlige_yayilmaz_miras_satiri_donusur()
    {
        var squatter = Guid.NewGuid();
        using var fx = Build(after => after == 0
            ? Page(3, Item(squatter, "ornek", 3) with { PurgedAt = T1, CreatedByShopper = true })
            : Page(after));
        var real = LocalRow(fx, "Ornek");                        // gerçek müşteri, aynı kimlik, başka Id
        var phone = TestPhone.NewE164();
        fx.Customers.UpdatePhone(real, phone);
        // Eski ingest'in geçici Id'yle açtığı miras satırı (başka harf yazımıyla), damgasız beyan.
        using (var scope = SyncApplyScope.Begin(fx.Db))
        {
            scope.Execute(@"INSERT INTO Customer (Id, Platform, Username, IdentityKey, DisplayName, FirstSeenAt, LastSeenAt, Phone)
                            VALUES (@id, 'tiktok', 'ORNEK', 'ornek', 'Beyan Ad', 1, 1, @claim)",
                new { id = squatter.ToString("N"), claim = TestPhone.NewE164() });
            scope.Commit();
        }

        (await fx.Svc.PullOnceAsync(CancellationToken.None)).Should().Be(CustomerPullOutcome.CaughtUp);

        fx.Customers.GetById(real)!.Phone.Should().Be(phone, "sahiplenenin silmesi gerçek müşteriyi silmez");
        Exists(fx, squatter.ToString("N")).Should().BeFalse("miras satırı yeni yerel Id'ye dönüştü (U9)");
        using var conn = fx.Db.Open();
        conn.ExecuteScalar<int>("SELECT COUNT(*) FROM CustomerPurgeTombstone").Should().Be(0);
        conn.ExecuteScalar<string?>("SELECT Phone FROM Customer WHERE Username = 'ORNEK'").Should().BeNull(
            "silinmiş geçici satırın beyanı bilinmez → beyan olabilen damgasız birimler düşer");
        conn.ExecuteScalar<int>("SELECT COUNT(*) FROM Customer WHERE PurgedAt IS NOT NULL").Should().Be(0);
        var converted = conn.ExecuteScalar<string>("SELECT Id FROM Customer WHERE Username = 'ORNEK'")!;
        fx.Pushed.Last().Customers.Should().Contain(i => i.Id == Guid.ParseExact(converted, "N"),
            "U2: dönüştürülen satırın yeni Id'si hemen gider — sunucu devralır (S8)");
    }

    [Fact]
    public async Task Kopya_satiri_once_yonlendirmedir_PurgedAt_tasisa_bile_mezar_tasi_sayilmaz()
    {
        // Sunucu kopyayı PurgedAt'siz gönderiyor (S12); bu, eski/bozuk bir yanıta karşı savunma.
        var copy = Guid.NewGuid();
        var canonical = Guid.NewGuid();
        using var fx = Build(after => after == 0
            ? Page(4, Alias(copy, "ornek", canonical, 4) with { PurgedAt = T1 })
            : Page(after));
        var local = LocalRow(fx, "ornek", copy);

        await fx.Svc.PullOnceAsync(CancellationToken.None);

        fx.Customers.GetById(local)!.DisplayName.Should().Be("takma");
        using var conn = fx.Db.Open();
        conn.ExecuteScalar<int>("SELECT COUNT(*) FROM CustomerPurgeTombstone").Should().Be(0);
    }

    // ── yönlendirme, kimlik sahipleri, durma (U4, U5) ───────────────────

    [Fact]
    public async Task Gonderilmemis_kimlik_sahibi_once_gonderilir_sonra_asil_kayda_tasinir_ayni_turda()
    {
        var canonical = Guid.NewGuid();
        var holder = Guid.NewGuid();
        var pushes = 0;
        using var fx = Build(
            after => after == 0
                ? Page(7, Item(canonical, "ornek", 7) with { FullName = "Örnek Müşteri", FullNameChangedAt = T1 })
                : Page(after),
            // Turun başındaki gönderim düşer → sahip gönderilmemiş kalır → akış durur (U5), gönderim
            // yeniden koşar, akış aynı turda sürer.
            sync: () => Interlocked.Increment(ref pushes) == 1
                ? throw new HttpRequestException("ağ yok")
                : $$"""{"synced":1,"retroactiveMatches":0,"redirects":[{"id":"{{holder}}","canonicalId":"{{canonical}}"}]}""");
        var local = LocalRow(fx, "Ornek", holder);
        using (var conn = fx.Db.Open())
        {
            conn.Execute("INSERT INTO StreamSession (Id, StartedAt) VALUES ('s1', 1)");
            conn.Execute(@"INSERT INTO Label (Id, SessionId, CustomerId, Platform, Username, MessageText, Price, AddedAt)
                           VALUES ('l1', 's1', @local, 'tiktok', 'Ornek', 'A1', 10, 1)", new { local });
        }

        (await fx.Svc.PullOnceAsync(CancellationToken.None)).Should().Be(CustomerPullOutcome.CaughtUp);

        pushes.Should().BeGreaterThanOrEqualTo(2, "durma: sahip önce gönderildi");
        Exists(fx, local).Should().BeFalse();
        var c = fx.Customers.GetById(canonical.ToString("N"))!;
        c.FullName.Should().Be("Örnek Müşteri");
        c.TotalAmount.Should().Be(450m);
        using var q = fx.Db.Open();
        q.ExecuteScalar<string>("SELECT CustomerId FROM Label WHERE Id = 'l1'").Should().Be(canonical.ToString("N"));
        fx.FeedCursor.Should().Be(7);
        fx.Tracker.IsInitialCatchUpDone.Should().BeTrue();
    }

    [Fact]
    public async Task Durma_sonrasi_gonderim_sahibi_goturemezse_tur_biter_akis_yeniden_istenmez()
    {
        // C6: gönderim HTTP hatasında fırlatmaz, imleci ilerletmeden döner. Durmadan sonraki
        // gönderim filigranı ilerletmediyse ikinci deneme aynı öğede yine durur — istek harcamadan
        // tur biter, imleç o öğede kalır.
        var canonical = Guid.NewGuid();
        using var fx = Build(
            after => FakeHttpMessageHandler.Json(200, after == 0 ? Page(7, Item(canonical, "ornek", 7)) : Page(after)),
            sync: () => FakeHttpMessageHandler.Empty(503));
        var holder = LocalRow(fx, "Ornek");

        (await fx.Svc.PullOnceAsync(CancellationToken.None)).Should().Be(CustomerPullOutcome.Stalled);

        fx.Pulls.Should().Be(1, "gönderim sahibi götüremedi — akış aynı turda yeniden istenmez");
        fx.Posts.Should().Be(2, "turun başı + durma sonrası");
        fx.FeedCursor.Should().Be(0);
        Exists(fx, holder).Should().BeTrue("gönderilmemiş sahip taşınmaz (U5)");
        Exists(fx, canonical.ToString("N")).Should().BeFalse();
        fx.Tracker.IsInitialCatchUpDone.Should().BeFalse("durma yetişme sayılmaz");
    }

    [Fact]
    public async Task Kopya_satiri_hedefinden_once_gelirse_hedef_gelince_tasinir()
    {
        var copy = Guid.NewGuid();
        var canonical = Guid.NewGuid();
        using var fx = Build(after => after == 0
            ? Page(8, Alias(copy, "ornek", canonical, 4), Item(canonical, "Ornek", 8) with { Notes = "sunucu notu" })
            : Page(after));
        var local = LocalRow(fx, "ornek", copy);
        await fx.Push.SyncOnceAsync(CancellationToken.None);   // kopya daha önce gönderilmiş (S7)

        await fx.Svc.PullOnceAsync(CancellationToken.None);

        Exists(fx, local).Should().BeFalse();
        fx.Customers.GetById(canonical.ToString("N"))!.Notes.Should().Be("sunucu notu");
    }

    [Fact]
    public async Task Gecici_asil_kayit_yerelde_acilmaz_yerel_kimlik_sahibine_tasinmaz()
    {
        var squatter = Guid.NewGuid();
        var lone = Guid.NewGuid();
        using var fx = Build(after => after == 0
            ? Page(6, Item(squatter, "ORNEK", 5) with { Phone = TestPhone.NewE164(), CreatedByShopper = true },
                      Item(lone, "baskasi", 6) with { FullName = "Beyan Ad", CreatedByShopper = true })
            : Page(after));
        var real = LocalRow(fx, "ornek");

        (await fx.Svc.PullOnceAsync(CancellationToken.None)).Should().Be(CustomerPullOutcome.CaughtUp);

        Exists(fx, real).Should().BeTrue("kural 7: gerçek müşteri sahiplenenin kaydına taşınmaz");
        Exists(fx, squatter.ToString("N")).Should().BeFalse();
        Exists(fx, lone.ToString("N")).Should().BeFalse("yerelde kimlik sahibi olmasa da geçici satır açılmaz");
        fx.FeedCursor.Should().Be(6);
    }

    // ── tur düzeni, zehirli öğe, meşgul müşteri, kilit artığı ───────────

    [Fact]
    public async Task Her_tur_cekmeden_once_gonderir()
    {
        using var fx = Build(after => Page(after));
        LocalRow(fx, "ornek");

        await fx.Svc.PullOnceAsync(CancellationToken.None);

        var calls = fx.Http.Requests.Select(r => r.RequestUri!.AbsolutePath)
            .Where(p => !p.EndsWith("/me/licenses", StringComparison.Ordinal)).ToList();
        calls.First().Should().EndWith("/wpf-customers/sync", "gönderim çekmeden önce koşar");
        calls.Should().Contain(p => p.EndsWith("/wpf-customers/changes", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Gonderimin_yerel_hatasi_turu_basarisiz_sayar_akisi_durdurmaz()
    {
        // C6 incelemesi: gönderim HTTP hatasında fırlatmaz ama yerel SQLite hatası (GetForPush)
        // çıkabilir. Hata turu başarısız sayar; akış yine uygulanır (KVKK silmeleri bir gönderim
        // hatasının arkasında beklemez) ve yetişme kaydı akışın kendisine bakar.
        var id = Guid.NewGuid();
        using var fx = Build(after => after == 0 ? Page(4, Item(id, "ornek", 4)) : Page(after));
        // Bozuk yerel satır: INTEGER kolonda metin — gönderim okuması (eşleme) her seferinde düşer.
        using (var scope = SyncApplyScope.Begin(fx.Db))
        {
            scope.Execute(@"INSERT INTO Customer (Id, Platform, Username, IdentityKey, FirstSeenAt, LastSeenAt)
                            VALUES (@id, 'tiktok', 'bozuk', 'bozuk', 'x', 1)", new { id = Guid.NewGuid().ToString("N") });
            scope.Commit();
        }

        (await fx.Svc.PullOnceAsync(CancellationToken.None)).Should().Be(CustomerPullOutcome.Failed);

        fx.Customers.GetById(id.ToString("N")).Should().NotBeNull("akış gönderim hatasına rağmen uygulandı");
        fx.FeedCursor.Should().Be(4);
        fx.Tracker.IsInitialCatchUpDone.Should().BeTrue("akış boş sayfaya kadar uygulandı");
        fx.Posts.Should().Be(0);
    }

    [Fact]
    public async Task Zehirli_oge_bes_turda_atlanir_kayit_kalir_sonraki_degisiklikte_silinir()
    {
        var poison = Guid.NewGuid();
        var ok = Guid.NewGuid();
        var phase = 1;
        using var fx = Build(after => (phase, after) switch
        {
            (1, 0) => Page(6, Item(poison, "zehir", 5), Item(ok, "saglam", 6)),
            (2, 6) => Page(9, Item(poison, "zehir", 9) with { Notes = "düzeldi" }),
            _ => Page(after),
        });
        using (var c = fx.Db.Open())
            c.Execute("CREATE TRIGGER zehir BEFORE INSERT ON Customer WHEN new.Username = 'zehir' BEGIN SELECT RAISE(ABORT, 'zehir'); END");

        for (var tour = 1; tour < CustomerChangesPullService.MaxAttemptsBeforeSkip; tour++)
        {
            (await fx.Svc.PullOnceAsync(CancellationToken.None)).Should().Be(CustomerPullOutcome.Failed, $"tur {tour}");
            fx.FeedCursor.Should().Be(0, "atlamak sessiz veri kaybı olurdu — önce yeniden denenir");
        }
        (await fx.Svc.PullOnceAsync(CancellationToken.None)).Should().Be(CustomerPullOutcome.CaughtUp);

        Exists(fx, ok.ToString("N")).Should().BeTrue("zehirli öğe arkasındakileri sonsuza dek bekletmez");
        fx.FeedCursor.Should().Be(6);
        using (var c = fx.Db.Open())
            c.ExecuteScalar<long?>("SELECT SkippedAt FROM CustomerFeedFailure WHERE ItemId = @id", new { id = poison.ToString("N") })
                .Should().NotBeNull("kayıt kalır → durum satırında kalıcı uyarı (D2)");

        using (var c = fx.Db.Open()) c.Execute("DROP TRIGGER zehir");
        phase = 2;
        await fx.Svc.PullOnceAsync(CancellationToken.None);

        fx.Customers.GetById(poison.ToString("N"))!.Notes.Should().Be("düzeldi");
        using var conn = fx.Db.Open();
        conn.ExecuteScalar<int>("SELECT COUNT(*) FROM CustomerFeedFailure").Should().Be(0,
            "aynı satırın sonraki değişikliği uygulandı — uyarı kalkar");
    }

    [Theory]
    [InlineData(5)]   // SQLITE_BUSY
    [InlineData(6)]   // SQLITE_LOCKED
    public async Task Kilit_cekismesi_deneme_sayilmaz_tur_basarisiz_sonraki_tur_ayni_ogeden(int sqliteErrorCode)
    {
        // U10: kilit çekişmesi geçicidir — zehirli öğe sayacına yazılmaz (yoksa yoğun bir yayında beş
        // çekişme sağlam bir öğeyi atlatırdı).
        var id = Guid.NewGuid();
        Fixture? fixture = null;
        var contended = true;
        using var fx = fixture = Build(after =>
        {
            if (after == 0 && contended)
                // Sayfa yanıtından sonraki ilk depo bağlantısı = öğenin uygulama işlemi.
                fixture!.Faults.FailNextOpen(new SqliteException("database is locked", sqliteErrorCode));
            return FakeHttpMessageHandler.Json(200, after == 0 ? Page(4, Item(id, "ornek", 4)) : Page(after));
        });

        (await fx.Svc.PullOnceAsync(CancellationToken.None)).Should().Be(CustomerPullOutcome.Failed);

        fx.FeedCursor.Should().Be(0);
        Exists(fx, id.ToString("N")).Should().BeFalse();
        using (var c = fx.Db.Open())
            c.ExecuteScalar<int>("SELECT COUNT(*) FROM CustomerFeedFailure").Should().Be(0, "çekişme deneme sayılmaz");

        contended = false;
        (await fx.Svc.PullOnceAsync(CancellationToken.None)).Should().Be(CustomerPullOutcome.CaughtUp);
        Exists(fx, id.ToString("N")).Should().BeTrue();
        fx.FeedCursor.Should().Be(4);
    }

    [Fact]
    public async Task Odeme_akisindaki_musteri_akisi_o_ogede_birakir_sonraki_turda_surer()
    {
        var canonical = Guid.NewGuid();
        var busy = new CustomerBusySet();
        using var fx = Build(after => after == 0 ? Page(7, Item(canonical, "ornek", 7)) : Page(after), busy: busy);
        var holder = LocalRow(fx, "Ornek");
        var lease = await busy.EnterAsync(holder);

        (await fx.Svc.PullOnceAsync(CancellationToken.None)).Should().Be(CustomerPullOutcome.Busy);
        fx.FeedCursor.Should().Be(0);
        Exists(fx, holder).Should().BeTrue("ödeme akışı süren müşteri taşınmaz (U13)");
        fx.Tracker.IsInitialCatchUpDone.Should().BeFalse();
        fx.Pulls.Should().Be(1, "meşgul müşteri aynı turda yeniden denenmez");

        lease.Dispose();
        (await fx.Svc.PullOnceAsync(CancellationToken.None)).Should().Be(CustomerPullOutcome.CaughtUp);
        Exists(fx, holder).Should().BeFalse();
        fx.FeedCursor.Should().Be(7);
    }

    [Fact]
    public async Task Kalmis_kilit_satiri_her_turda_gonderimden_once_temizlenir()
    {
        // U15: kalmış kilit satırı bütün damgalamayı ve gönderimi sessizce kapatır; turun başındaki
        // gönderimin taşımaları da (SyncApplyScope ikinci kilit satırına çarpar) düşerdi.
        var copy = Guid.NewGuid();
        var canonical = Guid.NewGuid();
        using var fx = Build(after => Page(after),
            sync: () => $$"""{"synced":1,"retroactiveMatches":0,"redirects":[{"id":"{{copy}}","canonicalId":"{{canonical}}"}]}""");
        LocalRow(fx, "Ornek", canonical);
        var local = LocalRow(fx, "ornek", copy);
        using (var c = fx.Db.Open()) c.Execute("INSERT INTO SyncApplyGuard (Id) VALUES (1)");

        await fx.Svc.PullOnceAsync(CancellationToken.None);

        using var conn = fx.Db.Open();
        conn.ExecuteScalar<int>("SELECT COUNT(*) FROM SyncApplyGuard").Should().Be(0);
        Exists(fx, local).Should().BeFalse("gönderimin taşıması kilit artığına çarpmadı");
    }

    // ── imleç sıfırlama ─────────────────────────────────────────────────

    [Fact]
    public async Task CursorReset_akis_imlecini_sifirlar_gonderimi_geri_sarar_ve_devam_eder()
    {
        var a = Guid.NewGuid();
        using var fx = Build(after => after switch
        {
            999 => ResetPage(5, Item(a, "ornek_a", 5)),      // sunucu yedekten döndü
            _ => Page(after),
        });
        var earlier = LocalRow(fx, "daha_once_gonderilen");
        fx.Cursors.Upsert(CustomerChangesPullService.CursorName, Lisans, seq: 999);
        fx.Cursors.Upsert(WpfCustomerProjectionSyncService.CursorName, Lisans, seq: MaxSyncSeq(fx));

        (await fx.Svc.PullOnceAsync(CancellationToken.None)).Should().Be(CustomerPullOutcome.CaughtUp);

        fx.Customers.GetById(a.ToString("N")).Should().NotBeNull("sıfırlanan sayfa da uygulanır");
        fx.FeedCursor.Should().Be(5);
        fx.Pushed.SelectMany(p => p.Customers).Should().Contain(i => i.Id == Guid.ParseExact(earlier, "N"),
            "yedekten dönen sunucuya önceden gönderilmiş satırlar da yeniden gider");
        fx.Push.Watermark(Lisans).Should().Be(MaxSyncSeq(fx));
    }
}
