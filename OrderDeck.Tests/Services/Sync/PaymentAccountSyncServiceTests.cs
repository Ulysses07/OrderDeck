using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using OrderDeck.App.Services.Sync;
using OrderDeck.Core.Settings;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Licensing.Api;
using OrderDeck.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.Tests.Services.Sync;

public sealed class PaymentAccountSyncServiceTests
{
    private sealed class FakeLicenseProvider : ICurrentLicenseProvider
    {
        public string? CurrentLicenseKey { get; set; }
    }

    private static readonly Guid TestLicenseId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private const string TestLicenseKey = "PAY-ACCT-TEST-KEY";

    private static string LicensesJson() =>
        $"[{{\"id\":\"{TestLicenseId}\",\"licenseKey\":\"{TestLicenseKey}\"}}]";

    private sealed record Fixture(
        PaymentAccountSyncService Svc,
        SettingsStore Store,
        AppSettings Settings,
        FakeLicenseProvider License,
        InMemorySqlite Db,
        List<(HttpMethod Method, string Path, string? Body)> Requests);

    private static Fixture Build(
        Func<HttpRequestMessage, HttpResponseMessage> responder,
        bool seedLicense = true)
    {
        var settingsPath = Path.Combine(Path.GetTempPath(), $"pa-settings-{Guid.NewGuid():N}.json");
        var store = new SettingsStore(settingsPath);
        var settings = store.Load();

        var db = new InMemorySqlite();
        new MigrationRunner(db).Run();

        var licenseProvider = new FakeLicenseProvider();
        if (seedLicense) licenseProvider.CurrentLicenseKey = TestLicenseKey;

        var (svc, requests) = NewInstance(responder, store, licenseProvider, db);
        return new Fixture(svc, store, settings, licenseProvider, db, requests);
    }

    /// <summary>
    /// "Uygulama yeniden başladı" simülasyonu: aynı ayar dosyası ve aynı
    /// kalıcı depo üzerinde, süreç belleği sıfırlanmış TAZE bir servis örneği.
    /// </summary>
    private static Fixture Restart(
        Fixture previous, Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var licenseProvider = new FakeLicenseProvider
        {
            CurrentLicenseKey = previous.License.CurrentLicenseKey
        };
        var (svc, requests) = NewInstance(responder, previous.Store, licenseProvider, previous.Db);
        return previous with { Svc = svc, License = licenseProvider, Requests = requests };
    }

    private static (PaymentAccountSyncService Svc, List<(HttpMethod, string, string?)> Requests)
        NewInstance(
            Func<HttpRequestMessage, HttpResponseMessage> responder,
            SettingsStore store, FakeLicenseProvider licenseProvider, InMemorySqlite db)
    {
        var requests = new List<(HttpMethod, string, string?)>();
        var handler = new FakeHttpMessageHandler(req =>
        {
            var body = req.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
            requests.Add((req.Method, req.RequestUri!.PathAndQuery, body));
            return responder(req);
        });
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://test.local") };
        var api  = new LicenseApiClient(http, new LicenseTokenStore());

        var svc = new PaymentAccountSyncService(
            api, store, licenseProvider, new PaymentAccountSyncStateRepository(db),
            NullLogger<PaymentAccountSyncService>.Instance);
        return (svc, requests);
    }

    // Helper: wire responder that serves /me/licenses then 204 for payment-account
    private static HttpResponseMessage DefaultResponder(HttpRequestMessage req)
    {
        var path = req.RequestUri!.PathAndQuery;
        if (path.StartsWith("/api/v1/me/licenses"))
            return FakeHttpMessageHandler.Json(200, LicensesJson());
        if (path.Contains("/payment-account"))
            return FakeHttpMessageHandler.Empty(204);
        return FakeHttpMessageHandler.Empty(404);
    }

    [Fact]
    public async Task Sync_no_license_skips()
    {
        var fx = Build(_ => FakeHttpMessageHandler.Json(200, LicensesJson()), seedLicense: false);

        await fx.Svc.SyncIfChangedAsync(CancellationToken.None);

        fx.Requests.Should().BeEmpty("no HTTP calls when no license key");
    }

    [Fact]
    public async Task Sync_first_call_sends_values()
    {
        var fx = Build(DefaultResponder);
        var settings = fx.Store.Load();
        settings.Payment.Iban          = "TR330006100519786457841326";
        settings.Payment.AccountHolder = "Ahmet Yıldız";
        fx.Store.Save(settings);

        await fx.Svc.SyncIfChangedAsync(CancellationToken.None);

        fx.Requests.Should().Contain(r =>
            r.Path.Contains("/payment-account") && r.Method == HttpMethod.Post,
            "should POST to payment-account endpoint");
    }

    [Fact]
    public async Task Sync_no_change_does_not_call_api_again()
    {
        var fx = Build(DefaultResponder);
        var settings = fx.Store.Load();
        settings.Payment.Iban          = "TR330006100519786457841326";
        settings.Payment.AccountHolder = "Ahmet Yıldız";
        fx.Store.Save(settings);

        // First call → sends
        await fx.Svc.SyncIfChangedAsync(CancellationToken.None);
        var afterFirst = fx.Requests.Count;

        // Second call with identical values → no-op
        await fx.Svc.SyncIfChangedAsync(CancellationToken.None);

        fx.Requests.Should().HaveCount(afterFirst, "no new requests when values unchanged");
    }

    [Fact]
    public async Task Sync_change_sends_new_values()
    {
        var postCount = 0;
        var fx = Build(req =>
        {
            var path = req.RequestUri!.PathAndQuery;
            if (path.StartsWith("/api/v1/me/licenses"))
                return FakeHttpMessageHandler.Json(200, LicensesJson());
            if (path.Contains("/payment-account"))
            {
                Interlocked.Increment(ref postCount);
                return FakeHttpMessageHandler.Empty(204);
            }
            return FakeHttpMessageHandler.Empty(404);
        });

        var s1 = fx.Store.Load();
        s1.Payment.Iban = "TR330006100519786457841326";
        fx.Store.Save(s1);
        await fx.Svc.SyncIfChangedAsync(CancellationToken.None);

        var s2 = fx.Store.Load();
        s2.Payment.Iban = "TR440006100519786457841327";
        fx.Store.Save(s2);
        await fx.Svc.SyncIfChangedAsync(CancellationToken.None);

        postCount.Should().Be(2, "two POSTs: one for each distinct IBAN");
    }

    [Fact]
    public async Task Sync_whitespace_iban_treated_as_null_and_equals_initial_state_so_noop()
    {
        // Blank IBAN/holder normalise to null. The service's initial in-memory cache is
        // also null/null, so there is no change → no POST on the first call.
        var fx = Build(DefaultResponder);

        var settings = fx.Store.Load();
        settings.Payment.Iban          = "   ";
        settings.Payment.AccountHolder = "  ";
        fx.Store.Save(settings);

        await fx.Svc.SyncIfChangedAsync(CancellationToken.None);

        // Only the /me/licenses call may fire (for license resolution), but
        // no payment-account POST should occur because null == null.
        fx.Requests.Should().NotContain(r => r.Path.Contains("/payment-account"),
            "whitespace values normalise to null which matches initial null cache → no-op");
    }

    [Fact]
    public async Task Sync_whitespace_iban_after_real_value_sends_null()
    {
        // First sync with a real IBAN, then overwrite with whitespace.
        // Service must detect the change (real → null) and POST with null iban.
        string? capturedBody = null;
        var postCount = 0;
        var fx = Build(req =>
        {
            var path = req.RequestUri!.PathAndQuery;
            if (path.StartsWith("/api/v1/me/licenses"))
                return FakeHttpMessageHandler.Json(200, LicensesJson());
            if (path.Contains("/payment-account"))
            {
                Interlocked.Increment(ref postCount);
                if (postCount == 2)
                    capturedBody = req.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
                return FakeHttpMessageHandler.Empty(204);
            }
            return FakeHttpMessageHandler.Empty(404);
        });

        var s1 = fx.Store.Load();
        s1.Payment.Iban = "TR330006100519786457841326";
        fx.Store.Save(s1);
        await fx.Svc.SyncIfChangedAsync(CancellationToken.None); // first POST: real IBAN

        var s2 = fx.Store.Load();
        s2.Payment.Iban = "   "; // overwrite with whitespace
        fx.Store.Save(s2);
        await fx.Svc.SyncIfChangedAsync(CancellationToken.None); // second POST: null iban

        postCount.Should().Be(2, "change from real to whitespace (null) must trigger a new POST");
        capturedBody.Should().NotBeNull();
        capturedBody!.Should().Contain("null",
            "whitespace IBAN should be serialised as null in the request body");
    }

    [Fact]
    public async Task Lisans_degisince_ayni_degerler_yeni_hedefe_de_gonderilir()
    {
        // R10-D03: "değişti mi?" önbelleği yalnız değerlerden oluşuyordu
        // (_lastSyncedIban/_lastSyncedAccountHolder) — hedef lisans kimliği
        // içinde yoktu. A lisansına gönderilmiş IBAN, hedef B'ye geçince de
        // "değişmedi" sayılıyor ve B hiç POST almıyordu. Önbellek kimliği
        // hedef lisansı da içermeli.
        var licenseIdB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        const string licenseKeyB = "PAY-ACCT-TEST-KEY-B";
        var twoLicensesJson =
            $"[{{\"id\":\"{TestLicenseId}\",\"licenseKey\":\"{TestLicenseKey}\"}}," +
            $"{{\"id\":\"{licenseIdB}\",\"licenseKey\":\"{licenseKeyB}\"}}]";

        var fx = Build(req =>
        {
            var path = req.RequestUri!.PathAndQuery;
            if (path.StartsWith("/api/v1/me/licenses"))
                return FakeHttpMessageHandler.Json(200, twoLicensesJson);
            if (path.Contains("/payment-account"))
                return FakeHttpMessageHandler.Empty(204);
            return FakeHttpMessageHandler.Empty(404);
        });

        var settings = fx.Store.Load();
        settings.Payment.Iban          = "TR330006100519786457841326";
        settings.Payment.AccountHolder = "Ahmet Yıldız";
        fx.Store.Save(settings);

        // A hedefine ilk gönderim
        await fx.Svc.SyncIfChangedAsync(CancellationToken.None);
        fx.Requests.Should().Contain(r =>
            r.Method == HttpMethod.Post && r.Path.Contains($"/{TestLicenseId}/payment-account"),
            "ilk tur mevcut hedefe göndermeli");

        // Hedef lisans değişti; değerler aynı
        fx.License.CurrentLicenseKey = licenseKeyB;
        await fx.Svc.SyncIfChangedAsync(CancellationToken.None);

        fx.Requests.Should().Contain(r =>
            r.Method == HttpMethod.Post && r.Path.Contains($"/{licenseIdB}/payment-account"),
            "yeni hedef B, aynı değerlerle de olsa hesabı almalı — değer önbelleği hedefe bağlı olmalı");
    }

    [Fact]
    public async Task Temizleme_niyeti_yeniden_baslatmayi_atlatir()
    {
        // R10-D04: operatör IBAN+hesap sahibini boşalttı, 5 dakikalık tur
        // gelmeden uygulama yeniden başladı. Taze servis örneğinin önbelleği
        // null/null; ayarlar da null/null → null==null → POST yok, uzaktaki
        // hesap dolu kalıyordu. "Sunucuyla hiç karşılaştırılmadı" ile "boş
        // değer başarıyla gönderildi" ayrımı süreç belleğinde yaşayamaz.
        string? lastBody = null;
        Func<HttpRequestMessage, HttpResponseMessage> responder = req =>
        {
            var path = req.RequestUri!.PathAndQuery;
            if (path.StartsWith("/api/v1/me/licenses"))
                return FakeHttpMessageHandler.Json(200, LicensesJson());
            if (path.Contains("/payment-account"))
            {
                lastBody = req.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
                return FakeHttpMessageHandler.Empty(204);
            }
            return FakeHttpMessageHandler.Empty(404);
        };

        var fx = Build(responder);
        var s1 = fx.Store.Load();
        s1.Payment.Iban          = "TR330006100519786457841326";
        s1.Payment.AccountHolder = "Ahmet Yıldız";
        fx.Store.Save(s1);
        await fx.Svc.SyncIfChangedAsync(CancellationToken.None); // sunucu artık dolu

        // Operatör hesabı boşaltır…
        var s2 = fx.Store.Load();
        s2.Payment.Iban          = "";
        s2.Payment.AccountHolder = "";
        fx.Store.Save(s2);
        // …ve tur gelmeden uygulama yeniden başlar.
        var restarted = Restart(fx, responder);

        await restarted.Svc.SyncIfChangedAsync(CancellationToken.None);

        restarted.Requests.Should().Contain(r =>
            r.Method == HttpMethod.Post && r.Path.Contains("/payment-account"),
            "temizleme niyeti kalıcı olmalı — restart onu unutturamaz");
        lastBody.Should().NotBeNull();
        lastBody!.Should().Contain("null", "boşaltma sunucuya null olarak gitmeli");
    }

    [Fact]
    public async Task Yaniti_kaybolan_gonderimden_sonra_bosaltma_niyeti_atlanmaz()
    {
        // R11-D02: kayıt yalnız BAŞARIDA yazıldığı için, sunucuya ULAŞAN ama
        // yanıtı kaybolan ilk gönderimden sonra hiç satır oluşmuyordu. Operatör
        // sonra hesabı boşaltıp uygulamayı yeniden başlatınca "kayıt yok + yerel
        // boş" dalına düşülüp ATLANIYOR, kaldırılmak istenen hesap sunucuda
        // kalıyordu — üstelik dekont fraud kontrolü o hesabı karşılaştırmaya
        // devam ediyor.
        //
        // Gönderimin sonucu BİLİNMİYORSA bu, "hiç denenmedi" ile aynı şey
        // değildir: denemenin kendisi kalıcılaşmalı ki sonraki tur kararı
        // atlamak yerine yeniden göndermek olsun.
        string? lastBody = null;
        var loseFirstResponse = true;
        Func<HttpRequestMessage, HttpResponseMessage> responder = req =>
        {
            var path = req.RequestUri!.PathAndQuery;
            if (path.StartsWith("/api/v1/me/licenses"))
                return FakeHttpMessageHandler.Json(200, LicensesJson());
            if (path.Contains("/payment-account"))
            {
                if (loseFirstResponse)
                {
                    loseFirstResponse = false;
                    // Sunucu isteği işledi; yanıt dönüş yolunda kayboldu.
                    throw new HttpRequestException("yanıt kayboldu");
                }
                lastBody = req.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
                return FakeHttpMessageHandler.Empty(204);
            }
            return FakeHttpMessageHandler.Empty(404);
        };

        var fx = Build(responder);
        var s1 = fx.Store.Load();
        s1.Payment.Iban          = "TR330006100519786457841326";
        s1.Payment.AccountHolder = "Ahmet Yıldız";
        fx.Store.Save(s1);
        await fx.Svc.SyncIfChangedAsync(CancellationToken.None);

        // Operatör vazgeçip hesabı boşaltır, tur gelmeden uygulama yeniden başlar.
        var s2 = fx.Store.Load();
        s2.Payment.Iban          = "";
        s2.Payment.AccountHolder = "";
        fx.Store.Save(s2);
        var restarted = Restart(fx, responder);

        await restarted.Svc.SyncIfChangedAsync(CancellationToken.None);

        restarted.Requests.Should().Contain(r =>
            r.Method == HttpMethod.Post && r.Path.Contains("/payment-account"),
            "sonucu bilinmeyen bir gönderimden sonra atlamak, sunucudaki hesabı " +
            "operatörün niyetine aykırı olarak ayakta bırakır");
        lastBody.Should().NotBeNull();
        lastBody!.Should().Contain("null", "boşaltma sunucuya null olarak gitmeli");
    }

    [Fact]
    public async Task Yaniti_kaybolan_gonderim_degerler_aynı_kalsa_da_tekrarlanir()
    {
        // R11-D02'nin aynası: yanıt kaybolduktan sonra operatör hiçbir şey
        // değiştirmezse de gönderim tekrarlanmalı. Belirsiz kayıt karşılaştırma
        // tabanı olarak kullanılırsa (yerel == kaydedilen → "değişiklik yok")
        // sunucu hesabı HİÇ almaz ve dekont fraud kontrolü karşılaştıracak
        // hesap bulamaz. Belirsizlikte karar her zaman "yeniden gönder".
        var postCount = 0;
        Func<HttpRequestMessage, HttpResponseMessage> responder = req =>
        {
            var path = req.RequestUri!.PathAndQuery;
            if (path.StartsWith("/api/v1/me/licenses"))
                return FakeHttpMessageHandler.Json(200, LicensesJson());
            if (path.Contains("/payment-account"))
            {
                Interlocked.Increment(ref postCount);
                if (postCount == 1) throw new HttpRequestException("yanıt kayboldu");
                return FakeHttpMessageHandler.Empty(204);
            }
            return FakeHttpMessageHandler.Empty(404);
        };

        var fx = Build(responder);
        var settings = fx.Store.Load();
        settings.Payment.Iban          = "TR330006100519786457841326";
        settings.Payment.AccountHolder = "Ahmet Yıldız";
        fx.Store.Save(settings);

        await fx.Svc.SyncIfChangedAsync(CancellationToken.None);   // ulaştı mı? bilinmiyor
        var restarted = Restart(fx, responder);                     // değerler aynı
        await restarted.Svc.SyncIfChangedAsync(CancellationToken.None);

        postCount.Should().Be(2,
            "sonucu bilinmeyen gönderim doğrulanmış taban sayılamaz; aynı " +
            "değerler yeniden gönderilmeli (POST idempotent)");
    }

    [Fact]
    public async Task Taze_profil_dolu_uzak_hesabi_korlemesine_silmez()
    {
        // AC46 (zorunlu karşıt kontrol): hiç yapılandırılmamış taze kurulum —
        // kalıcı durum kaydı YOK, ayarlar boş — meşru şekilde yapılandırılmış
        // uzak hesabı null-POST'la silmemeli. "Kayıt yok" ≠ "boşaltıldı".
        var fx = Build(DefaultResponder);

        await fx.Svc.SyncIfChangedAsync(CancellationToken.None);
        await fx.Svc.SyncIfChangedAsync(CancellationToken.None);

        fx.Requests.Should().NotContain(r => r.Path.Contains("/payment-account"),
            "bilinmeyen sunucu durumu + boş yerel değer → dokunma");
    }

    [Fact]
    public async Task Sync_api_failure_does_not_throw()
    {
        var fx = Build(req =>
        {
            var path = req.RequestUri!.PathAndQuery;
            if (path.StartsWith("/api/v1/me/licenses"))
                return FakeHttpMessageHandler.Json(200, LicensesJson());
            return FakeHttpMessageHandler.Empty(500);
        });

        var settings = fx.Store.Load();
        settings.Payment.Iban = "TR330006100519786457841326";
        fx.Store.Save(settings);

        // Must not throw — service logs warning and swallows
        var act = async () => await fx.Svc.SyncIfChangedAsync(CancellationToken.None);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Sync_api_failure_does_not_update_last_synced_values()
    {
        var postCount = 0;
        var fx = Build(req =>
        {
            var path = req.RequestUri!.PathAndQuery;
            if (path.StartsWith("/api/v1/me/licenses"))
                return FakeHttpMessageHandler.Json(200, LicensesJson());
            if (path.Contains("/payment-account"))
            {
                Interlocked.Increment(ref postCount);
                // Fail on first attempt, succeed on second
                return postCount == 1 ? FakeHttpMessageHandler.Empty(500) : FakeHttpMessageHandler.Empty(204);
            }
            return FakeHttpMessageHandler.Empty(404);
        });

        var settings = fx.Store.Load();
        settings.Payment.Iban = "TR330006100519786457841326";
        fx.Store.Save(settings);

        // First call: API fails → internal state NOT updated
        await fx.Svc.SyncIfChangedAsync(CancellationToken.None);

        // Second call: same IBAN → should retry because state was NOT cached on failure
        await fx.Svc.SyncIfChangedAsync(CancellationToken.None);

        postCount.Should().Be(2, "failed call must not advance cached state, so next call retries");
    }
}
