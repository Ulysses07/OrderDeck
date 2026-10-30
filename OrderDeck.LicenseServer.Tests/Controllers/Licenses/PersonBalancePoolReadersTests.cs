using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;
using Kit = OrderDeck.LicenseServer.Tests.Controllers.Licenses.PersonBalanceTestKit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Licenses;

/// <summary>
/// Ortak bakiye S2, WPF okuyucularının havuz kipi (spec 2026-10-11 §4.2 "İdempotency ve A11", §4.3).
/// <list type="bullet">
///   <item><c>/scope</c> havuz kipinde <c>PersonPool = true</c> döner; yüklem ve <c>−Σ</c> S1'deki gibi, çapa düzeyinde.</item>
///   <item>Kayıtlı anahtarın oynatması <c>RemainingBalance</c> olarak o anki havuzu verir: <c>memberIds</c> varsa
///   kural 4'ün Σ'sı, yoksa <c>S</c>'nin toplamı. 409 kuralları uygulanmaz (plan fark 18c).</item>
///   <item>Satır kipinde ikisi de bugünkü gibi; <c>memberIds</c> okunmaz. Tek fark: Guid'e bağlanamayan
///   <c>memberIds</c> apply gövdesinde kipten bağımsız 400 alır (plan Görev 6 Not 6).</item>
/// </list>
/// Satış senkronu mod Off (sürüm kapısı yok). Kayıtlar Örnek 1'in sonucu, elle kurulur; bu görevde havuzlu yazım yok.
/// </summary>
public sealed class PersonBalancePoolReadersTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public PersonBalancePoolReadersTests(ApiFactory factory) => _factory = factory;

    private sealed record ScopeDto(Guid TransactionId, decimal AppliedAmount, decimal ProductTotal, DateTimeOffset CreatedAt, bool PersonPool);
    private sealed record ApplyDto(Guid TransactionId, decimal AppliedAmount, decimal RemainingBalance);

    /// <summary>Gövdede <c>personPool</c> alanı gerçekten var mı (eksik alan record'da da false okunurdu).</summary>
    private static async Task<bool> PersonPoolFieldAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("personPool").GetBoolean();
    }

    private static async Task<ApplyDto> OkApplyAsync(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<ApplyDto>())!;
    }

    /// <summary>Örnek 1'in sonucu (T 0, I 0, Y 10; K çapa T'de) ve lisans havuz kipinde.</summary>
    private async Task<(Kit, Kit.Example1)> PoolExample1Async()
    {
        var kit = await Kit.CreateAsync(_factory);
        var ex = await kit.Example1Async();
        await kit.EnablePoolAsync();
        return (kit, ex);
    }

    // ── scope (§4.3) ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Havuz_kipinde_scope_PersonPool_true()
    {
        var (kit, ex) = await PoolExample1Async();

        var resp = await kit.ScopeAsync(ex.T, Kit.Session42);

        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        (await resp.Content.ReadFromJsonAsync<ScopeDto>()).Should().BeEquivalentTo(new
        {
            TransactionId = ex.K, AppliedAmount = 120m, ProductTotal = 120m, PersonPool = true,
        }, "yüklem ve −Σ(grup) S1'deki gibi; yalnız kip bayrağı değişir (§4.3)");
        (await PersonPoolFieldAsync(resp)).Should().BeTrue("WPF birleştirme notu için kipi okur (§6.1-d)");
    }

    [Fact]
    public async Task Havuz_kipinde_baska_uyedeki_capa_scope_204()
    {
        var (kit, ex) = await PoolExample1Async();

        (await kit.ScopeAsync(ex.I, Kit.Session42)).StatusCode.Should().Be(HttpStatusCode.NoContent,
            "kapsam kuralı havuz kipinde de çapa düzeyinde: bacak kapsam taşımaz (Örnek 1, M10)");
    }

    [Fact]
    public async Task Satir_kipinde_scope_PersonPool_false()
    {
        var kit = await Kit.CreateAsync(_factory);
        var ex = await kit.Example1Async();

        var resp = await kit.ScopeAsync(ex.T, Kit.Session42);

        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        (await resp.Content.ReadFromJsonAsync<ScopeDto>()).Should().BeEquivalentTo(new
        {
            TransactionId = ex.K, AppliedAmount = 120m, ProductTotal = 120m, PersonPool = false,
        });
        (await PersonPoolFieldAsync(resp)).Should().BeFalse("bayrak boş: satır kipi (§4.0 Kip)");
    }

    // ── oynatmanın RemainingBalance'ı (§4.2 "İdempotency ve A11") ────────────

    [Fact]
    public async Task Havuz_kipinde_oynatma_RemainingBalance_havuz()
    {
        var (kit, ex) = await PoolExample1Async();

        var body = await OkApplyAsync(
            await kit.ApplyAsync(ex.T, 120m, 120m, ex.K, Kit.Session42, [ex.T, ex.I, ex.Y]));

        body.Should().Be(new ApplyDto(ex.K, 120m, 10m), "oynatma o anki havuzu döner: T 0 + I 0 + Y 10 (§4.2)");
        (await kit.BalanceAsync(ex.Y)).Should().Be(10m, "oynatma hiçbir şey yazmaz");
        (await kit.BalanceAsync(ex.I)).Should().Be(0m);
    }

    [Fact]
    public async Task Havuz_kipinde_memberIds_siz_oynatma_RemainingBalance_S_toplami()
    {
        var (kit, ex) = await PoolExample1Async();

        // 5 argümanlı aşırı yükleme gövdeye memberIds alanını hiç koymaz (eski WPF'in gönderdiği biçim);
        // 6 argümanlıda null → JSON'da açık "memberIds": null.
        var withoutField = await OkApplyAsync(
            await kit.ApplyAsync(ex.T, 120m, 120m, ex.K, Kit.Session42));
        var explicitNull = await OkApplyAsync(
            await kit.ApplyAsync(ex.T, 120m, 120m, ex.K, Kit.Session42, memberIds: null));
        var emptyList = await OkApplyAsync(
            await kit.ApplyAsync(ex.T, 120m, 120m, ex.K, Kit.Session42, memberIds: []));

        withoutField.Should().Be(new ApplyDto(ex.K, 120m, 10m),
            "memberIds yoksa havuz S(a)'nın toplamı; kayıtlı anahtar member-ids-required almaz (§4.2)");
        explicitNull.Should().Be(withoutField, "açık null ile alanın yokluğu aynı sayılır");
        emptyList.Should().Be(withoutField, "yok ile boş liste aynı sayılır (plan fark 18f)");
    }

    [Fact]
    public async Task Havuz_kipinde_oynatma_kural_ihlalinde_de_200()
    {
        var (kit, ex) = await PoolExample1Async();

        // W = {T}: Y = 10 W dışında → kural 3b ihlali; oynatma yine de 200 (fark 18c).
        var onlyOwner = await OkApplyAsync(
            await kit.ApplyAsync(ex.T, 120m, 120m, ex.K, Kit.Session42, [ex.T]));
        onlyOwner.Should().Be(new ApplyDto(ex.K, 120m, 0m),
            "kayıtlı anahtar customer-not-synced almaz; RemainingBalance = Σ_P = T'nin 0'ı (§4.2)");

        // I = 5 W dışında (3b ihlali), Y W'de: Σ_P = Y'nin 10'u. Satır kipinin cevabı T'nin 0'ı olurdu.
        await kit.FundAsync(ex.I, 5m);
        var ownerAndY = await OkApplyAsync(
            await kit.ApplyAsync(ex.T, 120m, 120m, ex.K, Kit.Session42, [ex.T, ex.Y]));
        ownerAndY.Should().Be(new ApplyDto(ex.K, 120m, 10m),
            "Σ, kural 1 ve 3 uygulanmadan kural 4'ün P'sinden: I'nin 5'i havuza girmez");
    }

    [Fact]
    public async Task Havuz_kipinde_kopya_Id_ile_oynatma()
    {
        var (kit, ex) = await PoolExample1Async();
        var copy = Guid.NewGuid();
        await kit.Sales.SeedProjectionAsync(copy, mergedInto: ex.T);

        var body = await OkApplyAsync(
            await kit.ApplyAsync(copy, 120m, 120m, ex.K, Kit.Session42, [copy, ex.I, ex.Y]));

        body.Should().Be(new ApplyDto(ex.K, 120m, 10m),
            "kopya Id'si asıl kayda (T) çözülür: aynı kişinin aynı isteği, havuz S(T) (A5b, §4.2)");
    }

    [Fact]
    public async Task Satir_kipinde_oynatma_RemainingBalance_asil_kaydin_bakiyesi()
    {
        var kit = await Kit.CreateAsync(_factory);
        var ex = await kit.Example1Async();

        var body = await OkApplyAsync(
            await kit.ApplyAsync(ex.T, 120m, 120m, ex.K, Kit.Session42, [ex.T, ex.I, ex.Y]));

        body.Should().Be(new ApplyDto(ex.K, 120m, 0m),
            "satır kipinde RemainingBalance bugünkü gibi asıl kaydın (T) bakiyesi; memberIds okunmaz");
    }

    /// <summary>
    /// Satır kipinde geçerli <c>memberIds</c> okunmaz. Tek kabul edilen fark (Not 6, fark 18f'nin apply karşılığı):
    /// Guid'e bağlanamayan <c>memberIds</c> (bozuk eleman ya da dizi olmayan değer) gövde bağlamasında, kip
    /// okunmadan <c>[ApiController]</c>'ın 400 <c>ValidationProblem</c>'ını alır ve hiçbir şey yazılmaz.
    /// </summary>
    [Fact]
    public async Task Satir_kipinde_memberIds_okunmaz_bozugu_400()
    {
        var kit = await Kit.CreateAsync(_factory);
        var a = await kit.MemberAsync(null, 50m);
        var key = Guid.NewGuid();

        var body = await OkApplyAsync(
            await kit.ApplyAsync(a, 30m, 30m, key, saleScope: null, memberIds: [Guid.NewGuid()]));

        body.Should().Be(new ApplyDto(key, 30m, 20m), "bilinmeyen üye Id'si satır kipinde hiçbir şeyi değiştirmez");
        var row = await kit.Sales.ReadDbAsync(db =>
            db.CustomerBalanceTransactions.AsNoTracking().SingleAsync(t => t.Id == key));
        row.WpfCustomerId.Should().Be(a);
        row.Amount.Should().Be(-30m);
        row.DrawGroupId.Should().BeNull("satır kipinde çapa tek bacaklı, DrawGroupId NULL (R02)");
        (await kit.BalanceAsync(a)).Should().Be(20m);

        foreach (object malformedMemberIds in new object[] { new[] { "bozuk" }, "bozuk" })
        {
            var badKey = Guid.NewGuid();
            var malformed = await kit.SendAsync(HttpMethod.Post,
                $"/api/v1/licenses/{kit.LicenseId}/customer-balance/apply",
                new
                {
                    wpfCustomerId = a, amount = 10m, productTotal = 10m, idempotencyKey = badKey,
                    saleScope = (string?)null, memberIds = malformedMemberIds,
                });

            malformed.StatusCode.Should().Be(HttpStatusCode.BadRequest,
                "bozuk memberIds gövde bağlamasında, kip okunmadan düşer; satır kipinde de 400 (Not 6)");
            (await kit.Sales.ReadDbAsync(db =>
                db.CustomerBalanceTransactions.AsNoTracking().AnyAsync(t => t.Id == badKey)))
                .Should().BeFalse("bağlama hatasında eyleme girilmez, hiçbir şey yazılmaz");
        }
        (await kit.BalanceAsync(a)).Should().Be(20m, "bozuk istekler bakiyeye dokunmaz");
    }

    [Fact]
    public async Task Havuz_kipinde_A11_content_conflict_degismez()
    {
        var (kit, ex) = await PoolExample1Async();

        var resp = await kit.ApplyAsync(ex.T, 120m, 999m, ex.K, Kit.Session42, [ex.T, ex.I, ex.Y]);

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Kit.TitleAsync(resp)).Should().Be("content-conflict",
            "OriginalAmount çapada karşılaştırılır; havuz kipi A11'i değiştirmez (§4.2)");
        (await kit.BalanceAsync(ex.Y)).Should().Be(10m);
    }
}

/// <summary>
/// Havuz kipi + satış senkronu mod On: prod'da <c>enable</c>'dan sonra görülecek tek birleşim (lisanslar mod On'da).
/// <c>/scope</c>'ta havuz sürüm kapısı yok (plan fark 4); satış senkronu kapısı yerinde ve sırası değişmez: eski
/// istemci <c>/scope</c>'ta da kayıtlı anahtarın oynatmasında da bakiyeyi okumadan 409 <c>client-update-required</c>
/// alır (§6.5-3). Güncel istemcide <c>/scope</c> <c>PersonPool = true</c>, oynatma o anki havuzu döner.
/// </summary>
public sealed class PersonBalancePoolReadersModeOnTests : IClassFixture<PersonBalanceConfiguredApiFactory>
{
    private readonly PersonBalanceConfiguredApiFactory _factory;

    public PersonBalancePoolReadersModeOnTests(PersonBalanceConfiguredApiFactory factory) => _factory = factory;

    private sealed record ScopeDto(Guid TransactionId, decimal AppliedAmount, decimal ProductTotal, DateTimeOffset CreatedAt, bool PersonPool);
    private sealed record ApplyDto(Guid TransactionId, decimal AppliedAmount, decimal RemainingBalance);

    /// <summary>Örnek 1'in sonucu (T 0, I 0, Y 10; K çapa T'de), satış senkronu mod On ve lisans havuz kipinde.</summary>
    private async Task<(Kit, Kit.Example1)> PoolExample1ModeOnAsync()
    {
        var kit = await Kit.CreateAsync(_factory, SalesSyncMode.On);
        var ex = await kit.Example1Async();
        await kit.EnablePoolAsync();
        return (kit, ex);
    }

    [Fact]
    public async Task Mod_On_havuz_kipinde_scope_PersonPool_true()
    {
        var (kit, ex) = await PoolExample1ModeOnAsync();

        var resp = await kit.ScopeAsync(ex.T, Kit.Session42);

        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        (await resp.Content.ReadFromJsonAsync<ScopeDto>()).Should().BeEquivalentTo(new
        {
            TransactionId = ex.K, AppliedAmount = 120m, ProductTotal = 120m, PersonPool = true,
        }, "güncel istemci satış senkronu kapısını geçer; yüklem ve −Σ(grup) mod Off'takiyle aynı (§4.3)");
    }

    [Fact]
    public async Task Mod_On_havuz_kipinde_oynatma_RemainingBalance_havuz()
    {
        var (kit, ex) = await PoolExample1ModeOnAsync();

        var resp = await kit.ApplyAsync(ex.T, 120m, 120m, ex.K, Kit.Session42, [ex.T, ex.I, ex.Y]);

        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        (await resp.Content.ReadFromJsonAsync<ApplyDto>()).Should().Be(new ApplyDto(ex.K, 120m, 10m),
            "kapıdan sonra ön oynatma o anki havuzu döner, kapsam kuralına (§6.6) inmez (§4.2)");
        (await kit.BalanceAsync(ex.Y)).Should().Be(10m, "oynatma hiçbir şey yazmaz");
    }

    [Fact]
    public async Task Mod_On_havuz_kipinde_eski_istemci_scope_ve_oynatma_409_client_update_required()
    {
        var (kit, ex) = await PoolExample1ModeOnAsync();

        var scope = await kit.SendAsync(HttpMethod.Get,
            $"/api/v1/licenses/{kit.LicenseId}/customer-balance/scope?wpfCustomerId={ex.T}&saleScope={Uri.EscapeDataString(Kit.Session42)}",
            userAgent: Kit.OldUserAgent);
        var replay = await kit.ApplyAsync(
            ex.T, 120m, 120m, ex.K, Kit.Session42, [ex.T, ex.I, ex.Y], Kit.OldUserAgent);

        scope.StatusCode.Should().Be(HttpStatusCode.Conflict, await scope.Content.ReadAsStringAsync());
        (await Kit.TitleAsync(scope)).Should().Be("client-update-required",
            "havuz kipinde de satış senkronu kapısı /scope'ta yerinde (plan fark 4: havuz kapısı burada yok)");
        replay.StatusCode.Should().Be(HttpStatusCode.Conflict, await replay.Content.ReadAsStringAsync());
        (await Kit.TitleAsync(replay)).Should().Be("client-update-required",
            "kapı bakiyenin her okumasından, oynatma dahil, önce (§6.5-3)");
        (await kit.BalanceAsync(ex.Y)).Should().Be(10m);
        (await kit.BalanceAsync(ex.I)).Should().Be(0m);
    }
}
