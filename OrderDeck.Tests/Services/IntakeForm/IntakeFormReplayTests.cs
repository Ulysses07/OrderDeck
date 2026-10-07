using Dapper;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
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
/// form işleme her iki kipte de ilk tam müşteri akışını bekler (C2 kalite incelemesi).</summary>
public sealed class IntakeFormReplayTests
{
    private sealed class FixedClock : IClock { public long UnixNow() => 1714521600L; }
    private sealed class License : ICurrentLicenseProvider { public string? CurrentLicenseKey { get; set; } }

    private const string ReplayMarker = "intake-form-replay";
    private static readonly string Lisans = $"lisans-{Guid.NewGuid():N}";

    private sealed record Fx(IntakeFormSyncService Svc, CustomerRepository Customers, SyncCursorRepository Cursors,
        SyncStatusTracker Tracker, InMemorySqlite Db, FakeHttpMessageHandler Http);

    /// <param name="pages">Sırayla dönen form sayfaları (JSON dizi); bitince "[]".</param>
    private static Fx Build(params string[] pages)
    {
        var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var customers = new CustomerRepository(db);
        var cursors = new SyncCursorRepository(db);
        var served = 0;
        var http = new FakeHttpMessageHandler(_ =>
            FakeHttpMessageHandler.Json(200, served < pages.Length ? pages[served++] : "[]"));
        var api = new LicenseApiClient(new HttpClient(http) { BaseAddress = new Uri("https://test.local") }, new LicenseTokenStore());
        var tracker = new SyncStatusTracker();
        var svc = new IntakeFormSyncService(api, customers, cursors, new License { CurrentLicenseKey = Lisans },
            new FixedClock(), NullLogger<IntakeFormSyncService>.Instance, tracker);
        return new Fx(svc, customers, cursors, tracker, db, http);
    }

    private static string Form(string user, string phone, string submittedAt = "2026-04-30T12:00:00Z")
        => $$"""[{"id":"{{Guid.NewGuid()}}","username":"{{user}}","fullName":"Örnek Müşteri","address":"Adres","phone":"{{phone}}","submittedAt":"{{submittedAt}}","instagramUsername":"{{user}}"}]""";

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
        fx.Cursors.Get(ReplayMarker, Lisans)!.Seq.Should().Be(1);
    }

    [Fact]
    public async Task Ilk_oynatma_doldurma_kipinde_biter_sonra_damgali_kipe_gecer()
    {
        var legacyPhone = TestPhone.NewE164();
        var newPhone = TestPhone.NewE164();
        var fx = Build(Form("ayse_y", TestPhone.NewE164()), "[]", Form("ayse_y", newPhone, "2026-10-08T12:00:00Z"));
        var id = LegacyRow(fx, "ayse_y", legacyPhone);
        fx.Tracker.MarkPullSucceeded(DateTimeOffset.UtcNow);

        await fx.Svc.SyncOnceAsync();                          // eski form: doldurma
        fx.Customers.GetById(id)!.Phone.Should().Be(legacyPhone, "eski form damgasız dolu değeri ezmez");
        PhoneStamp(fx, id).Should().BeNull();

        await fx.Svc.SyncOnceAsync();                          // boş sayfa: oynatma bitti
        fx.Cursors.Get(ReplayMarker, Lisans)!.Seq.Should().Be(2);

        await fx.Svc.SyncOnceAsync();                          // oynatmadan sonraki form: damgalı (kural 3)
        fx.Customers.GetById(id)!.Phone.Should().Be(newPhone);
        PhoneStamp(fx, id).Should().Be(DateTimeOffset.Parse("2026-10-08T12:00:00Z").ToUnixTimeMilliseconds());
    }

    [Fact]
    public async Task Imleci_olan_kurulumda_oynatma_kipi_yoktur()
    {
        var fx = Build(Form("ayse_y", TestPhone.NewE164()));
        fx.Cursors.Upsert("intake-form-in", Lisans, updatedAt: DateTimeOffset.UtcNow.AddDays(-1), lastId: Guid.NewGuid());
        var id = LegacyRow(fx, "ayse_y", TestPhone.NewE164());
        fx.Tracker.MarkPullSucceeded(DateTimeOffset.UtcNow);

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

        fx.Tracker.MarkPullSucceeded(DateTimeOffset.UtcNow);
        (await fx.Svc.SyncOnceAsync()).Should().Be(1);
        PhoneStamp(fx, id).Should().NotBeNull();
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

        fx.Tracker.MarkPullSucceeded(DateTimeOffset.UtcNow);
        (await fx.Svc.BackfillFullNamesOnceAsync()).Should().Be(1);

        fx.Customers.GetById(id)!.FullName.Should().Be("Örnek Müşteri");
        using var c = fx.Db.Open();
        c.ExecuteScalar<long?>("SELECT FullNameChangedAt FROM Customer WHERE Id = @id", new { id })
            .Should().BeNull("oynatma sürerken backfill damga yazmaz");
        fx.Cursors.Get("intake-fullname-backfill", Lisans)!.Seq.Should().Be(2);
    }
}
