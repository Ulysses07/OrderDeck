using FluentAssertions;
using OrderDeck.App.Services.IntakeForm;
using OrderDeck.App.Services.Sync;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Core.Time;
using OrderDeck.Licensing.Api;
using OrderDeck.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace OrderDeck.Tests.Services.IntakeForm;

public sealed class IntakeFormSyncServiceTests
{
    private sealed class FakeClock : IClock
    {
        public long UnixNow() => 1714521600L;
        public DateTimeOffset Now => DateTimeOffset.FromUnixTimeSeconds(1714521600L);
    }

    private sealed class StubLicenseProvider : ICurrentLicenseProvider
    {
        public string? CurrentLicenseKey { get; set; }
    }

    // R9-D02/D03: imleç ve backfill işareti Customer satırlarıyla aynı SQLite
    // dosyasındaki SyncCursor tablosunda, lisans anahtarına bağlı.
    private const string TestLicenseKey = "LDK-TEST-FIXTURE";
    private const string CursorName = "intake-form-in";
    private const string BackfillMarkerName = "intake-fullname-backfill";

    private static (IntakeFormSyncService svc, CustomerRepository repo, SyncCursorRepository cursors, FakeHttpMessageHandler handler) Build(
        Func<HttpRequestMessage, HttpResponseMessage> responder,
        bool seedLicense = true)
    {
        var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var repo = new CustomerRepository(db);
        var cursors = new SyncCursorRepository(db);

        var handler = new FakeHttpMessageHandler(responder);
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://test.local") };
        var api = new LicenseApiClient(http, new OrderDeck.Licensing.Api.LicenseTokenStore());

        var licenseProvider = new StubLicenseProvider
        {
            CurrentLicenseKey = seedLicense ? TestLicenseKey : null
        };

        var svc = new IntakeFormSyncService(api, repo, cursors, licenseProvider, new FakeClock(),
            NullLogger<IntakeFormSyncService>.Instance);
        return (svc, repo, cursors, handler);
    }

    [Fact]
    public async Task SyncOnceAsync_returns_zero_when_server_returns_empty()
    {
        var (svc, repo, _, _) = Build(_ => FakeHttpMessageHandler.Json(200, "[]"));

        var count = await svc.SyncOnceAsync();

        count.Should().Be(0);
    }

    [Fact]
    public async Task SyncOnceAsync_creates_customer_with_form_platform()
    {
        var (svc, repo, _, _) = Build(_ => FakeHttpMessageHandler.Json(200,
            """[{"id":"00000000-0000-0000-0000-000000000001","username":"bilalcanli","fullName":"Bilal Canlı","address":"Atatürk Cad","submittedAt":"2026-04-30T12:00:00Z"}]"""));

        var count = await svc.SyncOnceAsync();

        count.Should().Be(1);
        var customers = repo.Search("bilalcanli", limit: 5);
        customers.Should().Contain(c => c.Platform == "form" && c.Username == "bilalcanli");
    }

    [Fact]
    public async Task SyncOnceAsync_updates_existing_form_customer_on_second_pull()
    {
        var (svc, repo, _, _) = Build(_ => FakeHttpMessageHandler.Json(200,
            "[{\"id\":\"00000000-0000-0000-0000-000000000001\",\"username\":\"u1\",\"fullName\":\"Eski Ad\",\"address\":\"Eski\",\"submittedAt\":\"2026-04-30T11:00:00Z\"},{\"id\":\"00000000-0000-0000-0000-000000000002\",\"username\":\"u1\",\"fullName\":\"Yeni Ad\",\"address\":\"Yeni\",\"submittedAt\":\"2026-04-30T12:00:00Z\"}]"));

        await svc.SyncOnceAsync();

        var customer = repo.Search("u1", limit: 5).Single(c => c.Platform == "form");
        customer.DisplayName.Should().Be("Yeni Ad");
        customer.Address.Should().Be("Yeni");
    }

    [Fact]
    public async Task SyncOnceAsync_advances_cursor_to_max_submittedAt()
    {
        var (svc, _, cursors, handler) = Build(_ => FakeHttpMessageHandler.Json(200,
            """[{"id":"00000000-0000-0000-0000-000000000001","username":"u","fullName":"n","address":"a","submittedAt":"2026-04-30T12:00:00Z"}]"""));

        await svc.SyncOnceAsync();

        cursors.Get(CursorName, TestLicenseKey)!.UpdatedAt
            .Should().Be(new DateTimeOffset(2026, 4, 30, 12, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task SyncOnceAsync_cursor_carries_the_last_row_id_not_just_the_timestamp()
    {
        // Aynı damgayı paylaşan iki kayıt. İmleç yalnız damga olsaydı, sunucu
        // bir sonraki turda `> damga` sorulduğu için ikisini de bir daha hiç
        // döndürmezdi — ve atlanan satır bir müşteri KAYDI.
        var (svc, _, cursors, _) = Build(_ => FakeHttpMessageHandler.Json(200,
            """
            [{"id":"00000000-0000-0000-0000-0000000000bb","username":"b","fullName":"B","address":"a","submittedAt":"2026-04-30T12:00:00Z"},
             {"id":"00000000-0000-0000-0000-0000000000aa","username":"a","fullName":"A","address":"a","submittedAt":"2026-04-30T12:00:00Z"}]
            """));

        await svc.SyncOnceAsync();

        var row = cursors.Get(CursorName, TestLicenseKey)!;
        row.UpdatedAt.Should().Be(new DateTimeOffset(2026, 4, 30, 12, 0, 0, TimeSpan.Zero));
        // R3-01: imleç sunucunun teslim ettiği SON satırın Id'si — istemci
        // yeniden sıralamaz (sunucu SQL uniqueidentifier sırasıyla sayfalıyor).
        row.LastId.Should()
            .Be(Guid.Parse("00000000-0000-0000-0000-0000000000aa"));
    }

    [Fact]
    public async Task SyncOnceAsync_imlec_sunucunun_teslim_ettigi_son_satirdan_okunur()
    {
        // R3-01: node baytları SQL sırasını belirler: ...0001 < ...0002 (SQL),
        // ama .NET sırasında 00000000-... < ffffffff-... İstemci yeniden
        // sıralarsa imleç sunucu sayfa sınırının gerisinde kalır.
        var sqlSmall = Guid.Parse("ffffffff-ffff-ffff-ffff-000000000001"); // SQL: küçük, .NET: büyük
        var sqlBig   = Guid.Parse("00000000-0000-0000-0000-000000000002"); // SQL: büyük, .NET: küçük

        // Sunucunun teslim sırası (SQL uniqueidentifier): sqlSmall, sqlBig.
        var (svc, _, cursors, _) = Build(_ => FakeHttpMessageHandler.Json(200,
            $$"""
            [{"id":"{{sqlSmall}}","username":"u1","fullName":"Bir","address":"a","submittedAt":"2026-04-30T12:00:00Z"},
             {"id":"{{sqlBig}}","username":"u2","fullName":"İki","address":"a","submittedAt":"2026-04-30T12:00:00Z"}]
            """));

        await svc.SyncOnceAsync();

        cursors.Get(CursorName, TestLicenseKey)!.LastId.Should().Be(sqlBig,
            "imleç sunucunun teslim ettiği SON satır olmalı — .NET Guid sırasıyla yeniden seçilirse " +
            "aynı satırlar tekrar iner");
    }

    [Fact]
    public async Task SyncOnceAsync_sends_both_halves_of_the_cursor()
    {
        var (svc, _, cursors, handler) = Build(_ => FakeHttpMessageHandler.Json(200, "[]"));
        cursors.Upsert(CursorName, TestLicenseKey,
            updatedAt: new DateTimeOffset(2026, 4, 30, 12, 0, 0, TimeSpan.Zero),
            lastId: Guid.Parse("00000000-0000-0000-0000-0000000000cc"));

        await svc.SyncOnceAsync();

        handler.Requests[0].RequestUri!.Query.Should()
            .Contain("since=")
            .And.Contain("sinceId=00000000-0000-0000-0000-0000000000cc");
    }

    /// <summary>R9-D02 geri yükleme sözleşmesi: SyncCursor satırı yoksa baştan
    /// çekim (since parametresi hiç gönderilmez). Eski settings.json imleci
    /// TOHUM OLMAZ — settings yedeğin dışında yaşadığı için ileri kalmış
    /// değeri güncel adres/telefonu sonsuza dek atlatıyordu.</summary>
    [Fact]
    public async Task SyncOnceAsync_imlec_satiri_yoksa_bastan_ceker()
    {
        var (svc, _, _, handler) = Build(_ => FakeHttpMessageHandler.Json(200, "[]"));

        await svc.SyncOnceAsync();

        handler.Requests[0].RequestUri!.Query.Should().NotContain("since=",
            "satır yokken baştan çekim — UpsertPersonFromIntake idempotent");
    }

    /// <summary>R9-D02: lisans anahtarı yokken imleç hangi lisans adına
    /// ilerleyecek bilinemez — hiç başlama (kardeş sync servisleriyle tutarlı).</summary>
    [Fact]
    public async Task SyncOnceAsync_skips_when_no_license_key()
    {
        var (svc, _, _, handler) = Build(_ => FakeHttpMessageHandler.Json(200, "[]"),
            seedLicense: false);

        var count = await svc.SyncOnceAsync();

        count.Should().Be(0);
        handler.Requests.Should().BeEmpty("no HTTP calls when no license key");
    }

    [Fact]
    public async Task SyncOnceAsync_returns_zero_on_network_failure_and_does_not_advance_cursor()
    {
        var initial = new DateTimeOffset(2026, 4, 30, 10, 0, 0, TimeSpan.Zero);
        var (svc, _, cursors, _) = Build(_ => throw new HttpRequestException("dns fail"));
        cursors.Upsert(CursorName, TestLicenseKey, updatedAt: initial, lastId: Guid.Empty);

        var count = await svc.SyncOnceAsync();

        count.Should().Be(0);
        cursors.Get(CursorName, TestLicenseKey)!.UpdatedAt.Should().Be(initial);
    }

    [Fact]
    public async Task SyncOnceAsync_propagates_phone_from_dto_to_customer()
    {
        var (svc, repo, _, _) = Build(_ => FakeHttpMessageHandler.Json(200,
            """[{"id":"00000000-0000-0000-0000-000000000001","username":"alice","fullName":"Alice","address":"Addr","phone":"+905551111111","submittedAt":"2026-04-30T12:00:00Z"}]"""));

        var count = await svc.SyncOnceAsync();

        count.Should().Be(1);
        var customer = repo.Search("alice", limit: 5).Single(c => c.Platform == "form");
        customer.Phone.Should().Be("+905551111111");
    }

    [Fact]
    public async Task SyncOnceAsync_youtube_channelId_merges_into_existing_chat_customer()
    {
        // Chat'ten kaydedilmiş YouTube müşterisi: Username=channelId.
        var (svc, repo, _, _) = Build(_ => FakeHttpMessageHandler.Json(200,
            """[{"id":"00000000-0000-0000-0000-000000000001","username":"UCabc123","fullName":"Sibel G","address":"Ankara","phone":"+905559998877","submittedAt":"2026-04-30T12:00:00Z","youTubeUsername":"sibelg","youTubeChannelId":"UCabc123"}]"""));
        repo.Insert(new OrderDeck.Core.Customers.Customer(
            "yt1", "youtube", "UCabc123", "@sibelg", null,
            100, 100, false, null, null, 2, 180m, null, null, null));

        var count = await svc.SyncOnceAsync();

        count.Should().Be(1);
        // channelId ile birebir eşleşti → AYRI satır açılmadı, geçmiş korundu.
        var yts = repo.GetRecent(1000).Where(c => c.Platform == "youtube").ToList();
        yts.Should().HaveCount(1);
        yts[0].Id.Should().Be("yt1");
        yts[0].Phone.Should().Be("+905559998877");
        yts[0].TotalAmount.Should().Be(180m);
    }

    // ── FullName backfill (tek seferlik geriye-dönük düzeltme) ────────────

    [Fact]
    public async Task BackfillFullNamesOnceAsync_fills_missing_fullname_from_server()
    {
        var (svc, repo, cursors, _) = Build(_ => FakeHttpMessageHandler.Json(200,
            """[{"id":"00000000-0000-0000-0000-000000000001","username":"musaa.sevinc","fullName":"Musa Sevinç","address":"Adr","submittedAt":"2026-04-30T12:00:00Z","instagramUsername":"musaa.sevinc"}]"""));
        // Chat'ten gelmiş IG satırı: DisplayName = takma ad, FullName boş.
        repo.Insert(new OrderDeck.Core.Customers.Customer(
            "ig1", "instagram", "musaa.sevinc", "musaa.sevinc", null,
            100, 100, false, null, null, 0, 0m, null, null, null));

        var updated = await svc.BackfillFullNamesOnceAsync();

        updated.Should().Be(1);
        repo.GetById("ig1")!.FullName.Should().Be("Musa Sevinç");
        repo.GetById("ig1")!.DisplayName.Should().Be("musaa.sevinc"); // dokunulmadı
        // R9-D03: "bitti" işareti = SyncCursor satırı, Seq = sürüm 2.
        cursors.Get(BackfillMarkerName, TestLicenseKey)!.Seq.Should().Be(2);
    }

    [Fact]
    public async Task BackfillFullNamesOnceAsync_is_noop_when_already_done()
    {
        bool called = false;
        var (svc, _, cursors, _) = Build(_ => { called = true; return FakeHttpMessageHandler.Json(200, "[]"); });
        cursors.Upsert(BackfillMarkerName, TestLicenseKey, seq: 2);

        var updated = await svc.BackfillFullNamesOnceAsync();

        updated.Should().Be(0);
        called.Should().BeFalse(); // sunucuya gitmedi
    }

    /// <summary>R9-D03 kontrollü onarım: eski kurulumların "bitti"si settings
    /// bool'undaydı (sürüm 1 sayılır) ve o dönemin imleç hatası işi yarım
    /// bırakmıştı. DB'de satır yokken (veya Seq &lt; 2) backfill YENİDEN koşar.</summary>
    [Fact]
    public async Task BackfillFullNamesOnceAsync_eski_surum_isareti_yeniden_kosturur()
    {
        bool called = false;
        var (svc, _, cursors, _) = Build(_ => { called = true; return FakeHttpMessageHandler.Json(200, "[]"); });
        cursors.Upsert(BackfillMarkerName, TestLicenseKey, seq: 1); // bool dönemi

        await svc.BackfillFullNamesOnceAsync();

        called.Should().BeTrue("sürüm 1 işareti güvenilmez — yarım kalmış olabilir");
        cursors.Get(BackfillMarkerName, TestLicenseKey)!.Seq.Should().Be(2);
    }

    /// <summary>R9-D03 (a): backfill imleci sunucunun teslim ettiği SON
    /// satırdan okunur — .NET Guid sırasıyla yeniden SIRALANMAZ. Eski
    /// OrderBy(...).Last() imleci sunucu sayfa sınırının gerisinde bırakıyor,
    /// döngü aynı satırları çekip 500 sayfa tavanına çarpıyordu.</summary>
    [Fact]
    public async Task BackfillFullNamesOnceAsync_imlec_sunucunun_teslim_ettigi_son_satirdan_okunur()
    {
        var sqlSmall = Guid.Parse("ffffffff-ffff-ffff-ffff-000000000001"); // SQL: küçük, .NET: büyük
        var sqlBig   = Guid.Parse("00000000-0000-0000-0000-000000000002"); // SQL: büyük, .NET: küçük

        var requests = new List<string>();
        var (svc, _, _, _) = Build(req =>
        {
            requests.Add(req.RequestUri!.PathAndQuery);
            if (requests.Count > 1) return FakeHttpMessageHandler.Json(200, "[]");
            // Tam sayfa (100 kayıt) — teslim sırasının SON satırı sqlBig.
            var items = new List<string>();
            for (var i = 0; i < 98; i++)
                items.Add($$"""{"id":"11111111-1111-1111-1111-{{i:D12}}","username":"u{{i}}","fullName":"N","address":"a","submittedAt":"2026-04-30T12:00:00Z"}""");
            items.Add($$"""{"id":"{{sqlSmall}}","username":"x","fullName":"N","address":"a","submittedAt":"2026-04-30T12:00:00Z"}""");
            items.Add($$"""{"id":"{{sqlBig}}","username":"y","fullName":"N","address":"a","submittedAt":"2026-04-30T12:00:00Z"}""");
            return FakeHttpMessageHandler.Json(200, "[" + string.Join(",", items) + "]");
        });

        await svc.BackfillFullNamesOnceAsync();

        requests.Should().HaveCount(2);
        requests[1].Should().Contain($"sinceId={sqlBig}",
            "ikinci istek teslim sırasının SON satırından devam etmeli — " +
            ".NET sırasına göre seçilseydi sqlSmall giderdi ve aynı sayfa tekrar inerdi");
    }

    /// <summary>R9-D03 (b): 500 sayfa tavanına çarpan (veya iptal edilen)
    /// backfill işi YARIMDIR — "bitti" işareti yazılmaz, sonraki açılış
    /// yeniden dener. Eski kod tavana çarpınca bile bool'u true yazıyordu
    /// (denetim: 1000 kayıttan 599'u işlenmiş, kalan 401 sonsuza dek eksik).</summary>
    [Fact]
    public async Task BackfillFullNamesOnceAsync_tavana_carpinca_bitti_isareti_yazilmaz()
    {
        // Her istekte AYNI tam sayfa dönen sunucu — imleç ilerleyemiyor,
        // döngü ancak tavanla durur.
        var items = new List<string>();
        for (var i = 0; i < 100; i++)
            items.Add($$"""{"id":"11111111-1111-1111-1111-{{i:D12}}","username":"u{{i}}","fullName":"N","address":"a","submittedAt":"2026-04-30T12:00:00Z"}""");
        var page = "[" + string.Join(",", items) + "]";
        var (svc, _, cursors, _) = Build(_ => FakeHttpMessageHandler.Json(200, page));

        await svc.BackfillFullNamesOnceAsync();

        cursors.Get(BackfillMarkerName, TestLicenseKey).Should().BeNull(
            "tavan çıkışı = iş yarım; işaret yazılırsa kalan satırlar sonsuza dek eksik kalır");
    }

    // ── UI freeze fix #1 (2026-05-13): auth failure flag ──────────────────

    [Fact]
    public async Task SyncOnceAsync_sets_LastSyncWasAuthFailure_on_401()
    {
        var (svc, _, _, _) = Build(_ => FakeHttpMessageHandler.Json(401,
            "{\"error\":\"invalid_credentials\",\"message\":\"E-posta veya şifre yanlış\"}"));

        await svc.SyncOnceAsync();

        svc.LastSyncWasAuthFailure.Should().BeTrue();
    }

    [Fact]
    public async Task SyncOnceAsync_clears_LastSyncWasAuthFailure_on_success()
    {
        bool firstCall = true;
        Func<HttpRequestMessage, HttpResponseMessage> responder = _ =>
        {
            if (firstCall)
            {
                firstCall = false;
                return FakeHttpMessageHandler.Json(401,
                    "{\"error\":\"invalid_credentials\",\"message\":\"err\"}");
            }
            return FakeHttpMessageHandler.Json(200, "[]");
        };
        var (svc, _, _, _) = Build(responder);

        await svc.SyncOnceAsync();
        svc.LastSyncWasAuthFailure.Should().BeTrue();

        await svc.SyncOnceAsync();
        svc.LastSyncWasAuthFailure.Should().BeFalse();
    }

    [Fact]
    public async Task SyncOnceAsync_does_not_set_auth_flag_on_network_failure()
    {
        // Generic HttpRequestException ≠ auth failure; flag false kalmalı
        var (svc, _, _, _) = Build(_ => throw new HttpRequestException("dns fail"));

        try { await svc.SyncOnceAsync(); }
        catch { /* network errors propagate, OK */ }

        svc.LastSyncWasAuthFailure.Should().BeFalse();
    }
}
