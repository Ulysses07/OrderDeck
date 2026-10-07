using Dapper;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using OrderDeck.App.Services.IntakeForm;
using OrderDeck.App.Services.Sync;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Core.Time;
using OrderDeck.Licensing.Api;
using OrderDeck.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.Tests.Services.IntakeForm;

/// <summary>U14: imleçsiz başlayan ilk tam form oynatması doldurma kipinde ve ilk tam akıştan sonra;
/// oynatma sürerken başlangıcından (T0) sonra gönderilen form damgalı; form işleme her iki kipte de
/// ilk tam müşteri akışını bekler (C2 kalite incelemesi).</summary>
public sealed class IntakeFormReplayTests
{
    private sealed class Clock : IClock
    {
        public long Now { get; set; }
        public long UnixNow() => Now;
    }

    private sealed class License : ICurrentLicenseProvider { public string? CurrentLicenseKey { get; set; } }

    private sealed class RecordingLogger : ILogger<IntakeFormSyncService>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (Entries) Entries.Add((logLevel, formatter(state, exception)));
        }
    }

    private const string ReplayMarker = "intake-form-replay";
    private static readonly string Lisans = $"lisans-{Guid.NewGuid():N}";

    /// <summary>Oynatmanın başladığı an (T0): eski formlar (varsayılan 2026-04-30) bundan önce.</summary>
    private static readonly DateTimeOffset ReplayStart = DateTimeOffset.Parse("2026-10-07T00:00:00Z");
    private const string AfterStart = "2026-10-07T12:00:00Z";

    private sealed record Fx(IntakeFormSyncService Svc, CustomerRepository Customers, SyncCursorRepository Cursors,
        SyncStatusTracker Tracker, InMemorySqlite Db, FakeHttpMessageHandler Http, Clock Clock, RecordingLogger Log);

    /// <param name="pages">Sırayla dönen form sayfaları (JSON dizi); bitince "[]".</param>
    private static Fx Build(params string[] pages)
    {
        var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        return Start(db, new Clock { Now = ReplayStart.ToUnixTimeSeconds() }, pages);
    }

    /// <summary>Aynı veritabanında yeni süreç (yeniden başlatma): yeni servis, yeni izleyici.</summary>
    private static Fx Start(InMemorySqlite db, Clock clock, params string[] pages)
    {
        var customers = new CustomerRepository(db);
        var cursors = new SyncCursorRepository(db);
        var served = 0;
        var http = new FakeHttpMessageHandler(_ =>
            FakeHttpMessageHandler.Json(200, served < pages.Length ? pages[served++] : "[]"));
        var api = new LicenseApiClient(new HttpClient(http) { BaseAddress = new Uri("https://test.local") }, new LicenseTokenStore());
        var tracker = new SyncStatusTracker();
        var log = new RecordingLogger();
        var svc = new IntakeFormSyncService(api, customers, cursors, new License { CurrentLicenseKey = Lisans },
            clock, log, tracker);
        return new Fx(svc, customers, cursors, tracker, db, http, clock, log);
    }

    private static string Item(string user, string phone, string submittedAt = "2026-04-30T12:00:00Z")
        => $$"""{"id":"{{Guid.NewGuid()}}","username":"{{user}}","fullName":"Örnek Müşteri","address":"Adres","phone":"{{phone}}","submittedAt":"{{submittedAt}}","instagramUsername":"{{user}}"}""";

    private static string Page(params string[] items) => "[" + string.Join(",", items) + "]";

    private static string Form(string user, string phone, string submittedAt = "2026-04-30T12:00:00Z")
        => Page(Item(user, phone, submittedAt));

    private static long Ms(string at) => DateTimeOffset.Parse(at).ToUnixTimeMilliseconds();

    private static void CaughtUp(Fx fx) => fx.Tracker.MarkPullSucceeded(DateTimeOffset.UtcNow, Lisans);

    private static string LegacyRow(Fx fx, string user, string phone)
    {
        // Göç öncesi elle girilmiş telefon: damgasız.
        var id = Guid.NewGuid().ToString("N");
        using var scope = SyncApplyScope.Begin(fx.Db);
        scope.Execute(@"INSERT INTO Customer (Id, Platform, Username, IdentityKey, DisplayName, FirstSeenAt, LastSeenAt, Phone)
                        VALUES (@id, 'instagram', @user, @key, @user, 1, 1, @phone)",
            new { id, user, key = CustomerIdentity.KeyOrNull(user), phone });
        scope.Commit();
        return id;
    }

    private static long? PhoneStamp(Fx fx, string id)
    {
        using var c = fx.Db.Open();
        return c.ExecuteScalar<long?>("SELECT PhoneChangedAt FROM Customer WHERE Id = @id", new { id });
    }

    [Fact]
    public async Task Ilk_oynatma_ilk_tam_akistan_once_baslamaz()
    {
        var fx = Build(Form("ayse_y", TestPhone.NewE164()));

        (await fx.Svc.SyncOnceAsync()).Should().Be(0);

        fx.Http.Requests.Should().BeEmpty("önce sunucu gerçeği inmeli (IsInitialCatchUpDone)");
        var marker = fx.Cursors.Get(ReplayMarker, Lisans)!;
        marker.Seq.Should().Be(1);
        marker.UpdatedAt.Should().Be(ReplayStart, "oynatmanın başlangıç anı (T0) işaretle saklanır");
    }

    [Fact]
    public async Task Ilk_oynatma_doldurma_kipinde_biter_sonra_damgali_kipe_gecer()
    {
        var legacyPhone = TestPhone.NewE164();
        var newPhone = TestPhone.NewE164();
        var fx = Build(Form("ayse_y", TestPhone.NewE164()), "[]", Form("ayse_y", newPhone, "2026-10-08T12:00:00Z"));
        var id = LegacyRow(fx, "ayse_y", legacyPhone);
        CaughtUp(fx);

        // Oynatma sürerken tur birden çok sayfa çeker: eski form (doldurma) + boş sayfa (bitti).
        await fx.Svc.SyncOnceAsync();
        fx.Customers.GetById(id)!.Phone.Should().Be(legacyPhone, "eski form damgasız dolu değeri ezmez");
        PhoneStamp(fx, id).Should().BeNull();
        fx.Cursors.Get(ReplayMarker, Lisans)!.Seq.Should().Be(2);

        await fx.Svc.SyncOnceAsync();                          // oynatmadan sonraki form: damgalı (kural 3)
        fx.Customers.GetById(id)!.Phone.Should().Be(newPhone);
        PhoneStamp(fx, id).Should().Be(Ms("2026-10-08T12:00:00Z"));
    }

    [Fact]
    public async Task Imleci_olan_kurulumda_oynatma_kipi_yoktur()
    {
        var fx = Build(Form("ayse_y", TestPhone.NewE164()));
        fx.Cursors.Upsert("intake-form-in", Lisans, updatedAt: DateTimeOffset.UtcNow.AddDays(-1), lastId: Guid.NewGuid());
        var id = LegacyRow(fx, "ayse_y", TestPhone.NewE164());
        CaughtUp(fx);

        await fx.Svc.SyncOnceAsync();                          // güncellenen kurulum: oynatma yok, damgalı kip

        fx.Cursors.Get(ReplayMarker, Lisans)!.Seq.Should().Be(2);
        PhoneStamp(fx, id).Should().NotBeNull("kural 3: damgasız yerel birim formun damgasıyla yazılır");
    }

    [Fact]
    public async Task Damgali_kipte_de_form_isleme_ilk_tam_akisi_bekler()
    {
        // C2 kalite incelemesi: sırayla kullanılan bilgisayar, başka bilgisayarın aynı form için
        // gönderdiği (eşit damgalı) sonucu indirmeden formu kendi yerel durumuyla işlemesin.
        var fx = Build(Form("ayse_y", TestPhone.NewE164()));
        fx.Cursors.Upsert("intake-form-in", Lisans, updatedAt: DateTimeOffset.UtcNow.AddDays(-1), lastId: Guid.NewGuid());
        var id = LegacyRow(fx, "ayse_y", TestPhone.NewE164());

        (await fx.Svc.SyncOnceAsync()).Should().Be(0);

        fx.Http.Requests.Should().BeEmpty("damgalı kipte de önce akış yetişir");
        fx.Cursors.Get(ReplayMarker, Lisans)!.Seq.Should().Be(2, "kip yerel imleçten belli — oynatma yok");
        PhoneStamp(fx, id).Should().BeNull();

        CaughtUp(fx);
        (await fx.Svc.SyncOnceAsync()).Should().Be(1);
        PhoneStamp(fx, id).Should().NotBeNull();
    }

    [Fact]
    public async Task Baska_lisansin_yetismesi_bu_lisansin_formlarini_baslatmaz()
    {
        // Yetişme lisansa bağlı: lisans değiştiğinde akış servisi değişimi görmeden (≤ bir akış turu)
        // form turu gelirse yeni lisansın oynatması önceki lisansın yetişmesiyle başlamamalı.
        var fx = Build(Form("ayse_y", TestPhone.NewE164()));
        fx.Tracker.MarkPullSucceeded(DateTimeOffset.UtcNow, $"lisans-{Guid.NewGuid():N}");

        (await fx.Svc.SyncOnceAsync()).Should().Be(0);
        (await fx.Svc.BackfillFullNamesOnceAsync()).Should().Be(0);

        fx.Http.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Oynatma_surerken_T0_sonrasi_form_damgali_uygulanir_eskisi_yalniz_doldurur()
    {
        // Sunucu formdan müşteri kaydı türetmez: tek bilgisayarlı lisansta oynatma sürerken gelen yeni
        // form (geri dönen müşterinin yeni telefonu) yalnız doldurulsaydı yeni değer kalıcı kaybolurdu.
        // T0'dan sonra gönderilen form oynatma öncesindeki her değerden yenidir → damgalı (kural 3).
        var oldPhone = TestPhone.NewE164();
        var newPhone = TestPhone.NewE164();
        var keptPhone = TestPhone.NewE164();
        var fx = Build(Page(
            Item("ayse_y", TestPhone.NewE164()),
            Item("fatma_k", TestPhone.NewE164(), "2026-05-01T12:00:00Z"),
            Item("ayse_y", newPhone, AfterStart)));
        var returning = LegacyRow(fx, "ayse_y", oldPhone);
        var older = LegacyRow(fx, "fatma_k", keptPhone);
        var badge = new List<int>();
        fx.Svc.SubmissionsSynced += (_, n) => badge.Add(n);
        (await fx.Svc.SyncOnceAsync()).Should().Be(0, "akış yetişmeden oynatma başlamaz — T0 burada yazılır");
        CaughtUp(fx);

        (await fx.Svc.SyncOnceAsync()).Should().Be(3);

        fx.Customers.GetById(returning)!.Phone.Should().Be(newPhone, "T0'dan sonraki form damgalı yazar");
        PhoneStamp(fx, returning).Should().Be(Ms(AfterStart));
        fx.Customers.GetById(older)!.Phone.Should().Be(keptPhone, "T0'dan önceki form yalnız boşu doldurur");
        PhoneStamp(fx, older).Should().BeNull();
        fx.Customers.GetById(older)!.FullName.Should().Be("Örnek Müşteri");
        badge.Should().Equal(new[] { 1 }, "'bu oturumda yeni' rozeti yalnız damgalı (yeni) formu sayar");
    }

    [Fact]
    public async Task Yeniden_baslatmada_oynatmanin_T0_i_korunur()
    {
        var first = Build();
        (await first.Svc.SyncOnceAsync()).Should().Be(0);     // akış bekleniyor; T0 yazıldı
        var newPhone = TestPhone.NewE164();
        var laterClock = new Clock { Now = ReplayStart.AddDays(2).ToUnixTimeSeconds() };
        var fx = Start(first.Db, laterClock, Form("ayse_y", newPhone, ReplayStart.AddDays(1).ToString("O")));
        var id = LegacyRow(fx, "ayse_y", TestPhone.NewE164());
        CaughtUp(fx);

        await fx.Svc.SyncOnceAsync();

        fx.Cursors.Get(ReplayMarker, Lisans)!.UpdatedAt.Should().Be(ReplayStart);
        fx.Customers.GetById(id)!.Phone.Should().Be(newPhone,
            "form ilk T0'dan sonra gönderildi — T0 yeniden başlatmada 'şimdi'ye kaysaydı yalnız doldururdu");
    }

    [Fact]
    public async Task Yarida_kalan_oynatma_imlec_olsa_da_doldurma_kipinde_surer()
    {
        // İşaret 1 imleçten ÖNCE denetlenir: oynatmanın kendisi imleci ilerletir.
        var fx = Build(Form("ayse_y", TestPhone.NewE164()));
        fx.Cursors.Upsert(ReplayMarker, Lisans, seq: 1, updatedAt: ReplayStart);
        fx.Cursors.Upsert("intake-form-in", Lisans, updatedAt: DateTimeOffset.Parse("2026-04-01T00:00:00Z"), lastId: Guid.NewGuid());
        var legacyPhone = TestPhone.NewE164();
        var id = LegacyRow(fx, "ayse_y", legacyPhone);
        CaughtUp(fx);

        await fx.Svc.SyncOnceAsync();

        fx.Customers.GetById(id)!.Phone.Should().Be(legacyPhone);
        PhoneStamp(fx, id).Should().BeNull();
    }

    [Fact]
    public async Task Oynatma_surerken_turda_en_cok_bes_sayfa_ceker_bittikten_sonra_tek_sayfa()
    {
        var pages = Enumerable.Range(0, 6).Select(i => Form($"kisi_{i}", TestPhone.NewE164())).ToArray();
        var fx = Build(pages);
        CaughtUp(fx);

        (await fx.Svc.SyncOnceAsync()).Should().Be(5);
        fx.Http.Requests.Should().HaveCount(5, "tur başına sayfa sınırı (hız sınırı)");
        fx.Cursors.Get(ReplayMarker, Lisans)!.Seq.Should().Be(1);

        (await fx.Svc.SyncOnceAsync()).Should().Be(1);
        fx.Http.Requests.Should().HaveCount(7, "kalan sayfa + oynatmayı bitiren boş sayfa");
        fx.Cursors.Get(ReplayMarker, Lisans)!.Seq.Should().Be(2);

        await fx.Svc.SyncOnceAsync();
        fx.Http.Requests.Should().HaveCount(8, "oynatma bittikten sonra tur başına tek sayfa");
    }

    [Fact]
    public async Task Akisi_on_dakikadan_uzun_beklemek_bir_kez_bilgi_gunlugune_yazilir()
    {
        var fx = Build();
        int Waits() => fx.Log.Entries.Count(e => e.Level == LogLevel.Information && e.Message.Contains("bekliyor"));

        await fx.Svc.SyncOnceAsync();
        fx.Clock.Now += 9 * 60;
        await fx.Svc.BackfillFullNamesOnceAsync();
        Waits().Should().Be(0);

        fx.Clock.Now += 2 * 60;
        await fx.Svc.SyncOnceAsync();
        fx.Clock.Now += 10 * 60;
        await fx.Svc.SyncOnceAsync();
        await fx.Svc.BackfillFullNamesOnceAsync();

        Waits().Should().Be(1, "uzun bekleme bir kez görünür olur, her turda yinelenmez");
    }

    [Fact]
    public async Task Backfill_ilk_tam_akistan_once_baslamaz_sonra_doldurma_kipinde_kosar()
    {
        // Taze bilgisayarda backfill de doldurma kipinde (boş + damgasız ad, damgasız) ve ilk tam
        // akıştan sonra; beklerken "bitti" işareti yazılmaz → arka plan işi sonraki turda yeniden dener.
        var fx = Build(Form("ayse_y", TestPhone.NewE164()));
        var id = LegacyRow(fx, "ayse_y", TestPhone.NewE164());

        (await fx.Svc.BackfillFullNamesOnceAsync()).Should().Be(0);
        fx.Http.Requests.Should().BeEmpty();
        fx.Cursors.Get("intake-fullname-backfill", Lisans).Should().BeNull("beklerken işaret yazılmaz");

        CaughtUp(fx);
        (await fx.Svc.BackfillFullNamesOnceAsync()).Should().Be(1);

        fx.Customers.GetById(id)!.FullName.Should().Be("Örnek Müşteri");
        using var c = fx.Db.Open();
        c.ExecuteScalar<long?>("SELECT FullNameChangedAt FROM Customer WHERE Id = @id", new { id })
            .Should().BeNull("oynatma sürerken T0'dan önceki form backfill'de de damga yazmaz");
        fx.Cursors.Get("intake-fullname-backfill", Lisans)!.Seq.Should().Be(2);
    }
}
