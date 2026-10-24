using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Iys;
using OrderDeck.LicenseServer.Services.Sms;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using OrderDeck.LicenseServer.Tools;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Tools;

/// <summary>iys-reverify gerçek SQL Server'da (göçlerle kurulmuş şema; <c>ExecuteUpdate</c> InMemory'de çalışmaz): kuru
/// koşu yazmaz ve marka/durum dağılımını kişisel veri olmadan basar; uygulama yalnız hedef kümeyi (Onay + Expired/Failed +
/// son tarih / iys-not-confirmed hatası + bir kez kuyruğa alınmış + son tarihi geçmiş + son İYS cevabı okunamamış)
/// Pushed'a döndürür, randevuyu şimdiye çeker, olay yazmaz, yeniden itmez; düzeltilmiş doğrulama işi ardından İYS'nin
/// ONAY dediğini eşler. Ayrıştırma <see cref="IysReverifyTests"/>'te. Değerler (telefon, marka, kimlik) üretilir.</summary>
[Collection(SqlServerCollection.Name)]
[Trait("Category", "Testcontainers")]
public sealed class IysReverifyRelationalTests : IAsyncLifetime
{
    private readonly SqlServerContainerFixture _sql;
    private RelationalApiFactory _factory = null!;
    public IysReverifyRelationalTests(SqlServerContainerFixture sql) => _sql = sql;

    public async Task InitializeAsync()
    {
        _factory = new RelationalApiFactory(await _sql.CreateDatabaseAsync());
        _ = _factory.Services; // göçleri şimdi kur
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>A markasında iki hedef (son tarihi geçmiş Expired; takvimi tükenmiş Failed), B markasında bir hedef ve
    /// dokunulmaması gereken sekiz kayıt. Hedef olmayanların her biri süzgecin tek bir koşulunu çiğner.</summary>
    private sealed record Seed(
        Guid LicenseA, string BrandA, string BrandB,
        Guid ExpiredA, Guid FailedA, Guid ExpiredB,
        Guid[] Untouched, string[] Phones);

    private async Task<Seed> SeedAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var accounts = scope.ServiceProvider.GetRequiredService<NetgsmAccountService>();
        var now = DateTimeOffset.UtcNow;

        var customer = new Customer { Id = Guid.NewGuid(), Email = $"iys-{Guid.NewGuid():N}@example.test", Name = "Onarim",
            PasswordHash = $"h-{Guid.NewGuid():N}", CreatedAt = now };
        var license = new License { Id = Guid.NewGuid(), CustomerId = customer.Id, LicenseKey = "iy-" + Guid.NewGuid().ToString("N")[..12],
            SkuCode = "STD", ActivationSlots = 1, IssuedAt = now, ExpiresAt = now.AddYears(1) };
        db.Customers.Add(customer);
        db.Licenses.Add(license);

        var brandA = Random.Shared.Next(100_000, 500_000).ToString();
        var brandB = Random.Shared.Next(500_000, 999_999).ToString();
        // Yalnız A'nın doğrulanmış hesabı var: doğrulama işi yalnız A'yı tarar.
        db.NetgsmAccounts.Add(new NetgsmAccount
        {
            Id = Guid.NewGuid(), LicenseId = license.Id, UserCode = $"user-{Guid.NewGuid():N}"[..32],
            PasswordProtected = accounts.ProtectPassword($"p-{Guid.NewGuid():N}"), Header = "ORDERDECK",
            BrandCode = brandA, Status = NetgsmAccountStatus.Verified, CreatedAt = now, UpdatedAt = now,
        });

        var phones = new HashSet<string>();
        while (phones.Count < 11) phones.Add(TestPhone.NewE164());
        var p = phones.ToArray();

        IysConsent Row(string phone, string brand, IysConsentStatus status, IysPushState state, string? lastError,
            bool everPushed = true)
            => new()
            {
                Id = Guid.NewGuid(), BrandCode = brand, ChannelType = "MESAJ", RecipientType = "BIREYSEL", Recipient = phone,
                Status = status, ConsentDate = now.AddDays(-6), SourceCode = "HS_WEB", PushState = state,
                PushDeadline = now.AddDays(-2), LastVerifiedStatus = IysConsentStatus.Unknown,
                LastVerifiedAt = everPushed ? now.AddDays(-3) : null, LastLocalEventAt = now.AddDays(-6),
                LastPushedAt = everPushed ? now.AddDays(-5) : null, VerifyAttempts = 4, NextVerifyAt = null,
                LastError = lastError, CreatedAt = now.AddDays(-6), UpdatedAt = now.AddDays(-1),
            };

        var expiredA = Row(p[0], brandA, IysConsentStatus.Onay, IysPushState.Expired, IysReverify.DeadlinePassed);
        var failedA = Row(p[1], brandA, IysConsentStatus.Onay, IysPushState.Failed, "iys-not-confirmed status=Unknown");
        var expiredB = Row(p[2], brandB, IysConsentStatus.Onay, IysPushState.Expired, IysReverify.DeadlinePassed);
        var untouched = new[]
        {
            Row(p[3], brandA, IysConsentStatus.Ret, IysPushState.Expired, IysReverify.DeadlinePassed),       // RET beyanı
            Row(p[4], brandA, IysConsentStatus.Onay, IysPushState.Expired, IysReverify.DeadlinePassed,
                everPushed: false),                                                                          // hiç kuyruğa alınmadı
            Row(p[5], brandA, IysConsentStatus.Onay, IysPushState.Failed, "iys-add code=70"),                 // push hatası
            Row(p[6], brandA, IysConsentStatus.Onay, IysPushState.Confirmed, null),                           // zaten eşleşti
            Row(p[7], brandA, IysConsentStatus.Onay, IysPushState.Pushed, null),                              // takvimi sürüyor
            // Son tarihi geçmemiş Düştü: kurtarma işinin — 20–35 dk içinde yeniden iter. Komut alsaydı ~31 sa
            // bekletir, İYS ONAY demezse kurtarma yine Pending'e çekip yeniden iterdi (döngü kodla sonlu olmazdı).
            Row(p[8], brandA, IysConsentStatus.Onay, IysPushState.Failed, "iys-not-confirmed status=Unknown"),
            // Son İYS cevabı gerçek RET (çok alıcılı dizi doğru okunmuştu): hata yalnız Unknown üretti.
            Row(p[9], brandA, IysConsentStatus.Onay, IysPushState.Expired, IysReverify.DeadlinePassed),
            Row(p[10], brandA, IysConsentStatus.Onay, IysPushState.Failed, "iys-not-confirmed status=Ret"),
        };
        untouched[4].NextVerifyAt = now.AddHours(6);   // randevusu gelmemiş: doğrulama işi sormaz
        untouched[4].VerifyAttempts = 2;
        untouched[3].LastVerifiedStatus = IysConsentStatus.Onay;
        untouched[5].PushDeadline = now.AddDays(1);
        untouched[6].LastVerifiedStatus = IysConsentStatus.Ret;
        untouched[7].LastVerifiedStatus = IysConsentStatus.Ret;
        db.IysConsents.AddRange(expiredA, failedA, expiredB);
        db.IysConsents.AddRange(untouched);
        await db.SaveChangesAsync();

        return new Seed(license.Id, brandA, brandB, expiredA.Id, failedA.Id, expiredB.Id,
            untouched.Select(u => u.Id).ToArray(), p);
    }

    private async Task<(int Exit, string Output)> RunAsync(IysReverify.Options options)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var output = new StringWriter();
        var exit = await IysReverify.RunAsync(db, options, output, CancellationToken.None);
        return (exit, output.ToString());
    }

    private async Task<Dictionary<Guid, IysConsent>> RowsAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        return await db.IysConsents.AsNoTracking().ToDictionaryAsync(c => c.Id);
    }

    [Fact]
    public async Task Kuru_kosu_yazmaz_ve_dagilimi_kisisel_veri_olmadan_basar()
    {
        var seed = await SeedAsync();
        var before = await RowsAsync();

        var (exit, output) = await RunAsync(new IysReverify.Options(Brand: null, Apply: false));

        exit.Should().Be(0);
        output.Should().Contain("KURU ÇALIŞTIRMA")
            .And.Contain($"marka={seed.BrandA} durum=Expired hata=push-deadline-passed son-iys=Unknown: 1")
            .And.Contain($"marka={seed.BrandA} durum=Failed hata=iys-not-confirmed status=Unknown son-iys=Unknown: 1")
            .And.Contain($"marka={seed.BrandB} durum=Expired hata=push-deadline-passed son-iys=Unknown: 1")
            .And.Contain("Hedef: 3 kayıt.")
            .And.Contain("Bilgi: 1 kayıt", "takvimi süren Pushed+Unknown kayıt bilgi olarak sayılır")
            .And.Contain("Bilgi: 1 RET beyanı", "aynı hatanın vurmuş olabileceği RET beyanı sayılır, dokunulmaz")
            .And.NotContain("son-iys=Ret", "son İYS cevabı gerçek RET olan kayıt hedef değil");
        foreach (var phone in seed.Phones)
            output.Should().NotContain(phone[3..], "çıktıda telefon yok (KVKK)");

        (await RowsAsync()).Should().BeEquivalentTo(before, "kuru koşu hiçbir şey yazmaz");
    }

    [Fact]
    public async Task Uygulama_yalniz_hedefleri_Pushed_a_dondurur_olay_yazmaz_ve_idempotent()
    {
        var seed = await SeedAsync();
        var before = await RowsAsync();
        var start = DateTimeOffset.UtcNow.AddSeconds(-1);

        var (exit, output) = await RunAsync(new IysReverify.Options(Brand: null, Apply: true));

        exit.Should().Be(0);
        output.Should().Contain("UYGULANDI — 3 kayıt").And.Contain("yeniden itilmedi");
        foreach (var phone in seed.Phones)
            output.Should().NotContain(phone[3..], "çıktıda telefon yok (KVKK)");

        var after = await RowsAsync();
        foreach (var id in new[] { seed.ExpiredA, seed.FailedA, seed.ExpiredB })
        {
            var row = after[id];
            row.PushState.Should().Be(IysPushState.Pushed);
            row.NextVerifyAt.Should().NotBeNull().And.BeOnOrAfter(start).And.BeOnOrBefore(DateTimeOffset.UtcNow.AddSeconds(1));
            row.VerifyAttempts.Should().Be(0, "takvim baştan: 15dk → 1sa → 6sa → 24sa");
            row.LastError.Should().BeNull();
            // Beyan, son tarih ve push damgası OLDUĞU GİBİ: onarım yeniden itmez, yalnız yeniden sorar.
            row.Should().BeEquivalentTo(before[id], o => o
                .Excluding(c => c.PushState).Excluding(c => c.NextVerifyAt).Excluding(c => c.VerifyAttempts)
                .Excluding(c => c.LastError).Excluding(c => c.UpdatedAt));
        }
        foreach (var id in seed.Untouched)
            after[id].Should().BeEquivalentTo(before[id], "süzgeç dışındaki kayda dokunulmaz");

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            (await db.IysConsentEvents.CountAsync()).Should().Be(0, "onarım ekle-only ispat tablosuna satır yazmaz");
        }

        var (again, againOutput) = await RunAsync(new IysReverify.Options(Brand: null, Apply: true));
        again.Should().Be(0);
        againOutput.Should().Contain("Hedef: 0 kayıt.").And.Contain("Uygulanacak kayıt yok.");
    }

    [Fact]
    public async Task Marka_suzgeci_yalniz_o_markaya_dokunur()
    {
        var seed = await SeedAsync();

        var (exit, output) = await RunAsync(new IysReverify.Options(seed.BrandA, Apply: true));

        exit.Should().Be(0);
        output.Should().Contain($"(marka: {seed.BrandA})").And.Contain("UYGULANDI — 2 kayıt")
            .And.NotContain($"marka={seed.BrandB}");
        var after = await RowsAsync();
        after[seed.ExpiredA].PushState.Should().Be(IysPushState.Pushed);
        after[seed.FailedA].PushState.Should().Be(IysPushState.Pushed);
        after[seed.ExpiredB].PushState.Should().Be(IysPushState.Expired, "başka marka");
        after[seed.ExpiredB].LastError.Should().Be(IysReverify.DeadlinePassed);
    }

    /// <summary>Beklenen sonuç uçtan uca: onarımdan sonra doğrulama işi (gerçek istemci, Netgsm biçimli sahte HTTP)
    /// İYS'nin ONAY dediği kaydı eşler; ONAY demediği kayıt takvimine devam eder. /iys/add hiç çağrılmaz — sahte
    /// işleyici yalnız /iys/search'ü kabul eder.</summary>
    [Fact]
    public async Task Uygulamadan_sonra_dogrulama_isi_ONAY_olani_eslesir_olmayani_bekletir()
    {
        var seed = await SeedAsync();
        (await RunAsync(new IysReverify.Options(seed.BrandA, Apply: true))).Exit.Should().Be(0);

        var handler = new NetgsmIysSearchHandler();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var rows = await db.IysConsents.AsNoTracking()
                .Where(c => c.Id == seed.ExpiredA || c.Id == seed.FailedA).ToListAsync();
            handler.Statuses[rows.Single(r => r.Id == seed.ExpiredA).Recipient] = "ONAY";
            handler.Statuses[rows.Single(r => r.Id == seed.FailedA).Recipient] = "RET";

            var client = new NetgsmIysClient(new HttpClient(handler), Options.Create(new NetgsmOptions()),
                NullLogger<NetgsmIysClient>.Instance);
            var job = new IysConsentVerifyJob(db, client, scope.ServiceProvider.GetRequiredService<NetgsmAccountService>(),
                NullLogger<IysConsentVerifyJob>.Instance);
            await job.RunAsync();
        }

        handler.Requests.Should().ContainSingle("yalnız A'nın randevusu gelen iki kaydı tek partide sorulur")
            .Which.Should().HaveCount(2);
        var after = await RowsAsync();
        after[seed.ExpiredA].PushState.Should().Be(IysPushState.Confirmed, "İYS ONAY diyor — 'İYS ile eşleşti'");
        after[seed.ExpiredA].LastVerifiedStatus.Should().Be(IysConsentStatus.Onay);
        IysConsentGate.CanSend(after[seed.ExpiredA]).Should().BeTrue();
        after[seed.FailedA].PushState.Should().Be(IysPushState.Pushed, "ONAY demedi — takvim sürer, sonu Failed/Expired");
        after[seed.FailedA].VerifyAttempts.Should().Be(1);
        IysConsentGate.CanSend(after[seed.FailedA]).Should().BeFalse();
    }
}
