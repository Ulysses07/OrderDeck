using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.CustomerSync;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Licenses;

/// <summary>
/// B1: kişi başına tek asıl kayıt kuralını GERÇEK filtreli tekil indeks koyar
/// (<see cref="CustomerIdentityIndex"/>); uygulamanın "bu kimliğin asıl kaydı
/// var mı" okuması iki eşzamanlı isteğin ikisine de "yok" diyebilir. Kaybeden
/// INSERT'ü sunucu kendi içinde çözer: sync ucu partiyi taze okumayla bir kez
/// yeniden uygular (kimlik araması diğer isteğin açtığı asıl kaydı bulur, bu Id
/// kopya + yönlendirme olur), Shopper kayıt/katılma geçici satırı bırakıp
/// bağlantıyı kanıtla bağlar ya da beklemede bırakır. Kimseye 500 gitmez.
///
/// <para>Eşzamanlılık zamanlamaya bırakılmaz: <see cref="RaceInterceptor"/>
/// iki isteğin İLK kaydını (yeni bir asıl kayıt ekleyen) ikisi de okumasını
/// bitirip kayda gelene kadar tutar, sonra birlikte bırakır — okuma→yazma
/// aralıkları her koşuda üst üste biner. Kaybedenin indekse çarptığı da ayrıca
/// sayılır: yarışın gerçekten yaşandığının kanıtı. Kimin kazanacağı ise
/// SQL Server'a kalır; her kol ayrıca <see cref="RaceInterceptor.BeforeSave"/>
/// kancasıyla (okumadan sonra, kayıttan hemen önce araya giren yazım)
/// deterministik sınanır.</para>
///
/// <para>Gerçek SQL Server şart (Testcontainers): InMemory tekil indeksi
/// uygulamaz, eşzamanlılık semantiği yoktur.</para>
/// </summary>
[Collection(SqlServerCollection.Name)]
[Trait("Category", "Testcontainers")]
public sealed class WpfCustomerIdentityRaceTests : IAsyncLifetime
{
    private readonly SqlServerContainerFixture _sql;
    private readonly RaceInterceptor _race = new();
    private string _cs = null!;
    private RaceFactory _factory = null!;

    public WpfCustomerIdentityRaceTests(SqlServerContainerFixture sql) => _sql = sql;

    public async Task InitializeAsync()
    {
        _cs = await _sql.CreateDatabaseAsync();
        _factory = new RaceFactory(_cs, _race);
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    private sealed record RedirectBody(Guid Id, Guid CanonicalId);
    private sealed record SyncBody(int Synced, int RetroactiveMatches, List<RedirectBody> Redirects);

    private sealed record RegisterBody(
        string BroadcasterCode, string FullName, string Phone, string Password,
        string Address, string Platform, string Username);

    private sealed record AuthBody(string AccessToken, Guid ShopperId);

    private sealed record LoginBody(string Phone, string Password);

    private sealed record JoinBody(string BroadcasterCode, string Platform, string Username);

    private static string NewPhone() => "+9055" + Random.Shared.Next(10_000_000, 99_999_999);

    /// <summary>Kayıt için üretilmiş giriş değeri (repo public: sabit yazılmaz).</summary>
    private static string NewSecret() => $"g-{Guid.NewGuid():N}";

    private static string SyncUrl(Guid licenseId) => $"/api/v1/licenses/{licenseId}/wpf-customers/sync";

    /// <summary>Yayıncı bilgisayarının format 2 gönderimi (ad ve telefon damgalı).</summary>
    private static object Item(Guid id, string username, string? fullName = null, string? phone = null)
    {
        var t = DateTimeOffset.UtcNow;
        return new
        {
            id, platform = "tiktok", username, fullName, phone, updatedAt = t, format = 2,
            fullNameChangedAt = fullName is null ? (DateTimeOffset?)null : t,
            phoneChangedAt = phone is null ? (DateTimeOffset?)null : t,
        };
    }

    /// <summary>Yayıncı (oturum açmış istemci) + Shopper davet kodlu lisansı.</summary>
    private async Task<(HttpClient Client, Guid LicenseId, string ShopperCode)> SetupAsync()
    {
        var (client, customerId, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);
        var code = "r" + Guid.NewGuid().ToString("N")[..12];
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var license = new License
        {
            Id = Guid.NewGuid(),
            CustomerId = customerId,
            // LicenseKey HasMaxLength(40) — gerçek SQL'de tam Guid taşar.
            LicenseKey = "race-" + Guid.NewGuid().ToString("N")[..12],
            SkuCode = "STD",
            ActivationSlots = 1,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
            ShopperCode = code,
            ShopperCodeUpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Licenses.Add(license);
        await db.SaveChangesAsync();
        return (client, license.Id, code);
    }

    private async Task<List<WpfCustomerProjection>> RowsAsync(Guid licenseId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        // Kopyalar varsayılan sorgulardan gizli (A5b) — burada açıkça istenir.
        return await db.WpfCustomerProjections.IgnoreQueryFilters().AsNoTracking()
            .Where(p => p.LicenseId == licenseId).ToListAsync();
    }

    private static async Task<SyncBody> OkBodyAsync(HttpResponseMessage resp)
    {
        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        return (await resp.Content.ReadFromJsonAsync<SyncBody>())!;
    }

    [Fact]
    public async Task Eszamanli_ilk_gonderimde_tek_asil_kayit_kalir_ikisi_de_200()
    {
        // İki bilgisayar aynı kişiyi (harf farkıyla) kendi Id'siyle İLK kez gönderir.
        var (client, licenseId, _) = await SetupAsync();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var phone = NewPhone();
        _race.ArmBarrier(parties: 2);

        var responses = await Task.WhenAll(
            client.PostAsJsonAsync(SyncUrl(licenseId), new { customers = new[] { Item(a, "zeynep", fullName: "Zeynep Kaya") } }),
            client.PostAsJsonAsync(SyncUrl(licenseId), new { customers = new[] { Item(b, "Zeynep", phone: phone) } }));

        var bodyA = await OkBodyAsync(responses[0]);
        var bodyB = await OkBodyAsync(responses[1]);
        _race.IdentityViolations.Should().Be(1, "iki istek de 'asıl kayıt yok' okudu; kaybeden indekse çarptı");

        var rows = await RowsAsync(licenseId);
        var head = rows.Should().ContainSingle(p => p.MergedIntoId == null, "kişi başına tek asıl kayıt").Subject;
        var loser = head.Id == a ? b : a;
        rows.Single(p => p.Id == loser).MergedIntoId.Should().Be(head.Id, "kaybedenin Id'si kopya olur");
        var (winnerBody, loserBody) = head.Id == a ? (bodyA, bodyB) : (bodyB, bodyA);
        winnerBody.Redirects.Should().BeEmpty();
        loserBody.Redirects.Should().ContainSingle().Which.Should().Be(new RedirectBody(loser, head.Id),
            "istemci yerel satırını asıl kaydın Id'sine taşısın");
        bodyA.Synced.Should().Be(1);
        bodyB.Synced.Should().Be(1);
        // Kaybedenin verisi kaybolmaz: yeniden denemede asıl kayda yazıldı.
        head.FullName.Should().Be("Zeynep Kaya");
        head.Phone.Should().Be(phone);
    }

    [Fact]
    public async Task Yeniden_denemede_de_kimlik_cakisirsa_409_doner_istemci_yeniden_gonderince_toparlanir()
    {
        // Her denemede, kayıttan hemen önce partideki bir kimliğin asıl kaydını
        // başka bir bilgisayar açar: ilk deneme "ayse"de, yeniden deneme
        // "mehmet"te indekse çarpar. Tek yeniden deneme hakkı bitti → 409.
        var (client, licenseId, _) = await SetupAsync();
        var x = Guid.NewGuid();
        var y = Guid.NewGuid();
        var pending = new Queue<string>(["ayse", "mehmet"]);
        var rivals = new Dictionary<string, Guid>();
        _race.BeforeSave = async ctx =>
        {
            var newHeads = ctx.ChangeTracker.Entries<WpfCustomerProjection>()
                .Where(e => e.State == EntityState.Added && e.Entity.MergedIntoId == null)
                .Select(e => e.Entity.IdentityKey).ToList();
            if (pending.Count == 0 || !newHeads.Contains(pending.Peek())) return;
            var key = pending.Dequeue();
            rivals[key] = await InsertHeadAsync(licenseId, key);
        };

        var resp = await client.PostAsJsonAsync(SyncUrl(licenseId),
            new { customers = new[] { Item(x, "ayse"), Item(y, "Mehmet") } });

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await resp.Content.ReadFromJsonAsync<ProblemDetails>())!.Title.Should().Be("sync-conflict");
        _race.IdentityViolations.Should().Be(2);
        (await RowsAsync(licenseId)).Select(p => p.Id).Should().NotContain(new[] { x, y }, "parti bütünüyle geri alındı");

        // İstemci partiyi yeniden gönderir: iki Id de karşı tarafın asıl kaydına bağlanır.
        _race.BeforeSave = null;
        var body = await OkBodyAsync(await client.PostAsJsonAsync(SyncUrl(licenseId),
            new { customers = new[] { Item(x, "ayse"), Item(y, "Mehmet") } }));
        body.Redirects.Should().BeEquivalentTo(new[]
        {
            new RedirectBody(x, rivals["ayse"]),
            new RedirectBody(y, rivals["mehmet"]),
        });
        (await RowsAsync(licenseId)).Where(p => p.MergedIntoId == null).Select(p => p.Id)
            .Should().BeEquivalentTo(new[] { rivals["ayse"], rivals["mehmet"] });
    }

    [Fact]
    public async Task Devralma_gercek_indeksle_bakiyeli_gecici_satir_W_ye_devredilir()
    {
        // Devralmada geçici S indeksten ÇIKAR (UPDATE MergedIntoId) ve yayıncının
        // W'si aynı anahtarla GİRER (INSERT), tek kayıtta. EF tablo içinde önce
        // UPDATE'i, sonra INSERT'ü gönderir; öz-FK eklenseydi sıra tersine döner
        // ve her denemede 2601 olurdu (bkz. LicenseDbContext).
        var (client, licenseId, _) = await SetupAsync();
        var phone = NewPhone();
        var sId = Guid.NewGuid();
        var linkId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var shopper = new OrderDeck.LicenseServer.Domain.Shopper
            {
                Id = Guid.NewGuid(), FullName = "Shopper Beyanı", Phone = phone, PhoneVerifiedAt = DateTimeOffset.UtcNow,
                PasswordHash = $"hash-{Guid.NewGuid():N}", Address = "Shopper adresi",
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            };
            db.Shoppers.Add(shopper);
            db.WpfCustomerProjections.Add(new WpfCustomerProjection
            {
                Id = sId, LicenseId = licenseId, Platform = "tiktok", Username = "devralinan",
                FullName = "Shopper Beyanı", Phone = phone, Address = "Shopper adresi",
                CreatedByShopper = true, UpdatedAt = DateTimeOffset.UtcNow,
            });
            db.ShopperBroadcasterLinks.Add(new ShopperBroadcasterLink
            {
                Id = linkId, ShopperId = shopper.Id, LicenseId = licenseId, Platform = "tiktok",
                Username = "devralinan", WpfCustomerId = sId, JoinedAt = DateTimeOffset.UtcNow,
            });
            db.CustomerBalances.Add(new CustomerBalance
                { Id = Guid.NewGuid(), LicenseId = licenseId, WpfCustomerId = sId, Balance = 40m, UpdatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }
        var w = Guid.NewGuid();

        var body = await OkBodyAsync(await client.PostAsJsonAsync(SyncUrl(licenseId),
            new { customers = new[] { Item(w, "Devralinan", fullName: "Yayıncının Kaydı", phone: phone) } }));

        _race.IdentityViolations.Should().Be(0, "S indeksten W girmeden önce çıkar — yeniden deneme bile gerekmez");
        body.Redirects.Should().BeEmpty("W asıl kayıt");
        var rows = await RowsAsync(licenseId);
        rows.Single(p => p.Id == w).MergedIntoId.Should().BeNull();
        rows.Single(p => p.Id == sId).MergedIntoId.Should().Be(w);
        using var verify = _factory.Services.CreateScope();
        var vdb = verify.ServiceProvider.GetRequiredService<LicenseDbContext>();
        (await vdb.CustomerBalances.AsNoTracking().SingleAsync(b => b.WpfCustomerId == w)).Balance.Should().Be(40m);
        (await vdb.CustomerBalances.AsNoTracking().SingleAsync(b => b.WpfCustomerId == sId)).Balance.Should().Be(0m);
        (await vdb.ShopperBroadcasterLinks.AsNoTracking().SingleAsync(l => l.Id == linkId)).WpfCustomerId
            .Should().Be(w, "yayıncının telefonu shopper'ın doğrulanmış telefonu: kanıt geçer");
    }

    [Fact]
    public async Task Shopper_kaydi_yayinci_gonderimiyle_ust_uste_gelirse_500_yok_tek_asil_kayit()
    {
        // Aday yokken: shopper kaydı geçici satır açmaya, yayıncı aynı kişiyi
        // kendi Id'siyle göndermeye gidiyor. Hangisi kazanırsa kazansın asıl
        // kayıt yayıncının satırı (W): ya shopper geri çekilip bağlantıyı bekletir,
        // ya da sync yeniden denemede geçici satırı devralır.
        var (client, licenseId, code) = await SetupAsync();
        var w = Guid.NewGuid();
        _race.ArmBarrier(parties: 2);

        var responses = await Task.WhenAll(
            _factory.CreateClient().PostAsJsonAsync("/api/v1/shopper/auth/register",
                new RegisterBody(code, "Kemal Shopper", NewPhone(), NewSecret(), "Adres", "tiktok", "Kemal")),
            client.PostAsJsonAsync(SyncUrl(licenseId), new { customers = new[] { Item(w, "kemal", fullName: "Kemal Yayıncı") } }));

        responses[0].StatusCode.Should().Be(HttpStatusCode.Created, await responses[0].Content.ReadAsStringAsync());
        await OkBodyAsync(responses[1]);
        _race.IdentityViolations.Should().Be(1, "iki istek de aday görmedi; kaybeden indekse çarptı");
        await ShouldHaveSingleHeadAndPendingLinkAsync(licenseId, w);
    }

    [Fact]
    public async Task Shopper_katilmasi_yayinci_gonderimiyle_ust_uste_gelirse_500_yok_tek_asil_kayit()
    {
        var (client, licenseId, code) = await SetupAsync();
        var (_, _, otherCode) = await SetupAsync();
        // Shopper başka bir yayıncıya kayıtlı; bu yayıncıya katılıyor.
        var shopperClient = _factory.CreateClient();
        var register = await shopperClient.PostAsJsonAsync("/api/v1/shopper/auth/register",
            new RegisterBody(otherCode, "Nehir Shopper", NewPhone(), NewSecret(), "Adres", "tiktok", "nehir-baska"));
        register.StatusCode.Should().Be(HttpStatusCode.Created);
        var auth = (await register.Content.ReadFromJsonAsync<AuthBody>())!;
        shopperClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        var w = Guid.NewGuid();
        _race.ArmBarrier(parties: 2);

        var responses = await Task.WhenAll(
            shopperClient.PostAsJsonAsync("/api/v1/shopper/broadcasters/join", new JoinBody(code, "tiktok", "NEHIR")),
            client.PostAsJsonAsync(SyncUrl(licenseId), new { customers = new[] { Item(w, "nehir") } }));

        responses[0].StatusCode.Should().Be(HttpStatusCode.OK, await responses[0].Content.ReadAsStringAsync());
        await OkBodyAsync(responses[1]);
        _race.IdentityViolations.Should().Be(1);
        await ShouldHaveSingleHeadAndPendingLinkAsync(licenseId, w);
    }

    [Fact]
    public async Task Gonderim_eszamanli_acilan_gecici_satira_carparsa_yeniden_denemede_devralir()
    {
        // Yarışın öbür kolu, deterministik: shopper kaydı önce yazdı. Sync
        // okuduktan SONRA, kaydetmeden ÖNCE geçici satır (S) ve bağlantısı açılır;
        // W'nin INSERT'ü indekse çarpar, taze okumada S bulunur ve devralınır.
        var (client, licenseId, _) = await SetupAsync();
        var phone = NewPhone();
        var w = Guid.NewGuid();
        Guid? sId = null, linkId = null;
        _race.BeforeSave = async _ =>
        {
            if (sId is not null) return;
            (sId, linkId) = await InsertProvisionalAsync(licenseId, "Deniz", phone);
        };

        var body = await OkBodyAsync(await client.PostAsJsonAsync(SyncUrl(licenseId),
            new { customers = new[] { Item(w, "deniz", fullName: "Deniz Yayıncı", phone: phone) } }));

        _race.IdentityViolations.Should().Be(1, "ilk kayıt aradaki geçici satıra çarptı");
        body.Redirects.Should().BeEmpty("devralmada W asıl kayıt");
        var rows = await RowsAsync(licenseId);
        rows.Single(p => p.Id == w).MergedIntoId.Should().BeNull();
        var provisional = rows.Single(p => p.Id == sId);
        provisional.MergedIntoId.Should().Be(w);
        provisional.Phone.Should().BeNull("geçici satırın beyanı boşaltıldı");
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        (await db.ShopperBroadcasterLinks.AsNoTracking().SingleAsync(l => l.Id == linkId)).WpfCustomerId
            .Should().Be(w, "yayıncının telefonu shopper'ın doğrulanmış telefonu: bağlantı W'ye taşındı, kanıtlandı");
    }

    // ── Shopper tarafı, deterministik: geçici satır INSERT'ü, okumadan SONRA
    //    açılan asıl kayda çarpar. Telefonu doğrulanmış shopper'ın numarası o
    //    asıl kaydın telefonu → bağlantı kanıtla ona bağlanır (yarış testleri
    //    doğrulanmamış shopper'la "beklemede" kolunu gösteriyor).

    [Fact]
    public async Task Kayit_gecici_satiri_eszamanli_acilan_asil_kayda_carparsa_baglanti_kanitla_baglanir()
    {
        var (_, licenseId, code) = await SetupAsync();
        var (phone, secret, _) = await SeedVerifiedShopperAsync();
        var w = HeadInsertedBeforeProvisionalSave(licenseId, "Ece", phone);

        // Telefonu kayıtlı shopper bu yayıncıya kaydoluyor (hesap yeniden kullanılır).
        var resp = await _factory.CreateClient().PostAsJsonAsync("/api/v1/shopper/auth/register",
            new RegisterBody(code, "Ece Shopper", phone, secret, "Adres", "tiktok", "ece"));

        resp.StatusCode.Should().Be(HttpStatusCode.Created, await resp.Content.ReadAsStringAsync());
        await ShouldBindProvenToHeadAsync(licenseId, w);
    }

    [Fact]
    public async Task Katilma_gecici_satiri_eszamanli_acilan_asil_kayda_carparsa_baglanti_kanitla_baglanir()
    {
        var (_, licenseId, code) = await SetupAsync();
        var (phone, secret, _) = await SeedVerifiedShopperAsync();
        var login = await _factory.CreateClient().PostAsJsonAsync("/api/v1/shopper/auth/login", new LoginBody(phone, secret));
        login.StatusCode.Should().Be(HttpStatusCode.OK);
        var shopperClient = _factory.CreateClient();
        shopperClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", (await login.Content.ReadFromJsonAsync<AuthBody>())!.AccessToken);
        var w = HeadInsertedBeforeProvisionalSave(licenseId, "Ece", phone);

        var resp = await shopperClient.PostAsJsonAsync("/api/v1/shopper/broadcasters/join",
            new JoinBody(code, "tiktok", "ECE"));

        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        await ShouldBindProvenToHeadAsync(licenseId, w);
    }

    /// <summary>Geçici satırı ekleyen ilk kayıttan HEMEN ÖNCE (okumadan sonra)
    /// yayıncının asıl kaydını — verilen telefonla — kancasız bir bağlamdan açar.</summary>
    private Task<Guid> HeadInsertedBeforeProvisionalSave(Guid licenseId, string username, string phone)
    {
        var inserted = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
        _race.BeforeSave = async ctx =>
        {
            if (inserted.Task.IsCompleted || !ctx.ChangeTracker.Entries<WpfCustomerProjection>()
                    .Any(e => e.State == EntityState.Added && e.Entity.CreatedByShopper)) return;
            inserted.SetResult(await InsertHeadAsync(licenseId, username, phone));
        };
        return inserted.Task;
    }

    private async Task ShouldBindProvenToHeadAsync(Guid licenseId, Task<Guid> insertedHead)
    {
        // İstek bitti: kanca tetiklendiyse asıl kayıt çoktan açıldı (beklemek
        // yerine denetlenir — tetiklenmeseydi test asılı kalmasın).
        insertedHead.IsCompletedSuccessfully.Should().BeTrue("kanca geçici satırı ekleyen kayıtta tetiklendi");
        var headId = await insertedHead;
        _race.IdentityViolations.Should().Be(1, "geçici satır aradaki asıl kayda çarptı");
        var rows = await RowsAsync(licenseId);
        rows.Should().ContainSingle("geçici satır bırakıldı").Which.Id.Should().Be(headId);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        (await db.ShopperBroadcasterLinks.AsNoTracking().SingleAsync(l => l.LicenseId == licenseId))
            .WpfCustomerId.Should().Be(headId, "doğrulanmış telefon asıl kaydın telefonu: kanıt geçer");
    }

    /// <summary>Telefonu OTP ile doğrulanmış, giriş yapabilen shopper (değerler üretilmiş).</summary>
    private async Task<(string Phone, string Secret, Guid ShopperId)> SeedVerifiedShopperAsync()
    {
        var phone = NewPhone();
        var secret = NewSecret();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<OrderDeck.LicenseServer.Services.Auth.PasswordHasher>();
        var shopper = new OrderDeck.LicenseServer.Domain.Shopper
        {
            Id = Guid.NewGuid(), FullName = "Ece Shopper", Phone = phone, PhoneVerifiedAt = DateTimeOffset.UtcNow,
            PasswordHash = hasher.Hash(secret), Address = "Adres",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Shoppers.Add(shopper);
        await db.SaveChangesAsync();
        return (phone, secret, shopper.Id);
    }

    /// <summary>Kimliğin tek asıl kaydı yayıncının satırı; geçici satır açıldıysa
    /// onun kopyası. Shopper'ın telefonu doğrulanmadı — kanıt yok, bağlantı
    /// beklemede.</summary>
    private async Task ShouldHaveSingleHeadAndPendingLinkAsync(Guid licenseId, Guid broadcasterRowId)
    {
        var rows = await RowsAsync(licenseId);
        rows.Should().ContainSingle(p => p.MergedIntoId == null).Which.Id.Should().Be(broadcasterRowId);
        rows.Where(p => p.Id != broadcasterRowId).Should().NotContain(p => p.MergedIntoId != broadcasterRowId,
            "geçici satır kazandıysa yeniden denemede yayıncının satırına devredildi");
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        (await db.ShopperBroadcasterLinks.AsNoTracking().SingleAsync(l => l.LicenseId == licenseId))
            .WpfCustomerId.Should().BeNull("telefon kanıtı yok: bağlantı beklemede");
    }

    /// <summary>Shopper kaydının açtığı geçici satır (ShopperAuthController adım
    /// 8a ile aynı alanlar) ve ona kanıtsız bağlı bağlantı; telefonu doğrulanmış
    /// shopper. Kancasız, ayrı bir bağlamdan.</summary>
    private async Task<(Guid ProjectionId, Guid LinkId)> InsertProvisionalAsync(
        Guid licenseId, string username, string phone)
    {
        await using var db = new LicenseDbContext(
            new DbContextOptionsBuilder<LicenseDbContext>().UseSqlServer(_cs).Options);
        var shopper = new OrderDeck.LicenseServer.Domain.Shopper
        {
            Id = Guid.NewGuid(), FullName = "Shopper Beyanı", Phone = phone, PhoneVerifiedAt = DateTimeOffset.UtcNow,
            PasswordHash = $"hash-{Guid.NewGuid():N}", Address = "Shopper adresi",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        var projection = new WpfCustomerProjection
        {
            Id = Guid.NewGuid(), LicenseId = licenseId, Platform = "tiktok", Username = username,
            FullName = shopper.FullName, Phone = phone, Address = shopper.Address,
            CreatedByShopper = true, UpdatedAt = DateTimeOffset.UtcNow,
        };
        var link = new ShopperBroadcasterLink
        {
            Id = Guid.NewGuid(), ShopperId = shopper.Id, LicenseId = licenseId, Platform = "tiktok",
            Username = username, WpfCustomerId = projection.Id, JoinedAt = DateTimeOffset.UtcNow,
        };
        db.Shoppers.Add(shopper);
        db.WpfCustomerProjections.Add(projection);
        db.ShopperBroadcasterLinks.Add(link);
        await db.SaveChangesAsync();
        return (projection.Id, link.Id);
    }

    /// <summary>"Başka bir bilgisayar" — kancasız, ayrı bir bağlamdan asıl kayıt açar.</summary>
    private async Task<Guid> InsertHeadAsync(Guid licenseId, string username, string? phone = null)
    {
        await using var db = new LicenseDbContext(
            new DbContextOptionsBuilder<LicenseDbContext>().UseSqlServer(_cs).Options);
        var row = new WpfCustomerProjection
        {
            Id = Guid.NewGuid(), LicenseId = licenseId, Platform = "tiktok", Username = username,
            Phone = phone, UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.WpfCustomerProjections.Add(row);
        await db.SaveChangesAsync();
        return row.Id;
    }

    /// <summary>
    /// (1) Bariyer: kurulunca, YENİ BİR ASIL KAYIT ekleyen ilk N kayıt (N isteğin
    /// her birinin ilki) hepsi gelene kadar bekler, sonra birlikte bırakılır.
    /// SavingChanges EF kayıt işlemini açmadan ÖNCE koşar: bekleyen istek hiçbir
    /// kilit tutmaz. Sonraki kayıtlar (yeniden deneme, jeton) beklemez.
    /// (2) <see cref="BeforeSave"/>: her kayıttan önce koşan test kancası.
    /// (3) Kimlik indeksine çarpan kayıt sayısı.
    /// </summary>
    private sealed class RaceInterceptor : SaveChangesInterceptor
    {
        private int _parties;
        private int _arrived;
        private TaskCompletionSource _allArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _identityViolations;

        public Func<DbContext, Task>? BeforeSave { get; set; }
        public int IdentityViolations => Volatile.Read(ref _identityViolations);

        public void ArmBarrier(int parties)
        {
            _allArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref _arrived, 0);
            Volatile.Write(ref _parties, parties);
        }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var ctx = eventData.Context!;
            var parties = Volatile.Read(ref _parties);
            if (parties > 0 && ctx.ChangeTracker.Entries<WpfCustomerProjection>()
                    .Any(e => e.State == EntityState.Added && e.Entity.MergedIntoId == null))
            {
                if (Interlocked.Increment(ref _arrived) == parties) _allArrived.TrySetResult();
                await _allArrived.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            }
            if (BeforeSave is { } hook) await hook(ctx);
            return result;
        }

        public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
        {
            if (eventData.Exception is DbUpdateException ex && CustomerIdentityIndex.IsViolation(ex))
                Interlocked.Increment(ref _identityViolations);
            return Task.CompletedTask;
        }
    }

    private sealed class RaceFactory(string connectionString, RaceInterceptor race)
        : RelationalApiFactory(connectionString)
    {
        protected override void ConfigureDbContextOptions(DbContextOptionsBuilder opt)
            => opt.AddInterceptors(race);
    }
}
