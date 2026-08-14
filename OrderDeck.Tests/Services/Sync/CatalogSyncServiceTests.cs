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
using Microsoft.Extensions.Logging;
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
    /// <summary>
    /// Anahtarı taşır <b>ve okunma sayısını sayar</b>. Sayaç bir "tur denendi"
    /// probu: <c>SyncOnceAsync</c> anahtarı ilk satırında tam bir kez okuyor,
    /// dolayısıyla okuma sayısı = denenen tur sayısı. Lisans yokken hiç HTTP
    /// isteği çıkmadığı için ritmi başka türlü ölçmenin yolu yok.
    /// </summary>
    private sealed class FakeLicenseProvider : ICurrentLicenseProvider
    {
        private string? _key;
        private int _reads;

        public int Reads => Volatile.Read(ref _reads);
        public Action<int>? OnRead { get; set; }

        public string? CurrentLicenseKey
        {
            get
            {
                var n = Interlocked.Increment(ref _reads);
                OnRead?.Invoke(n);
                return Volatile.Read(ref _key);
            }
            set => Volatile.Write(ref _key, value);
        }
    }

    private sealed class FakeHttpClientFactory : IHttpClientFactory
    {
        private readonly Func<string, HttpClient> _create;
        public FakeHttpClientFactory(Func<string, HttpClient> create) => _create = create;
        public HttpClient CreateClient(string name) => _create(name);
    }

    /// <summary>Kayıt seviyesini sınamak için; arka plan turları da yazdığından kilitli.</summary>
    private sealed class RecordingLogger<T> : ILogger<T>
    {
        private readonly List<(LogLevel Level, string Message)> _entries = new();

        public IReadOnlyList<(LogLevel Level, string Message)> Entries
        {
            get { lock (_entries) return _entries.ToList(); }
        }

        IDisposable? ILogger.BeginScope<TState>(TState state) => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_entries) _entries.Add((logLevel, formatter(state, exception)));
        }
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
        private readonly List<CatalogCategoryPullItem> _categories;
        private readonly InMemorySqlite _db;
        private readonly string _photoRoot;
        private readonly FakeLicenseProvider _license = new();
        private readonly bool _endless;
        private int _failPage = -1;
        private int _photoDownloads;
        private int _requestCount;
        private int _nextCode = 1;

        private readonly RecordingLogger<CatalogSyncService> _log = new();

        public CatalogSyncService Service { get; }
        public CatalogReplicaRepository Repo { get; }
        public CatalogPhotoCache Photos { get; }

        /// <summary>Servisin yazdığı kayıtlar (seviye + biçimlenmiş metin).</summary>
        public IReadOnlyList<(LogLevel Level, string Message)> Logs => _log.Entries;

        /// <summary>Kategori ucu 200 döner ama gövde JSON değildir (bozuk yanıt).</summary>
        public bool CorruptCategoriesBody { get; set; }

        /// <summary>Ürün uçlarına giden <c>after</c> değerleri, sırasıyla.</summary>
        public List<Guid?> RequestedAfterCursors { get; } = new();

        /// <summary>Fotoğraf indirmede istenen HttpClient adları.</summary>
        public List<string> CreatedClientNames { get; } = new();

        public int PhotoDownloads => _photoDownloads;
        public int RequestCount => _requestCount;

        /// <summary>Denenen <c>SyncOnceAsync</c> turu sayısı (lisans okuması probu).</summary>
        public int SyncAttempts => _license.Reads;

        public Action<int>? OnSyncAttempt { get => _license.OnRead; set => _license.OnRead = value; }

        /// <summary>Fotoğraf isteğinde fırlatılacak istisna (zaman aşımı taklidi).</summary>
        public Exception? PhotoThrows { get; set; }

        /// <summary>Fotoğraf istemcisi ÜRETİLİRKEN fırlatılacak istisna.</summary>
        public Exception? PhotoClientFactoryThrows { get; set; }

        /// <summary>Her fotoğraf isteğinin başında çağrılır (iptal enjekte etmek için).</summary>
        public Action? OnPhotoRequest { get; set; }

        /// <summary>Kategori isteğinin başında çağrılır (tur içinde kilitlenmek için).</summary>
        public Action? OnCategoriesRequest { get; set; }

        public Guid LastIdOfPage(int index) => _pages[index][^1].Id;
        public void FailProductPage(int index) => _failPage = index;
        public void SetLicenseKey(string? key) => _license.CurrentLicenseKey = key;

        public CatalogProductPullItem ProductAt(int page, int index) => _pages[page][index];

        /// <summary>
        /// Sunucudan bir ürünü kaldırır (panelden silinmiş taklidi). İlk ürün
        /// seçiliyor ki sayfanın SON ürününün Id'si — yani imleç — değişmesin.
        /// </summary>
        public CatalogProductPullItem DropFirstProduct()
        {
            var removed = _pages[0][0];
            _pages[0].RemoveAt(0);
            return removed;
        }

        public static Harness WithProductPages(int[] pageSizes, bool withPhotos = false)
            => new(pageSizes, withPhotos, endless: false, singlePage: null, categories: null);

        /// <summary>
        /// Alan eşlemesini çivilemek için elle kurulmuş katalog: tek sayfa ürün
        /// + kategori ağacı.
        /// </summary>
        public static Harness WithCatalog(
            List<CatalogProductPullItem> products,
            List<CatalogCategoryPullItem>? categories = null)
            => new([], withPhotos: false, endless: false, singlePage: products, categories: categories);

        /// <summary>
        /// Hiç bitmeyen sunucu: her istekte <c>take</c> kadar TAZE ürün döner.
        /// <c>MaxPages</c> tavanına çarpan senkronun ne yaptığını sınar.
        /// </summary>
        public static Harness WithEndlessFullPages()
            => new([], withPhotos: false, endless: true, singlePage: null, categories: null);

        private Harness(
            int[] pageSizes,
            bool withPhotos,
            bool endless,
            List<CatalogProductPullItem>? singlePage,
            List<CatalogCategoryPullItem>? categories)
        {
            _endless = endless;
            _categories = categories ?? new List<CatalogCategoryPullItem>();

            if (singlePage is not null)
                _pages.Add(singlePage);
            else
                foreach (var size in pageSizes)
                    _pages.Add(Enumerable.Range(0, size).Select(_ => MakeProduct(withPhotos)).ToList());

            _db = new InMemorySqlite();
            new MigrationRunner(_db).Run();
            Repo = new CatalogReplicaRepository(_db);

            _photoRoot = Path.Combine(Path.GetTempPath(), "od-sync-photo-" + Guid.NewGuid().ToString("N"));
            Photos = new CatalogPhotoCache(_photoRoot);

            var apiHttp = new HttpClient(new FakeHttpMessageHandler(Respond))
            {
                BaseAddress = new Uri("https://test.local")
            };
            var api = new LicenseApiClient(apiHttp, new LicenseTokenStore());

            var photoHttp = new HttpClient(new FakeHttpMessageHandler(_ =>
            {
                Interlocked.Increment(ref _photoDownloads);
                OnPhotoRequest?.Invoke();
                if (PhotoThrows is not null) throw PhotoThrows;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent([1, 2, 3])
                };
            }));
            var factory = new FakeHttpClientFactory(name =>
            {
                CreatedClientNames.Add(name);
                if (PhotoClientFactoryThrows is not null) throw PhotoClientFactoryThrows;
                return photoHttp;
            });

            _license.CurrentLicenseKey = TestLicenseKey;

            Service = new CatalogSyncService(
                api, Repo, Photos, factory, _license, _log);
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
            {
                OnCategoriesRequest?.Invoke();
                // 200 + JSON olmayan gövde: LicenseApiClient bunu bilerek
                // LicenseApiUnknownException'a çeviriyor ("katalog boş" DEĞİL).
                if (CorruptCategoriesBody)
                    return FakeHttpMessageHandler.Json(200, "<html>hata sayfasi</html>");
                return FakeHttpMessageHandler.Json(200, JsonSerializer.Serialize(_categories));
            }

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

    // ── Alan eşlemesi: DTO → replika ──────────────────────────────────────────

    /// <summary>
    /// Bu görevin varlık sebebi tek cümle: <b>sohbete yazılan kod doğru ürüne
    /// düşsün</b>. Kodun yerine adın yazıldığı, eksenlerin takas olduğu ya da
    /// varyantların düştüğü bir replika sayfalama testlerinin hepsini geçer —
    /// o yüzden eşleme burada alan alan çivileniyor.
    ///
    /// Ürün bilerek zor kurulmuş: <c>Code != Name</c>, iki eksen FARKLI
    /// değerlerle dolu, raf yeri dolu, kategorisi var ve <b>iki</b> varyantı
    /// sunucu sırasında (XL önce, S sonra) — yani alfabetik/ordinal sıranın
    /// tersinde geliyor.
    /// </summary>
    [Fact]
    public async Task Every_field_survives_the_round_trip_from_the_wire_to_the_replica()
    {
        var productId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var parentCategoryId = Guid.Parse("bbbbbbbb-cccc-dddd-eeee-ffffffffffff");
        var categoryId = Guid.Parse("66666666-7777-8888-9999-aaaaaaaaaaaa");
        var xlVariantId = Guid.Parse("0a0a0a0a-1b1b-2c2c-3d3d-4e4e4e4e4e4e");
        var sVariantId = Guid.Parse("f0f0f0f0-e1e1-d2d2-c3c3-b4b4b4b4b4b4");

        var product = new CatalogProductPullItem(
            Id: productId,
            CategoryId: categoryId,
            Code: "KOD-7",
            Name: "Yazlık Elbise",
            NameSearch: "YAZLIK ELBISE",
            DefaultPrice: 249.95m,
            ShelfLocation: "R3-K2",
            Axis1Name: "Beden", Axis1Role: 1,
            Axis2Name: "Renk", Axis2Role: 2,
            UpdatedAt: DateTimeOffset.FromUnixTimeSeconds(1_723_000_000L),
            CoverPhotoKey: "lic/products/kod7/kapak.img",
            CoverPhotoUrl: null,
            Variants: [
                new CatalogVariantPullItem(xlVariantId, "XL", "XL", "Kırmızı", "KR", "KOD-7-XL-KR", "8690000000017", true),
                new CatalogVariantPullItem(sVariantId, "S", "S", "Mavi", "MV", "KOD-7-S-MV", null, false)
            ]);

        var categories = new List<CatalogCategoryPullItem>
        {
            new(parentCategoryId, null, "Kadın", "0001", 5, true),
            new(categoryId, parentCategoryId, "Elbise", "0001.0002", 7, false)
        };

        using var harness = Harness.WithCatalog([product], categories);

        await harness.Service.SyncOnceAsync(CancellationToken.None);

        var stored = harness.Repo.FindByCode("KOD-7");
        stored.Should().NotBeNull();

        // Kimlikler repo genelinde tiresiz "N" biçiminde (bkz.
        // ShopperRegistrationIngestService); tireli bir Id başka yerde üretilen
        // kimliklerle SESSİZCE eşleşmez.
        stored!.Id.Should().Be("11111111222233334444555555555555");
        stored.CategoryId.Should().Be("66666666777788889999aaaaaaaaaaaa");
        stored.Code.Should().Be("KOD-7", "kod ile ad takas olursa yayında yanlış ürün eşleşir");
        stored.CodeNormalized.Should().Be("KOD-7");
        stored.Name.Should().Be("Yazlık Elbise");
        stored.DefaultPrice.Should().Be(249.95m);
        stored.ShelfLocation.Should().Be("R3-K2");
        stored.Axis1Name.Should().Be("Beden");
        stored.Axis1Role.Should().Be(1);
        stored.Axis2Name.Should().Be("Renk");
        stored.Axis2Role.Should().Be(2);
        stored.CoverPhotoKey.Should().Be("lic/products/kod7/kapak.img");
        // Unix SANİYE — .LocalDateTime.Ticks değil: yerel saate çevirmek tr-TR
        // makinede sessiz veri hatası üretirdi.
        stored.UpdatedAt.Should().Be(1_723_000_000L);

        var variants = harness.Repo.GetVariants(stored.Id);
        variants.Should().HaveCount(2, "varyantlar düşerse beden seçimi imkânsızlaşır");
        variants.Select(v => v.VariantCode).Should().Equal(
            new[] { "KOD-7-XL-KR", "KOD-7-S-MV" },
            "sıra sunucunun kararı; SortOrder dizideki konumu taşır");
        variants[0].SortOrder.Should().Be(0);
        variants[1].SortOrder.Should().Be(1);
        variants[0].Id.Should().Be("0a0a0a0a1b1b2c2c3d3d4e4e4e4e4e4e");
        variants[0].ProductId.Should().Be("11111111222233334444555555555555");
        variants[1].ProductId.Should().Be("11111111222233334444555555555555");
        variants[0].Axis1Value.Should().Be("XL");
        variants[0].Axis1Code.Should().Be("XL");
        variants[0].Axis2Value.Should().Be("Kırmızı");
        variants[0].Axis2Code.Should().Be("KR");
        variants[0].Barcode.Should().Be("8690000000017");
        variants[0].IsActive.Should().BeTrue();
        variants[1].Id.Should().Be("f0f0f0f0e1e1d2d2c3c3b4b4b4b4b4b4");
        variants[1].Axis1Value.Should().Be("S");
        variants[1].Axis2Value.Should().Be("Mavi");
        variants[1].Barcode.Should().BeNull();
        variants[1].IsActive.Should().BeFalse();

        var storedCategories = harness.Repo.GetCategories();
        storedCategories.Should().HaveCount(2, "kategoriler düşerse ürün ağacı yerelde yok olur");
        storedCategories[0].Id.Should().Be("bbbbbbbbccccddddeeeeffffffffffff");
        storedCategories[0].ParentCategoryId.Should().BeNull();
        storedCategories[0].Name.Should().Be("Kadın");
        storedCategories[0].Path.Should().Be("0001");
        storedCategories[0].SortOrder.Should().Be(5);
        storedCategories[0].IsActive.Should().BeTrue();
        storedCategories[1].Id.Should().Be("66666666777788889999aaaaaaaaaaaa");
        storedCategories[1].ParentCategoryId.Should().Be("bbbbbbbbccccddddeeeeffffffffffff");
        storedCategories[1].Name.Should().Be("Elbise");
        storedCategories[1].Path.Should().Be("0001.0002");
        storedCategories[1].SortOrder.Should().Be(7);
        // Sunucu PASİF kategorileri de gönderiyor; replika bunu bozuk veri
        // saymamalı (bkz. GetCatalogCategoriesAsync XML dokümanı).
        storedCategories[1].IsActive.Should().BeFalse();
    }

    /// <summary>
    /// Kod çok kelimeli ve Türkçe harfli olabilir. Sohbete "ışıltı 1" yazan da
    /// "IŞILTI 1" yazan da "isilti 1" yazan da aynı ürüne düşmeli — saklanan
    /// kolon da aranan iğne de <c>SearchNormalizer</c>'dan geçtiği için.
    /// Bütün kodların zaten büyük harf ASCII olduğu bir katalogda bu garanti
    /// hiç sınanmaz: <c>Normalize</c> orada birim fonksiyondur.
    /// </summary>
    [Fact]
    public async Task A_turkish_multi_word_code_matches_whatever_the_chat_types()
    {
        var product = new CatalogProductPullItem(
            Id: Guid.NewGuid(), CategoryId: null,
            Code: "ışıltı 1", Name: "Işıltı Elbise", NameSearch: "ISILTI ELBISE",
            DefaultPrice: 99m, ShelfLocation: null,
            Axis1Name: null, Axis1Role: null, Axis2Name: null, Axis2Role: null,
            UpdatedAt: DateTimeOffset.FromUnixTimeSeconds(1_723_000_000L),
            CoverPhotoKey: null, CoverPhotoUrl: null,
            Variants: []);

        using var harness = Harness.WithCatalog([product]);

        await harness.Service.SyncOnceAsync(CancellationToken.None);

        harness.Repo.FindByCode("ışıltı 1").Should().NotBeNull("birebir yazım");
        harness.Repo.FindByCode("IŞILTI 1").Should().NotBeNull("büyük harf farkı önemsiz");
        harness.Repo.FindByCode("Işıltı 1").Should().NotBeNull("karışık harf farkı önemsiz");
        harness.Repo.FindByCode("isilti 1").Should().NotBeNull("Türkçe harfler ASCII'ye katlanır");
        harness.Repo.FindByCode("ISILTI 1").Should().NotBeNull("ASCII + büyük harf");
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

    /// <summary>
    /// Katalogdan düşen ürünün fotoğrafı diskte kalmamalı; kalanınki durmalı.
    /// Yalnız koruma kümesini sınamak yetmez — <c>Prune</c> çağrısının kendisi
    /// silinirse hiçbir şey fark etmezdi.
    /// </summary>
    [Fact]
    public async Task A_product_that_disappears_from_the_server_loses_its_cached_photo()
    {
        using var harness = Harness.WithProductPages([2], withPhotos: true);
        await harness.Service.SyncOnceAsync(CancellationToken.None);

        var dropped = harness.DropFirstProduct();
        var survivor = harness.ProductAt(0, 0);
        harness.Photos.ResolveAbsolute(dropped.CoverPhotoKey).Should().NotBeNull("ön koşul: ilk turda indi");

        await harness.Service.SyncOnceAsync(CancellationToken.None);

        harness.Photos.ResolveAbsolute(dropped.CoverPhotoKey)
            .Should().BeNull("katalogdan düşen ürünün dosyası temizlenir");
        harness.Photos.ResolveAbsolute(survivor.CoverPhotoKey)
            .Should().NotBeNull("kalan ürünün dosyası korunur");
    }

    /// <summary>
    /// <b>Anahtar dolu ama adres null</b> MEŞRU bir yanıt: sunucu R2 imzalama
    /// hatasını yutup sayfayı yine 200 döndürüyor. Böyle bir tur, fotoğrafı
    /// "silinmiş" saymamalı — önbellekteki dosya durmalı — ve <b>indirmeyi
    /// hiç denememeli</b>.
    ///
    /// Ölçüldü: "denemez" kısmının tek gözlenebilir izi KAYIT. Muhafız
    /// silinince önbellekte olmayan ürün için <c>GetByteArrayAsync(null)</c>
    /// çağrılıyor, istemcinin <c>BaseAddress</c>'i olmadığı için istek daha
    /// yola çıkmadan düşüyor (yani indirme sayacı yine 0 kalıyor) ve tur
    /// başına ürün başına bir "Kapak fotoğrafı indirilemedi" satırı birikiyor:
    /// üretimde 5 dakikada bir tekrarlayan, hiçbir zaman düzelmeyen sahte
    /// arıza gürültüsü. Testin RED'e döndüğü yer de burası.
    /// </summary>
    [Fact]
    public async Task A_null_photo_url_is_skipped_and_the_cached_file_survives()
    {
        var cachedId = Guid.NewGuid();
        var uncachedId = Guid.NewGuid();
        var cachedKey = $"lic/products/{cachedId:N}/kapak.img";
        var uncachedKey = $"lic/products/{uncachedId:N}/kapak.img";

        using var harness = Harness.WithCatalog(
        [
            UrlLessProduct(cachedId, "A1", cachedKey),
            UrlLessProduct(uncachedId, "A2", uncachedKey)
        ]);

        // Önceki turda, adres hâlâ imzalanabiliyorken inmiş dosya.
        harness.Photos.Save(cachedKey, [1, 2, 3]);

        var written = await harness.Service.SyncOnceAsync(CancellationToken.None);

        written.Should().Be(2, "adressiz fotoğraf katalogu düşürmez");
        harness.Photos.ResolveAbsolute(cachedKey)
            .Should().NotBeNull("adres gelmedi diye önbellekteki dosya atılmaz");
        harness.PhotoDownloads.Should().Be(0, "indirilecek bir adres yok");
        harness.Logs.Should().NotContain(
            e => e.Message.Contains("Kapak fotoğrafı indirilemedi"),
            "adressiz ürün bir ARIZA değil, sunucunun bilinen davranışı");
    }

    /// <summary>Kapak anahtarı dolu, imzalı adresi null olan ürün.</summary>
    private static CatalogProductPullItem UrlLessProduct(Guid id, string code, string coverKey)
        => new(
            Id: id,
            CategoryId: null,
            Code: code,
            Name: $"Ürün {code}",
            NameSearch: $"ÜRÜN {code}",
            DefaultPrice: 99m,
            ShelfLocation: null,
            Axis1Name: "Beden", Axis1Role: 1,
            Axis2Name: null, Axis2Role: null,
            UpdatedAt: DateTimeOffset.FromUnixTimeSeconds(1_700_000_000L),
            CoverPhotoKey: coverKey,
            CoverPhotoUrl: null,
            Variants: []);

    // ── İptal ile zaman aşımının ayrımı ───────────────────────────────────────

    /// <summary>
    /// <c>HttpClient</c>'ın zaman aşımı <see cref="TaskCanceledException"/>
    /// olarak yüzeye çıkıyor ve o da bir
    /// <see cref="OperationCanceledException"/>. Türe bakan bir filtre bunu
    /// yakalamaz: istisna fotoğraf döngüsünden kaçar ve <b>tek bir fotoğrafın
    /// zaman aşımı bütün turu başarısız yapar</b> — kalan fotoğraflar inmez,
    /// <c>Prune</c> atlanır ve <c>SyncOnceAsync</c>, replika az önce yazılmış
    /// olmasına rağmen 0 döner. Bedeli görünür: <c>CatalogSyncHostedService</c>
    /// yazan bir tur görene kadar 30 saniyelik açılış ritminde kalır, 5 dakikaya
    /// oturmaz. Doğru filtre <c>!ct.IsCancellationRequested</c>.
    /// </summary>
    [Fact]
    public async Task A_photo_timeout_is_a_failure_not_a_shutdown()
    {
        using var harness = Harness.WithProductPages([2], withPhotos: true);
        harness.PhotoThrows = new TaskCanceledException("timeout");

        var written = await harness.Service.SyncOnceAsync(CancellationToken.None);

        written.Should().Be(2, "tek fotoğrafın zaman aşımı katalogu düşürmez");
        harness.Repo.FindByCode("A1").Should().NotBeNull();
    }

    /// <summary>
    /// Fotoğraf döngüsünün DIŞINDA doğan, iptal olmayan bir
    /// <see cref="OperationCanceledException"/> de turdan kaçmamalı: dış
    /// <c>catch</c> arka plan servisine giden son eşik ve oradan kaçan bir
    /// <c>OperationCanceledException</c> hiçbir kayıt düşmeden yutuluyor
    /// (<c>CatalogSyncHostedService.RunRoundAsync</c>) — yani arıza tamamen
    /// sessizleşir.
    /// </summary>
    [Fact]
    public async Task An_operation_canceled_exception_without_cancellation_never_escapes_the_round()
    {
        using var harness = Harness.WithProductPages([1], withPhotos: true);
        harness.PhotoClientFactoryThrows = new TaskCanceledException("timeout");

        var written = await harness.Service.SyncOnceAsync(CancellationToken.None);

        written.Should().Be(0, "tur başarısız sayılır ama istisna dışarı sızmaz");
    }

    /// <summary>
    /// Gerçek iptal ise aynen yayılmalı: kapanışta arka plan servisi döngüden
    /// çıkabilsin diye.
    /// </summary>
    [Fact]
    public async Task A_genuine_cancellation_still_propagates()
    {
        using var harness = Harness.WithProductPages([1], withPhotos: true);
        using var cts = new CancellationTokenSource();

        // Kapanış anını taklit et: indirme sırasında token iptal edilir ve
        // HttpClient TaskCanceledException fırlatır.
        harness.OnPhotoRequest = () => cts.Cancel();
        harness.PhotoThrows = new TaskCanceledException("cancelled");

        var act = async () => await harness.Service.SyncOnceAsync(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // ── Kayıt seviyesi: bozuk gövde ayrı bir arıza ────────────────────────────

    /// <summary>
    /// 401, ağ kopması ve bozuk gövde tek satırda birbirine karışırsa üçü de
    /// aynı görünür — oysa ilk ikisi bir sonraki turda kendini onarır, bozuk
    /// gövde onarmaz: sunucu şeması ya da araya giren bir katman bozulmuştur
    /// ve replika sessizce bayatlar. <c>LicenseApiClient</c> bu durumu bilerek
    /// gürültülü yapıyor ("bu 'katalog boş' demek DEĞİLDİR"); senkron da onu
    /// Warning yığınında kaybetmemeli.
    /// </summary>
    [Fact]
    public async Task A_malformed_body_is_logged_louder_than_an_ordinary_failure()
    {
        using var corrupt = Harness.WithProductPages([1]);
        corrupt.CorruptCategoriesBody = true;

        (await corrupt.Service.SyncOnceAsync(CancellationToken.None)).Should().Be(0);
        corrupt.Logs.Should().Contain(e => e.Level == LogLevel.Error,
            "bozuk gövde kendini onarmayan bir arıza");

        using var networkFailure = Harness.WithProductPages([2, 1]);
        networkFailure.FailProductPage(index: 1);

        (await networkFailure.Service.SyncOnceAsync(CancellationToken.None)).Should().Be(0);
        networkFailure.Logs.Should().NotContain(e => e.Level == LogLevel.Error,
            "sıradan bir 5xx bir sonraki turda kendini onarır; Error değil Warning");
        networkFailure.Logs.Should().Contain(e => e.Level == LogLevel.Warning);
    }

    // ── Eşzamanlılık kapısı ───────────────────────────────────────────────────

    /// <summary>
    /// <c>CatalogSyncService</c> singleton ve <c>SyncOnceAsync</c> public: iki
    /// tur aynı anda koşabilir. <c>CatalogPhotoCache</c> ise iş parçacığı
    /// güvenli DEĞİL — paralel tur, süren indirmenin <c>.tmp</c> dosyasını
    /// siler. Kapı sıfır zaman aşımlı: süren tur zaten aynı TAM anlık
    /// görüntüyü çekiyor, ikinciyi kuyruğa almak iki tam çekmeyi arka arkaya
    /// koştururdu.
    /// </summary>
    [Fact]
    public async Task A_second_round_started_while_one_runs_returns_immediately()
    {
        using var harness = Harness.WithProductPages([1]);
        using var firstRoundInside = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);

        var blocked = 0;
        harness.OnCategoriesRequest = () =>
        {
            // Yalnız ilk tur bekletilir; kapı kaldırılırsa ikinci tur kilitlenip
            // testi asmasın, temiz bir assert hatasıyla düşsün.
            if (Interlocked.Exchange(ref blocked, 1) != 0) return;
            firstRoundInside.Set();
            release.Wait(TimeSpan.FromSeconds(10));
        };

        var first = Task.Run(() => harness.Service.SyncOnceAsync(CancellationToken.None));
        firstRoundInside.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue("ilk tur başlamalıydı");

        var second = await harness.Service.SyncOnceAsync(CancellationToken.None);

        second.Should().Be(0, "süren tur varken ikinci çağrı kuyruğa alınmaz, anında 0 döner");

        release.Set();
        (await first).Should().Be(1);
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

    // ── Arka plan ritmi ───────────────────────────────────────────────────────

    /// <summary>
    /// Açılışta lisans anahtarı henüz çözülmemiş olabilir (giriş/token gelmemiş);
    /// o tur 0 döner. Tek ritim 5 dakika olsaydı taze giriş yapan operatör bütün
    /// yayın hazırlığı boyunca BOŞ katalogla otururdu. Bu yüzden ilk GERÇEKTEN
    /// başarılı tura kadar hızlı ritim, sonra yerleşik ritim.
    /// </summary>
    [Fact]
    public async Task Retries_on_a_fast_cadence_until_the_first_successful_round()
    {
        using var harness = Harness.WithProductPages([1]);
        harness.SetLicenseKey(null);

        var fastRetriesSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.OnSyncAttempt = n => { if (n >= 3) fastRetriesSeen.TrySetResult(); };

        var roundCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.OnCategoriesRequest = () => roundCompleted.TrySetResult();

        var hosted = new CatalogSyncHostedService(
            harness.Service,
            NullLogger<CatalogSyncHostedService>.Instance,
            startupInterval: TimeSpan.FromMilliseconds(20),
            steadyInterval: TimeSpan.FromSeconds(30));

        using var cts = new CancellationTokenSource();
        await hosted.StartAsync(cts.Token);
        try
        {
            // 1) Lisans yokken hızlı ritimde yeniden dener.
            await fastRetriesSeen.Task.WaitAsync(TimeSpan.FromSeconds(5));

            // 2) Lisans gelir, ilk başarılı tur koşar.
            harness.SetLicenseKey(TestLicenseKey);
            await roundCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            // 3) Artık yerleşik ritim: 30 saniyelik pencerede yeni tur yok.
            await Task.Delay(300);
            var settledAt = harness.SyncAttempts;
            await Task.Delay(400);
            harness.SyncAttempts.Should().Be(settledAt,
                "ilk başarılı turdan sonra periyot yerleşik değere çıkar");
        }
        finally
        {
            cts.Cancel();
            try { await hosted.StopAsync(CancellationToken.None); } catch { /* kapanış */ }
        }
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
