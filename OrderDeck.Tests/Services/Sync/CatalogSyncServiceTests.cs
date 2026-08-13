using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using OrderDeck.App.Services;
using OrderDeck.App.Services.Sync;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Licensing.Api;
using OrderDeck.Licensing.Api.Models;
using OrderDeck.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.Tests.Services.Sync;

/// <summary>
/// Katalog senkronunun sözleşmesi. Koşum takımı
/// <c>ShopperRegistrationIngestServiceTests</c>'ten alındı (sahte handler,
/// <c>ICurrentLicenseProvider</c> sahtesi, lisans listesi JSON'u); yalnız
/// yönlendirmeye katalog dalları ve fotoğraf indirmesi için ayrı bir
/// <see cref="IHttpClientFactory"/> eklendi.
/// </summary>
public sealed class CatalogSyncServiceTests
{
    private sealed class FakeLicenseProvider : ICurrentLicenseProvider
    {
        public string? CurrentLicenseKey { get; set; }
    }

    private sealed class FakeHttpClientFactory : IHttpClientFactory
    {
        private readonly Func<string, HttpClient> _create;
        public FakeHttpClientFactory(Func<string, HttpClient> create) => _create = create;
        public HttpClient CreateClient(string name) => _create(name);
    }

    private static readonly Guid TestLicenseId = Guid.Parse("aaaabbbb-cccc-dddd-eeee-ffffaaaabbbb");
    private const string TestLicenseKey = "CATALOG-TEST-KEY";

    private static string LicensesJson() =>
        $"[{{\"id\":\"{TestLicenseId}\",\"licenseKey\":\"{TestLicenseKey}\"}}]";

    /// <summary>
    /// Sahte sunucu + gerçek servis. Sayfalar önceden üretilir; imleç
    /// çözümlemesi <b>sayfanın son ürününün Id'si</b> üstünden yapılır — yanlış
    /// imleçle gelen bir istek hiçbir sayfaya denk gelmez ve 500 döner.
    /// </summary>
    private sealed class Harness : IDisposable
    {
        private readonly List<List<CatalogProductPullItem>> _pages = new();
        private readonly InMemorySqlite _db;
        private readonly string _photoRoot;
        private readonly FakeLicenseProvider _license = new();
        private readonly bool _endless;
        private int _failPage = -1;
        private int _photoDownloads;
        private int _requestCount;
        private int _nextCode = 1;

        public CatalogSyncService Service { get; }
        public CatalogReplicaRepository Repo { get; }

        /// <summary>Ürün uçlarına giden <c>after</c> değerleri, sırasıyla.</summary>
        public List<Guid?> RequestedAfterCursors { get; } = new();

        /// <summary>Fotoğraf indirmede istenen HttpClient adları.</summary>
        public List<string> CreatedClientNames { get; } = new();

        public int PhotoDownloads => _photoDownloads;
        public int RequestCount => _requestCount;

        public Guid LastIdOfPage(int index) => _pages[index][^1].Id;
        public void FailProductPage(int index) => _failPage = index;
        public void SetLicenseKey(string? key) => _license.CurrentLicenseKey = key;

        public static Harness WithProductPages(int[] pageSizes, bool withPhotos = false)
            => new(pageSizes, withPhotos, endless: false);

        /// <summary>
        /// Hiç bitmeyen sunucu: her istekte <c>take</c> kadar TAZE ürün döner.
        /// <c>MaxPages</c> tavanına çarpan senkronun ne yaptığını sınar.
        /// </summary>
        public static Harness WithEndlessFullPages() => new([], withPhotos: false, endless: true);

        private Harness(int[] pageSizes, bool withPhotos, bool endless)
        {
            _endless = endless;
            foreach (var size in pageSizes)
                _pages.Add(Enumerable.Range(0, size).Select(_ => MakeProduct(withPhotos)).ToList());

            _db = new InMemorySqlite();
            new MigrationRunner(_db).Run();
            Repo = new CatalogReplicaRepository(_db);

            _photoRoot = Path.Combine(Path.GetTempPath(), "od-sync-photo-" + Guid.NewGuid().ToString("N"));

            var apiHttp = new HttpClient(new FakeHttpMessageHandler(Respond))
            {
                BaseAddress = new Uri("https://test.local")
            };
            var api = new LicenseApiClient(apiHttp, new LicenseTokenStore());

            var photoHttp = new HttpClient(new FakeHttpMessageHandler(_ =>
            {
                Interlocked.Increment(ref _photoDownloads);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent([1, 2, 3])
                };
            }));
            var factory = new FakeHttpClientFactory(name =>
            {
                CreatedClientNames.Add(name);
                return photoHttp;
            });

            _license.CurrentLicenseKey = TestLicenseKey;

            Service = new CatalogSyncService(
                api, Repo, new CatalogPhotoCache(_photoRoot), factory, _license,
                NullLogger<CatalogSyncService>.Instance);
        }

        private CatalogProductPullItem MakeProduct(bool withPhoto)
        {
            var n = _nextCode++;
            var id = Guid.NewGuid();
            var key = withPhoto ? $"lic/products/{id:N}/kapak.img" : null;
            return new CatalogProductPullItem(
                Id: id,
                CategoryId: null,
                Code: $"A{n}",
                Name: $"Ürün {n}",
                NameSearch: $"ÜRÜN {n}",
                DefaultPrice: 10m + n,
                ShelfLocation: null,
                Axis1Name: "Beden", Axis1Role: 1,
                Axis2Name: null, Axis2Role: null,
                UpdatedAt: DateTimeOffset.FromUnixTimeSeconds(1_700_000_000L + n),
                CoverPhotoKey: key,
                CoverPhotoUrl: key is null ? null : $"https://r2.test.local/{id:N}?sig=abc",
                Variants: [
                    new CatalogVariantPullItem(Guid.NewGuid(), "M", "M", null, null, $"A{n}-M", null, true)
                ]);
        }

        private HttpResponseMessage Respond(HttpRequestMessage req)
        {
            Interlocked.Increment(ref _requestCount);
            var uri = req.RequestUri!;
            var path = uri.AbsolutePath;

            if (path == "/api/v1/me/licenses")
                return FakeHttpMessageHandler.Json(200, LicensesJson());

            if (path.Contains("/catalog/products"))
            {
                var after = QueryGuid(uri, "after");
                RequestedAfterCursors.Add(after);

                if (_endless)
                    return FakeHttpMessageHandler.Json(200, Serialize(
                        Enumerable.Range(0, QueryInt(uri, "take") ?? 200)
                                  .Select(_ => MakeProduct(withPhoto: false)).ToList()));

                var index = IndexFor(after);
                // Bilinmeyen imleç = çağıranın imleci yanlış yerden aldığı anlamına
                // gelir; sessizce boş sayfa dönmek bunu gizlerdi.
                if (index < 0) return FakeHttpMessageHandler.Empty(500);
                if (index == _failPage) return FakeHttpMessageHandler.Empty(500);
                if (index >= _pages.Count) return FakeHttpMessageHandler.Json(200, "[]");
                return FakeHttpMessageHandler.Json(200, Serialize(_pages[index]));
            }

            if (path.Contains("/catalog/categories"))
                return FakeHttpMessageHandler.Json(200, "[]");

            return FakeHttpMessageHandler.Empty(404);
        }

        private int IndexFor(Guid? after)
        {
            if (after is null) return 0;
            for (var i = 0; i < _pages.Count; i++)
                if (_pages[i].Count > 0 && _pages[i][^1].Id == after.Value) return i + 1;
            return -1;
        }

        private static string Serialize(List<CatalogProductPullItem> page)
            => JsonSerializer.Serialize(page);

        private static string? QueryValue(Uri uri, string name)
        {
            foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = part.Split('=', 2);
                if (kv.Length == 2 && kv[0] == name) return Uri.UnescapeDataString(kv[1]);
            }
            return null;
        }

        private static Guid? QueryGuid(Uri uri, string name)
            => QueryValue(uri, name) is { } raw ? Guid.Parse(raw) : null;

        private static int? QueryInt(Uri uri, string name)
            => QueryValue(uri, name) is { } raw ? int.Parse(raw, System.Globalization.CultureInfo.InvariantCulture) : null;

        public void Dispose()
        {
            _db.Dispose();
            if (Directory.Exists(_photoRoot)) Directory.Delete(_photoRoot, recursive: true);
        }
    }

    // ── Sayfalama: BOŞ sayfa gelene kadar devam ───────────────────────────────

    /// <summary>
    /// Bitiş işareti kısa sayfa DEĞİL, boş sayfadır
    /// (<c>GetCatalogProductsAsync</c> XML dokümanının koyu yazdığı kural).
    /// Bu yüzden 200+200+1'lik katalog DÖRT istek eder: sonuncusu boş döner.
    /// Kısa sayfayla bitirmek, sayfa boyu sunucunun kırpma sınırını aştığı gün
    /// katalogu sessizce kırpardı.
    /// </summary>
    [Fact]
    public async Task Pulls_every_page_until_an_empty_page_arrives()
    {
        using var harness = Harness.WithProductPages([200, 200, 1]);

        var written = await harness.Service.SyncOnceAsync(CancellationToken.None);

        written.Should().Be(401);
        harness.RequestedAfterCursors.Should().Equal(new Guid?[]
        {
            null,
            harness.LastIdOfPage(0),
            harness.LastIdOfPage(1),
            harness.LastIdOfPage(2)
        }, "imleç her zaman bir önceki sayfanın SON ürününden gelir");
        harness.Repo.FindByCode("A401").Should().NotBeNull();
    }

    // ── Ya hep ya hiç ─────────────────────────────────────────────────────────

    [Fact]
    public async Task A_failure_midway_leaves_the_previous_replica_untouched()
    {
        using var harness = Harness.WithProductPages([200, 200, 1]);
        await harness.Service.SyncOnceAsync(CancellationToken.None);

        // İkinci sayfada 500: yarım liste ASLA yazılmamalı, yoksa silinmemiş
        // 201 ürün silinmiş sayılırdı.
        harness.FailProductPage(index: 1);

        var written = await harness.Service.SyncOnceAsync(CancellationToken.None);

        written.Should().Be(0);
        harness.Repo.FindByCode("A401").Should().NotBeNull("yarım çekme replikaya dokunmaz");
    }

    /// <summary>
    /// Sayfa tavanına çarpan senkron da yarım listedir: yazmak, tavandan
    /// sonraki bütün ürünleri "panelden silinmiş" yapardı.
    /// </summary>
    [Fact]
    public async Task Hitting_the_page_ceiling_writes_nothing()
    {
        using var harness = Harness.WithEndlessFullPages();

        var written = await harness.Service.SyncOnceAsync(CancellationToken.None);

        written.Should().Be(0);
        harness.Repo.FindByCode("A1").Should().BeNull("tavana çarpan tur replikaya yazmaz");
    }

    // ── Fotoğraflar ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Downloads_only_the_photos_that_are_not_cached_yet()
    {
        using var harness = Harness.WithProductPages([2], withPhotos: true);

        await harness.Service.SyncOnceAsync(CancellationToken.None);
        harness.PhotoDownloads.Should().Be(2);

        // İkinci turda anahtarlar değişmedi → tek bayt bile indirilmemeli.
        await harness.Service.SyncOnceAsync(CancellationToken.None);
        harness.PhotoDownloads.Should().Be(2);

        harness.CreatedClientNames.Should().OnlyContain(
            n => n == CatalogSyncService.PhotoClientName,
            "imzalı R2 adresine Authorization ekleyen istemci isteği bozar");
    }

    // ── Lisans yok ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Returns_zero_without_calling_the_api_when_no_license_key_is_set()
    {
        using var harness = Harness.WithProductPages([1]);
        harness.SetLicenseKey(null);

        var written = await harness.Service.SyncOnceAsync(CancellationToken.None);

        written.Should().Be(0);
        harness.RequestCount.Should().Be(0);
    }

    // ── DI ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Kayıtlar çözülebiliyor mu: bir eksik bağımlılık burada değil, kullanıcının
    /// makinesinde açılış çökmesi olarak görünürdü.
    /// </summary>
    [Fact]
    public void AppHost_resolves_the_service_and_registers_its_background_job()
    {
        using var host = new global::OrderDeck.App.AppHost();

        host.Services.GetRequiredService<CatalogSyncService>().Should().NotBeNull();
        // App.xaml.cs'e elle başlatma eklemek gerekmiyor:
        // WpfStartupEnvironment.StartBackgroundServicesAsync kayıtlı bütün
        // IHostedService'leri geziyor.
        host.Services.GetServices<IHostedService>()
            .Should().ContainSingle(s => s is CatalogSyncHostedService);
    }
}
