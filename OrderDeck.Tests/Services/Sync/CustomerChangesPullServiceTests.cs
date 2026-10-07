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
using Microsoft.Extensions.Logging;
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

    /// <summary>Her okumada geri çağrı — arka plan işinin turlarını sinyalle izlemek için.</summary>
    private sealed class CallbackLicenseProvider(Func<string?> get) : ICurrentLicenseProvider
    {
        public string? CurrentLicenseKey => get();
    }

    private sealed class FixedClock : IClock
    {
        public long UnixNow() => 1_791_000_000L;
    }

    /// <summary>Kurulan hata (ya da geri çağrı), fabrikanın SONRAKİ ilk <c>Open</c>'ında bir kez
    /// fırlatılır/koşar (yalnız senkron deposunun fabrikası sarılır).</summary>
    private sealed class FaultyFactory(IDbConnectionFactory inner) : IDbConnectionFactory
    {
        private Exception? _next;
        private Action? _onNext;

        public void FailNextOpen(Exception ex) => _next = ex;

        public void OnNextOpen(Action action) => _onNext = action;

        public System.Data.IDbConnection Open()
        {
            Interlocked.Exchange(ref _onNext, null)?.Invoke();
            var ex = Interlocked.Exchange(ref _next, null);
            if (ex is not null) throw ex;
            return inner.Open();
        }
    }

    /// <summary>Günlük satırları: düzey, biçimlenmiş metin, istisna (yığın izi var mı).</summary>
    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (Entries) Entries.Add((logLevel, formatter(state, exception), exception));
        }
    }

    private static readonly Guid LicenseId = Guid.NewGuid();
    private static readonly string Lisans = $"lisans-{Guid.NewGuid():N}";
    private static readonly Guid LicenseId2 = Guid.NewGuid();
    private static readonly string Lisans2 = $"lisans-{Guid.NewGuid():N}";
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static readonly DateTimeOffset T1 = new(2026, 10, 1, 10, 0, 0, TimeSpan.Zero);

    private sealed record Fixture(
        CustomerChangesPullService Svc, WpfCustomerProjectionSyncService Push,
        CustomerRepository Customers, SyncCursorRepository Cursors, SyncStatusTracker Tracker,
        InMemorySqlite Db, FakeHttpMessageHandler Http, List<WpfCustomerSyncRequest> Pushed,
        FaultyFactory Faults, FakeLicenseProvider License, RecordingLogger<CustomerChangesPullService> Log) : IDisposable
    {
        public long FeedCursor => Cursors.Get(CustomerChangesPullService.CursorName, Lisans)?.Seq ?? 0L;
        public int Posts => Http.Requests.Count(r => r.RequestUri!.AbsolutePath.EndsWith("/wpf-customers/sync"));
        public int Pulls => Http.Requests.Count(r => r.RequestUri!.AbsolutePath.EndsWith("/wpf-customers/changes"));

        public int Count(string sql, object? p = null)
        {
            using var c = Db.Open();
            return c.ExecuteScalar<int>(sql, p);
        }

        public void Dispose() => Db.Dispose();
    }

    /// <param name="changes">(lisans Id'si, afterSeq) → sayfa yanıtı.</param>
    /// <param name="sync">Gönderim yanıtı (varsayılan: yönlendirmesiz).</param>
    /// <param name="licenseProvider">Verilirse servisler bunu okur (<see cref="Fixture.License"/> kullanılmaz).</param>
    private static Fixture Build(Func<Guid, long, HttpResponseMessage> changes, Func<HttpResponseMessage>? sync = null,
        bool license = true, CustomerBusySet? busy = null, ICurrentLicenseProvider? licenseProvider = null)
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
                return FakeHttpMessageHandler.Json(200,
                    $"[{{\"id\":\"{LicenseId}\",\"licenseKey\":\"{Lisans}\"}},{{\"id\":\"{LicenseId2}\",\"licenseKey\":\"{Lisans2}\"}}]");
            if (path.EndsWith("/wpf-customers/changes"))
                return changes(LicenseOf(req), AfterSeq(req));
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
        var fake = new FakeLicenseProvider { CurrentLicenseKey = license ? Lisans : null };
        var lic = licenseProvider ?? fake;
        var clock = new FixedClock();
        var push = new WpfCustomerProjectionSyncService(api, syncRepo, cursors, lic, clock,
            NullLogger<WpfCustomerProjectionSyncService>.Instance);
        var tracker = new SyncStatusTracker();
        var log = new RecordingLogger<CustomerChangesPullService>();
        var svc = new CustomerChangesPullService(api, customers, syncRepo, cursors, push, lic, clock, tracker, log);
        return new Fixture(svc, push, customers, cursors, tracker, db, http, pushed, faults, fake, log);
    }

    private static Fixture Build(Func<long, HttpResponseMessage> changes, Func<HttpResponseMessage>? sync = null,
        bool license = true, CustomerBusySet? busy = null, ICurrentLicenseProvider? licenseProvider = null)
        => Build((_, after) => changes(after), sync, license, busy, licenseProvider);

    private static Fixture Build(Func<long, string> changes, Func<string>? sync = null, bool license = true,
        CustomerBusySet? busy = null, ICurrentLicenseProvider? licenseProvider = null)
        => Build((_, after) => FakeHttpMessageHandler.Json(200, changes(after)),
            sync is null ? null : () => FakeHttpMessageHandler.Json(200, sync()),
            license, busy, licenseProvider);

    /// <summary><c>/api/v1/licenses/{id}/wpf-customers/changes</c></summary>
    private static Guid LicenseOf(HttpRequestMessage r) => Guid.Parse(r.RequestUri!.AbsolutePath.Split('/')[4]);

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
    [InlineData(5)]    // SQLITE_BUSY
    [InlineData(6)]    // SQLITE_LOCKED
    [InlineData(8)]    // SQLITE_READONLY
    [InlineData(10)]   // SQLITE_IOERR
    [InlineData(13)]   // SQLITE_FULL
    [InlineData(14)]   // SQLITE_CANTOPEN
    [InlineData(266)]  // SQLITE_IOERR_READ (genişletilmiş kod — birincil kod 10)
    public async Task Kilit_cekismesi_ve_ortam_hatalari_deneme_sayilmaz_tur_basarisiz_sonraki_tur_ayni_ogeden(int sqliteErrorCode)
    {
        // U10 + C7 incelemesi M-1: kilit çekişmesi ve yerel ortam hataları öğeye özgü değildir —
        // zehirli öğe sayacına yazılmaz (yoksa yoğun bir yayında beş çekişme ya da dolu bir disk
        // sağlam bir öğeyi atlatırdı).
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

    [Fact]
    public async Task CursorReset_daha_once_gonderilmis_kimlik_sahibiyle_tek_turda_yakinsar()
    {
        // Geri sarılan gönderim filigranı (0) sahibi "gönderilmemiş" yapar → durma → gönderim sahibi
        // yeniden götürür → akış aynı turda baştan sürer ve taşır.
        var canonical = Guid.NewGuid();
        using var fx = Build(after => after switch
        {
            999 => ResetPage(5, Item(canonical, "ornek", 5)),
            0 => Page(5, Item(canonical, "ornek", 5)),
            _ => Page(after),
        });
        var holder = LocalRow(fx, "Ornek");
        await fx.Push.SyncOnceAsync(CancellationToken.None);   // sahip daha önce gönderilmiş
        fx.Cursors.Upsert(CustomerChangesPullService.CursorName, Lisans, seq: 999);

        (await fx.Svc.PullOnceAsync(CancellationToken.None)).Should().Be(CustomerPullOutcome.CaughtUp);

        Exists(fx, holder).Should().BeFalse();
        Exists(fx, canonical.ToString("N")).Should().BeTrue();
        fx.FeedCursor.Should().Be(5);
    }

    [Fact]
    public async Task Ertelenen_kopya_satiri_imleci_gecer_sonraki_gonderim_yonlendirmeyi_getirir()
    {
        // U5: akıştaki kopya satırının yerel kopyası gönderilmemişse durma gerekmez — o satırın
        // gönderimi yönlendirmeyi zaten döndürür (S7).
        var copy = Guid.NewGuid();
        var canonical = Guid.NewGuid();
        var posts = 0;
        using var fx = Build(
            after => FakeHttpMessageHandler.Json(200, after == 0 ? Page(4, Alias(copy, "ornek", canonical, 4)) : Page(after)),
            sync: () => Interlocked.Increment(ref posts) == 1
                ? FakeHttpMessageHandler.Empty(503)                   // turun başındaki gönderim düşer
                : FakeHttpMessageHandler.Json(200,
                    $$"""{"synced":2,"retroactiveMatches":0,"redirects":[{"id":"{{copy}}","canonicalId":"{{canonical}}"}]}"""));
        LocalRow(fx, "Ornek", canonical);
        var local = LocalRow(fx, "ornek", copy);

        (await fx.Svc.PullOnceAsync(CancellationToken.None)).Should().Be(CustomerPullOutcome.CaughtUp);
        fx.FeedCursor.Should().Be(4, "ertelenen kopya satırı akışı durdurmaz");
        Exists(fx, local).Should().BeTrue();

        await fx.Push.SyncOnceAsync(CancellationToken.None);

        Exists(fx, local).Should().BeFalse("gönderimin yanıtındaki yönlendirme taşıdı");
        Exists(fx, canonical.ToString("N")).Should().BeTrue();
    }

    // ── lisans değişimi (M-3, M-10) ─────────────────────────────────────

    [Fact]
    public async Task Lisans_degisince_yeni_lisansin_akisi_bastan_baslar()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        using var fx = Build((license, after) => FakeHttpMessageHandler.Json(200, (license == LicenseId, after) switch
        {
            (true, 0) => Page(6, Item(a, "ornek_a", 6)),
            (false, 0) => Page(3, Item(b, "ornek_b", 3)),
            _ => Page(after),
        }));
        (await fx.Svc.PullOnceAsync(CancellationToken.None)).Should().Be(CustomerPullOutcome.CaughtUp);

        fx.License.CurrentLicenseKey = Lisans2;
        (await fx.Svc.PullOnceAsync(CancellationToken.None)).Should().Be(CustomerPullOutcome.CaughtUp);

        AfterSeq(fx.Http.Requests.First(r => r.RequestUri!.AbsolutePath.EndsWith("/wpf-customers/changes") && LicenseOf(r) == LicenseId2))
            .Should().Be(0, "imleç lisansa bağlı — önceki lisansın imleci kullanılmaz");
        fx.Customers.GetById(b.ToString("N")).Should().NotBeNull();
        fx.Cursors.Get(CustomerChangesPullService.CursorName, Lisans2)!.Seq.Should().Be(3);
        fx.FeedCursor.Should().Be(6, "önceki lisansın imleci yerinde kalır");
    }

    [Fact]
    public async Task Lisans_degisince_onceki_lisansin_akis_hata_kayitlari_ve_takilma_durumu_silinir()
    {
        var poison = Guid.NewGuid();
        using var fx = Build((license, after) => FakeHttpMessageHandler.Json(200,
            license == LicenseId && after == 0 ? Page(5, Item(poison, "zehir", 5)) : Page(after)));
        using (var c = fx.Db.Open())
            c.Execute("CREATE TRIGGER zehir BEFORE INSERT ON Customer WHEN new.Username = 'zehir' BEGIN SELECT RAISE(ABORT, 'zehir'); END");

        (await fx.Svc.PullOnceAsync(CancellationToken.None)).Should().Be(CustomerPullOutcome.Failed);
        fx.Count("SELECT COUNT(*) FROM CustomerFeedFailure").Should().Be(1);

        fx.License.CurrentLicenseKey = Lisans2;
        (await fx.Svc.PullOnceAsync(CancellationToken.None)).Should().Be(CustomerPullOutcome.CaughtUp);

        fx.Count("SELECT COUNT(*) FROM CustomerFeedFailure").Should().Be(0,
            "kayıtlar lisansa bağlı değil — önceki lisansın öğesi yeni lisansın durum satırında uyarı olarak kalmaz");
        fx.Tracker.BlockedOn.Should().BeNull();
    }

    [Fact]
    public async Task Lisans_degisince_yetisme_durumu_sifirlanir_yeni_lisans_kendi_akisini_bekler()
    {
        // Form oynatmasının işareti lisansa bağlı, yetişme süreç içi: yeni lisansın oynatması ÖNCEKİ
        // lisansın yetişmesiyle başlamamalı (C10 incelemesi).
        using var fx = Build((license, after) => license == LicenseId
            ? FakeHttpMessageHandler.Json(200, Page(after))
            : FakeHttpMessageHandler.Json(500, "{}"));
        (await fx.Svc.PullOnceAsync(CancellationToken.None)).Should().Be(CustomerPullOutcome.CaughtUp);
        fx.Tracker.IsInitialCatchUpDoneFor(Lisans).Should().BeTrue();

        fx.License.CurrentLicenseKey = Lisans2;
        (await fx.Svc.PullOnceAsync(CancellationToken.None)).Should().Be(CustomerPullOutcome.Failed);

        fx.Tracker.IsInitialCatchUpDone.Should().BeFalse("yeni lisansın akışı henüz yetişmedi");
        fx.Tracker.LastPullOkAt.Should().BeNull();
        fx.Tracker.IsInitialCatchUpDoneFor(Lisans2).Should().BeFalse();
    }

    // ── takılan öğe: KVKK silmeleri beklemez, uzun takılma görünür (I-1) ─

    [Fact]
    public async Task Takilan_sayfanin_ilerisindeki_KVKK_silmeleri_yine_uygulanir_imlec_ilerlemez()
    {
        var canonical = Guid.NewGuid();
        var other = Guid.NewGuid();
        var purged = Guid.NewGuid();
        var busy = new CustomerBusySet();
        using var fx = Build(after => after == 0
            ? Page(9, Item(canonical, "ornek", 5), Item(other, "baska", 7), Item(purged, "silinecek", 9) with { PurgedAt = T1 })
            : Page(after), busy: busy);
        var holder = LocalRow(fx, "Ornek");
        var victim = LocalRow(fx, "silinecek");
        using var lease = await busy.EnterAsync(holder);

        (await fx.Svc.PullOnceAsync(CancellationToken.None)).Should().Be(CustomerPullOutcome.Busy);

        fx.Customers.GetById(victim)!.DisplayName.Should().Be("[Silindi]",
            "ilgisiz kişinin KVKK silmesi takılan öğeyi beklemez");
        Exists(fx, other.ToString("N")).Should().BeFalse("silme dışındaki öğeler sıralarını bekler");
        fx.FeedCursor.Should().Be(0, "imleç takılan öğede kalır");
        fx.Log.Entries.Should().ContainSingle(e => e.Message.Contains("KVKK silme: 1"));
    }

    [Fact]
    public async Task Erken_uygulanan_silme_takilan_eski_kayitla_geri_acilmaz()
    {
        // Silme işlemlerinin sıradan bağımsızlığı: k'de X kimliğinin asıl kaydı (takılı), k+2'de aynı
        // kimliğe düşen bir silme. Silme erken uygulanır; takılma çözülünce k'deki kayıt sonradan
        // uygulanır — kişiyi geri açmamalı. Sunucu bu sayfayı üretemez (filtreli tekil indeks bir
        // kimliğe tek asıl kayıt bırakır, PR-2); test yalnız erken silmenin sonradan gelen yazımla
        // bozulmadığını sınar.
        var canonical = Guid.NewGuid();
        var other = Guid.NewGuid();
        var purgedRecord = Guid.NewGuid();
        var phone = TestPhone.NewE164();
        var busy = new CustomerBusySet();
        using var fx = Build(after => after == 0
            ? Page(9,
                Item(canonical, "ornek", 5) with { FullName = "Örnek Müşteri", FullNameChangedAt = T1, Phone = phone, PhoneChangedAt = T1 },
                Item(other, "baska", 7),
                Item(purgedRecord, "ORNEK", 9) with { PurgedAt = T1 })
            : Page(after), busy: busy);
        var holder = LocalRow(fx, "Ornek");
        var lease = await busy.EnterAsync(holder);

        (await fx.Svc.PullOnceAsync(CancellationToken.None)).Should().Be(CustomerPullOutcome.Busy);
        fx.Customers.GetById(holder)!.DisplayName.Should().Be("[Silindi]", "silme takılmayı beklemeden uygulandı");
        lease.Dispose();
        (await fx.Svc.PullOnceAsync(CancellationToken.None)).Should().Be(CustomerPullOutcome.CaughtUp);

        using var conn = fx.Db.Open();
        var row = conn.QuerySingle<(string? FullName, string? Phone, string? DisplayName, long? PurgedAt)>(
            "SELECT FullName, Phone, DisplayName, PurgedAt FROM Customer WHERE Id = @id", new { id = canonical.ToString("N") });
        row.PurgedAt.Should().NotBeNull("mezar taşı sonradan eklenen asıl kaydı da boş doğurur");
        row.FullName.Should().BeNull();
        row.Phone.Should().BeNull();
        row.DisplayName.Should().Be("[Silindi]");
        fx.FeedCursor.Should().Be(9);
    }

    [Fact]
    public async Task Uzun_sure_takilan_oge_esikte_bir_kez_uyarilir_izleyicide_gorunur_uygulaninca_kalkar()
    {
        var canonical = Guid.NewGuid();
        var busy = new CustomerBusySet();
        using var fx = Build(after => after == 0 ? Page(7, Item(canonical, "ornek", 7)) : Page(after), busy: busy);
        var holder = LocalRow(fx, "Ornek");
        var lease = await busy.EnterAsync(holder);
        var id = canonical.ToString("N");
        var threshold = CustomerChangesPullService.BlockedRoundsBeforeWarning;
        int Warnings() => fx.Log.Entries.Count(e => e.Level == LogLevel.Warning && e.Message.Contains(id));

        for (var round = 1; round < threshold; round++)
        {
            (await fx.Svc.PullOnceAsync(CancellationToken.None)).Should().Be(CustomerPullOutcome.Busy);
            fx.Tracker.BlockedOn.Should().BeNull($"tur {round}: eşiğin altında");
        }
        fx.Log.Entries.Should().NotContain(e => e.Level >= LogLevel.Information, "eşiğin altındaki takılma sessiz (Debug)");

        (await fx.Svc.PullOnceAsync(CancellationToken.None)).Should().Be(CustomerPullOutcome.Busy);
        Warnings().Should().Be(1);
        fx.Tracker.BlockedOn.Should().NotBeNull();
        fx.Tracker.BlockedOn!.ItemId.Should().Be(id);
        fx.Tracker.BlockedOn.Reason.Should().Be(SyncBlockReason.Busy);
        fx.Tracker.BlockedOn.Since.Should().BeOnOrBefore(DateTimeOffset.UtcNow);

        (await fx.Svc.PullOnceAsync(CancellationToken.None)).Should().Be(CustomerPullOutcome.Busy);
        Warnings().Should().Be(1, "tek uyarı — her turda yinelenmez");

        lease.Dispose();
        (await fx.Svc.PullOnceAsync(CancellationToken.None)).Should().Be(CustomerPullOutcome.CaughtUp);
        fx.Tracker.BlockedOn.Should().BeNull("öğe uygulandı");
    }

    // ── öğeye özgü olmayan hatalar (M-1), günlük (M-2, M-4, M-8) ────────

    [Fact]
    public async Task Tur_ortasinda_sizan_kilit_satiri_deneme_sayilmaz_temizlenip_oge_uygulanir()
    {
        var a = Guid.NewGuid();
        Fixture? fixture = null;
        var leaked = false;
        using var fx = fixture = Build(after =>
        {
            if (!leaked)
            {
                // Başka bir yolun tur başındaki temizlikten SONRA sızdırdığı kilit satırı.
                leaked = true;
                using var c = fixture!.Db.Open();
                c.Execute("INSERT INTO SyncApplyGuard (Id) VALUES (1)");
            }
            return after == 0 ? Page(4, Item(a, "ornek", 4)) : Page(after);
        });

        (await fx.Svc.PullOnceAsync(CancellationToken.None)).Should().Be(CustomerPullOutcome.CaughtUp);

        Exists(fx, a.ToString("N")).Should().BeTrue();
        fx.Count("SELECT COUNT(*) FROM CustomerFeedFailure").Should().Be(0, "öğeye özgü değil — deneme sayılmaz");
        fx.Count("SELECT COUNT(*) FROM SyncApplyGuard").Should().Be(0);
        fx.Log.Entries.Should().Contain(e => e.Level == LogLevel.Error && e.Message.Contains("SyncApplyGuard"));
    }

    [Fact]
    public async Task Kalmis_satiri_bulunamayan_kilit_cakismasi_silindi_demez_oge_yeniden_denenir()
    {
        // N-3: genişletilmiş kod (SQLITE_CONSTRAINT_PRIMARYKEY 1555) da tanınır; temizlik 0 satır
        // sildiyse günlük "silindi" demez.
        var a = Guid.NewGuid();
        Fixture? fixture = null;
        var injected = false;
        using var fx = fixture = Build(after =>
        {
            if (after == 0 && !injected)
            {
                injected = true;
                fixture!.Faults.FailNextOpen(new SqliteException("UNIQUE constraint failed: SyncApplyGuard.Id", 1555));
            }
            return FakeHttpMessageHandler.Json(200, after == 0 ? Page(4, Item(a, "ornek", 4)) : Page(after));
        });

        (await fx.Svc.PullOnceAsync(CancellationToken.None)).Should().Be(CustomerPullOutcome.CaughtUp);

        Exists(fx, a.ToString("N")).Should().BeTrue();
        fx.Count("SELECT COUNT(*) FROM CustomerFeedFailure").Should().Be(0);
        var error = fx.Log.Entries.Single(e => e.Level == LogLevel.Error);
        error.Message.Should().Contain("bulunamadı").And.NotContain("silindi");
    }

    [Fact]
    public async Task Ogeye_ozgu_bozulma_hatasi_deneme_sayilir_sinirda_atlanir_arkasi_uygulanir()
    {
        // N-1: SQLITE_CORRUPT çoğu zaman öğeye özgüdür (ör. FTS5 dış içerik dizini YALNIZ bu satır için
        // tutarsız → CORRUPT_VTAB 267). Geçici sayılsaydı akış kayıtsız, takılma durumu ve ileri silme
        // olmadan sonsuza dek dururdu.
        var broken = Guid.NewGuid();
        var other = Guid.NewGuid();
        var purged = Guid.NewGuid();
        Fixture? fixture = null;
        using var fx = fixture = Build(after =>
        {
            if (after == 0)
                fixture!.Faults.FailNextOpen(new SqliteException("database disk image is malformed", 267));
            return FakeHttpMessageHandler.Json(200, after == 0
                ? Page(8, Item(broken, "bozuk", 4), Item(other, "saglam", 6), Item(purged, "silinecek", 8) with { PurgedAt = T1 })
                : Page(after));
        });
        var victim = LocalRow(fx, "silinecek");

        for (var round = 1; round < CustomerChangesPullService.MaxAttemptsBeforeSkip; round++)
        {
            (await fx.Svc.PullOnceAsync(CancellationToken.None)).Should().Be(CustomerPullOutcome.Failed, $"tur {round}");
            fx.FeedCursor.Should().Be(0);
        }
        fx.Count("SELECT Attempts FROM CustomerFeedFailure WHERE ItemId = @id", new { id = broken.ToString("N") })
            .Should().Be(CustomerChangesPullService.MaxAttemptsBeforeSkip - 1, "bozulma deneme sayılır (U10)");

        (await fx.Svc.PullOnceAsync(CancellationToken.None)).Should().Be(CustomerPullOutcome.CaughtUp);

        fx.Count("SELECT COUNT(*) FROM CustomerFeedFailure WHERE ItemId = @id AND SkippedAt IS NOT NULL",
            new { id = broken.ToString("N") }).Should().Be(1, "kayıt kalır → durum satırında kalıcı uyarı (D2)");
        Exists(fx, other.ToString("N")).Should().BeTrue();
        fx.Customers.GetById(victim)!.DisplayName.Should().Be("[Silindi]");
        fx.FeedCursor.Should().Be(8);
        fx.Log.Entries.Should().Contain(e => e.Level == LogLevel.Error
                                             && e.Message.Contains("integrity_check") && e.Message.Contains("'rebuild'"));
        fx.Log.Entries.Where(e => e.Level == LogLevel.Error && e.Message.Contains("SQLITE_CORRUPT"))
            .Select(e => e.Exception is not null).Should().Equal(new[] { true, false, false, false },
                "yığın izi yalnız ilk denemede (M-2)");
    }

    [Fact]
    public async Task Ortam_hatasi_serisinde_yigin_izi_bir_kez_basarili_turdan_sonra_yeniden()
    {
        // N-2: disk dolu gibi bir ortam hatası her 30 sn'de bir yığın izi yazmaz.
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var phase = 1;
        Fixture? fixture = null;
        using var fx = fixture = Build(after =>
        {
            if ((phase == 1 && after == 0) || (phase == 3 && after == 4))
                fixture!.Faults.FailNextOpen(new SqliteException("database or disk is full", 13));
            return FakeHttpMessageHandler.Json(200, (phase, after) switch
            {
                (1 or 2, 0) => Page(4, Item(a, "ornek_a", 4)),
                (3, 4) => Page(8, Item(b, "ornek_b", 8)),
                _ => Page(after),
            });
        });

        (await fx.Svc.PullOnceAsync(CancellationToken.None)).Should().Be(CustomerPullOutcome.Failed);
        (await fx.Svc.PullOnceAsync(CancellationToken.None)).Should().Be(CustomerPullOutcome.Failed);
        phase = 2;
        (await fx.Svc.PullOnceAsync(CancellationToken.None)).Should().Be(CustomerPullOutcome.CaughtUp);
        phase = 3;
        (await fx.Svc.PullOnceAsync(CancellationToken.None)).Should().Be(CustomerPullOutcome.Failed);

        fx.Log.Entries.Where(e => e.Level == LogLevel.Warning).Select(e => e.Exception is not null)
            .Should().Equal(new[] { true, false, true });
        fx.Count("SELECT COUNT(*) FROM CustomerFeedFailure").Should().Be(0);
    }

    [Fact]
    public async Task Kilit_yeniden_girisi_programlama_hatasidir_deneme_sayilmaz_bir_kez_hata_yazilir()
    {
        var a = Guid.NewGuid();
        Fixture? fixture = null;
        var inject = true;
        using var fx = fixture = Build(after =>
        {
            if (after == 0 && inject)
                fixture!.Faults.FailNextOpen(new CustomerBusySetReentrancyException("enjekte yeniden giriş"));
            return FakeHttpMessageHandler.Json(200, after == 0 ? Page(4, Item(a, "ornek", 4)) : Page(after));
        });

        (await fx.Svc.PullOnceAsync(CancellationToken.None)).Should().Be(CustomerPullOutcome.Failed);
        (await fx.Svc.PullOnceAsync(CancellationToken.None)).Should().Be(CustomerPullOutcome.Failed);

        fx.Count("SELECT COUNT(*) FROM CustomerFeedFailure").Should().Be(0);
        fx.Log.Entries.Count(e => e.Level == LogLevel.Error).Should().Be(1);

        inject = false;
        (await fx.Svc.PullOnceAsync(CancellationToken.None)).Should().Be(CustomerPullOutcome.CaughtUp);
        Exists(fx, a.ToString("N")).Should().BeTrue();
    }

    [Fact]
    public async Task Ayni_ogenin_tekrarlanan_hatasi_yigin_izi_olmadan_tek_satir()
    {
        var poison = Guid.NewGuid();
        using var fx = Build(after => after == 0 ? Page(5, Item(poison, "zehir", 5)) : Page(after));
        using (var c = fx.Db.Open())
            c.Execute("CREATE TRIGGER zehir BEFORE INSERT ON Customer WHEN new.Username = 'zehir' BEGIN SELECT RAISE(ABORT, 'zehir'); END");

        await fx.Svc.PullOnceAsync(CancellationToken.None);
        await fx.Svc.PullOnceAsync(CancellationToken.None);

        fx.Log.Entries.Where(e => e.Level == LogLevel.Warning && e.Message.Contains(poison.ToString("N")))
            .Select(e => e.Exception is not null)
            .Should().Equal(new[] { true, false }, "yığın izi yalnız (Id, ChangeSeq) çiftinin ilk hatasında");
    }

    [Fact]
    public async Task CursorReset_tekrarinda_uygulanmis_silmeler_yeniden_gunluge_yazilmaz()
    {
        var known = Guid.NewGuid();
        var unseen = Guid.NewGuid();
        WpfCustomerChangeItem[] Items() =>
        [
            Item(known, "silinen", 3) with { PurgedAt = T1 },
            Item(unseen, "hic_gorulmemis", 4) with { PurgedAt = T1 },
        ];
        using var fx = Build(after => after switch
        {
            0 => Page(4, Items()),
            999 => ResetPage(4, Items()),
            _ => Page(after),
        });
        LocalRow(fx, "silinen");

        await fx.Svc.PullOnceAsync(CancellationToken.None);
        fx.Log.Entries.Should().ContainSingle(e => e.Message.Contains("KVKK silme: 2"),
            "satır boşaltıldı + yerelde olmayan kişi için yeni mezar taşı");

        fx.Cursors.Upsert(CustomerChangesPullService.CursorName, Lisans, seq: 999);
        (await fx.Svc.PullOnceAsync(CancellationToken.None)).Should().Be(CustomerPullOutcome.CaughtUp);

        fx.Log.Entries.Count(e => e.Message.Contains("KVKK silme")).Should().Be(1,
            "yeniden oynatılan silmeler yeni bir şey yapmadı — sahaya inme kanıtı yinelenmez");
    }

    [Fact]
    public async Task Cevrimdisiyken_yigin_izi_yalniz_ilk_hatada_sunucuya_ulasinca_sifirlanir()
    {
        var offline = true;
        using var fx = Build(after => offline ? throw new HttpRequestException("ağ yok") : Page(after));

        await fx.Svc.PullOnceAsync(CancellationToken.None);
        await fx.Svc.PullOnceAsync(CancellationToken.None);
        offline = false;
        (await fx.Svc.PullOnceAsync(CancellationToken.None)).Should().Be(CustomerPullOutcome.CaughtUp);
        offline = true;
        await fx.Svc.PullOnceAsync(CancellationToken.None);

        fx.Log.Entries.Where(e => e.Level == LogLevel.Warning).Select(e => e.Exception is not null)
            .Should().Equal(new[] { true, false, true });
    }

    // ── iptal, sayfa sınırı (M-6, M-7) ──────────────────────────────────

    [Fact]
    public async Task Iptal_edilince_imlec_son_uygulanan_ogede_kaydedilir()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        using var cts = new CancellationTokenSource();
        Fixture? fixture = null;
        using var fx = fixture = Build(after =>
        {
            // Sayfa yanıtından sonraki ilk depo bağlantısı = ilk öğenin uygulaması: öğe uygulanır,
            // iptal ikinci öğeden önce görülür.
            if (after == 0) fixture!.Faults.OnNextOpen(cts.Cancel);
            return FakeHttpMessageHandler.Json(200,
                after == 0 ? Page(5, Item(a, "ornek_a", 3), Item(b, "ornek_b", 5)) : Page(after));
        });

        var act = () => fx.Svc.PullOnceAsync(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        Exists(fx, a.ToString("N")).Should().BeTrue();
        Exists(fx, b.ToString("N")).Should().BeFalse();
        fx.FeedCursor.Should().Be(3, "uygulanan öğe yeniden istenmesin");
    }

    [Fact]
    public async Task Tur_basina_sayfa_siniri_kalan_sonraki_turda_surer()
    {
        var max = CustomerChangesPullService.MaxPagesPerRound;
        using var fx = Build(after => after <= max
            ? Page(after + 1, Item(Guid.NewGuid(), $"ornek_{after}", after + 1))
            : Page(after));

        (await fx.Svc.PullOnceAsync(CancellationToken.None)).Should().Be(CustomerPullOutcome.MorePending);
        fx.Pulls.Should().Be(max);
        fx.FeedCursor.Should().Be(max);
        fx.Tracker.IsInitialCatchUpDone.Should().BeFalse("sayfa sınırı yetişme sayılmaz");

        (await fx.Svc.PullOnceAsync(CancellationToken.None)).Should().Be(CustomerPullOutcome.CaughtUp);
        fx.FeedCursor.Should().Be(max + 1);
        fx.Tracker.IsInitialCatchUpDone.Should().BeTrue();
    }

    // ── arka plan işi (M-10) ────────────────────────────────────────────

    [Fact]
    public async Task Arka_plan_isi_acilista_ritmi_beklemeden_bir_tur_kosar()
    {
        var firstRound = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var fx = Build(after => Page(after), licenseProvider: new CallbackLicenseProvider(() =>
        {
            firstRound.TrySetResult();
            return null;
        }));
        using var hosted = new CustomerChangesPullHostedService(
            fx.Svc, NullLogger<CustomerChangesPullHostedService>.Instance, TimeSpan.FromHours(1));

        await hosted.StartAsync(CancellationToken.None);
        try
        {
            await firstRound.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            await hosted.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Arka_plan_isi_turun_hatasindan_sonra_dongu_surer()
    {
        var calls = 0;
        var secondRound = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var fx = Build(after => Page(after), licenseProvider: new CallbackLicenseProvider(() =>
        {
            if (Interlocked.Increment(ref calls) == 1) throw new InvalidOperationException("enjekte tur hatası");
            secondRound.TrySetResult();
            return null;
        }));
        using var hosted = new CustomerChangesPullHostedService(
            fx.Svc, NullLogger<CustomerChangesPullHostedService>.Instance, TimeSpan.FromMilliseconds(10));

        await hosted.StartAsync(CancellationToken.None);
        try
        {
            await secondRound.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            await hosted.StopAsync(CancellationToken.None);
        }
        hosted.ExecuteTask!.IsFaulted.Should().BeFalse();
    }
}
