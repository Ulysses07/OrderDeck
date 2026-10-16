using System.Net;
using FluentAssertions;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;
using Kit = OrderDeck.LicenseServer.Tests.Controllers.Licenses.CustomerBalanceTestKit;
using SalesKit = OrderDeck.LicenseServer.Tests.Controllers.Licenses.SalesCommandTestKit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Licenses;

/// <summary>
/// <c>customer-balance</c> preview ve apply'ın satış senkronu kuralları (InMemory) — spec 2026-10-13 §4.7, §6.5-3, §6.6,
/// §8.3 "Önizleme ve apply", DM6, M8, §15-18.
/// <list type="bullet">
///   <item>Mod Off: bugünkü davranış. Kapı yok, kapsam kuralı yok; aynı kapsama yeni anahtarla ikinci düşüm yazılır.</item>
///   <item>Mod On: istemci <c>SalesSync:MinClientVersion</c>'dan eskiyse ya da alt sürüm tanımsızsa preview ve apply 409
///   <c>client-update-required</c>; bakiyeye dokunulmaz.</item>
///   <item>Mod On, kapsam dolu, anahtar yeni: aynı asıl kayıt×kapsamda geri alınmamış düşüm varsa 409
///   <c>scope-has-deduction {transactionId, appliedAmount, productTotal}</c>. <c>legacy:</c> kapsamı muaf değildir;
///   aynı anahtarın tekrarı oynatılır.</item>
/// </list>
/// Eşzamanlılık (uygulama kilidi) gerçek SQL Server'da: <see cref="CustomerBalanceScopeRuleRelationalTests"/>.
/// </summary>
public sealed class CustomerBalanceScopeRuleTests
    : IClassFixture<SalesSyncConfiguredApiFactory>, IClassFixture<ApiFactory>
{
    private readonly SalesSyncConfiguredApiFactory _configured;
    private readonly ApiFactory _unconfigured;

    public CustomerBalanceScopeRuleTests(SalesSyncConfiguredApiFactory configured, ApiFactory unconfigured)
    {
        _configured = configured;
        _unconfigured = unconfigured;
    }

    // ── Mod Off: bugünkü davranış ────────────────────────────────────────────

    /// <summary>
    /// Mod Off'ta sürüm kapısı yok: v0.9.10 istemcisi, alt sürüm tanımsızken de önizleme ve apply'ı bugünkü gibi
    /// kullanır.
    /// </summary>
    [Theory]
    [InlineData(Kit.OldUserAgent)]
    [InlineData(null)]
    public async Task Mod_Off_eski_istemci_onizleme_ve_apply_bugunku_gibi(string? userAgent)
    {
        var kit = await Kit.CreateAsync(_unconfigured, SalesSyncMode.Off, 500m);

        var preview = await kit.PreviewAsync(userAgent);
        var apply = await kit.ApplyAsync(100m, 2100m, Guid.NewGuid(), Kit.Cumulative, userAgent);

        preview.StatusCode.Should().Be(HttpStatusCode.OK, await preview.Content.ReadAsStringAsync());
        apply.StatusCode.Should().Be(HttpStatusCode.OK, await apply.Content.ReadAsStringAsync());
        (await kit.BalanceAsync()).Should().Be(400m);
    }

    /// <summary>Mod Off'ta kapsam kuralı yok: aynı kapsama yeni anahtarla ikinci apply bugünkü gibi düşer.</summary>
    [Fact]
    public async Task Mod_Off_ayni_kapsama_yeni_anahtarla_ikinci_apply_basarili()
    {
        var kit = await Kit.CreateAsync(_configured, SalesSyncMode.Off, 500m);

        var first = await kit.ApplyAsync(100m, 2100m, Guid.NewGuid(), Kit.Cumulative, Kit.OldUserAgent);
        var second = await kit.ApplyAsync(150m, 3000m, Guid.NewGuid(), Kit.Cumulative, Kit.OldUserAgent);

        first.StatusCode.Should().Be(HttpStatusCode.OK, await first.Content.ReadAsStringAsync());
        second.StatusCode.Should().Be(HttpStatusCode.OK, await second.Content.ReadAsStringAsync());
        (await kit.UnreversedDeductionIdsAsync()).Should().HaveCount(2);
        (await kit.BalanceAsync()).Should().Be(250m);
    }

    // ── Mod On: istemci sürüm kapısı ─────────────────────────────────────────

    /// <summary>
    /// §6.5-3, §8.3: mod On'da eski sürümün (ya da sürüm bildirmeyen istemcinin) önizlemesi ve apply'ı 409
    /// <c>client-update-required</c> alır; defter ve bakiye değişmez.
    /// </summary>
    [Theory]
    [InlineData(Kit.OldUserAgent)]
    [InlineData("OrderDeck-WPF/0.9.11-rc1")]
    [InlineData("Mozilla/5.0")]
    [InlineData(null)]
    public async Task Mod_On_eski_istemci_onizleme_ve_apply_409_client_update_required(string? userAgent)
    {
        var kit = await Kit.CreateAsync(_configured, SalesSyncMode.On, 500m);

        var preview = await kit.PreviewAsync(userAgent);
        var apply = await kit.ApplyAsync(100m, 2100m, Guid.NewGuid(), Kit.Cumulative, userAgent);

        await ShouldBeClientUpdateRequiredAsync(preview);
        await ShouldBeClientUpdateRequiredAsync(apply);
        (await kit.DeductionsAsync()).Should().BeEmpty();
        (await kit.BalanceAsync()).Should().Be(500m);
    }

    /// <summary>Alt sürüm tanımsızken (Ws yayımlanmamış) mod On'da güncel istemci de geçemez: kapalı-hata (§2 "Ws").</summary>
    [Fact]
    public async Task Mod_On_alt_surum_tanimsizken_onizleme_ve_apply_409_client_update_required()
    {
        var kit = await Kit.CreateAsync(_unconfigured, SalesSyncMode.On, 500m);

        var preview = await kit.PreviewAsync();
        var apply = await kit.ApplyAsync(100m, 2100m, Guid.NewGuid(), Kit.Cumulative);

        await ShouldBeClientUpdateRequiredAsync(preview);
        await ShouldBeClientUpdateRequiredAsync(apply);
        (await kit.DeductionsAsync()).Should().BeEmpty();
        (await kit.BalanceAsync()).Should().Be(500m);
    }

    /// <summary>
    /// Kapı bakiyenin her okumasından önce: daha önce uygulanmış anahtarın eski istemciden tekrarı da oynatılmaz, 409
    /// alır (oynatma bakiye okur).
    /// </summary>
    [Fact]
    public async Task Mod_On_eski_istemcinin_anahtar_tekrari_da_409_client_update_required()
    {
        var kit = await Kit.CreateAsync(_configured, SalesSyncMode.On, 500m);
        var key = Guid.NewGuid();
        (await kit.ApplyAsync(100m, 2100m, key, Kit.Cumulative)).StatusCode.Should().Be(HttpStatusCode.OK);

        var replay = await kit.ApplyAsync(100m, 2100m, key, Kit.Cumulative, Kit.OldUserAgent);

        await ShouldBeClientUpdateRequiredAsync(replay);
    }

    /// <summary>Mod On'da kapsamsız apply'a da kapı uygulanır: kapı istemci sürümüne bakar, kapsama değil.</summary>
    [Fact]
    public async Task Mod_On_eski_istemcinin_kapsamsiz_applyi_da_409()
    {
        var kit = await Kit.CreateAsync(_configured, SalesSyncMode.On, 500m);

        var apply = await kit.ApplyAsync(100m, 2100m, Guid.NewGuid(), saleScope: null, Kit.OldUserAgent);

        await ShouldBeClientUpdateRequiredAsync(apply);
        (await kit.BalanceAsync()).Should().Be(500m);
    }

    /// <summary>Kapı yalnız sahiplikten sonra: başka yayıncının lisansı mod On'da da 404.</summary>
    [Fact]
    public async Task Mod_On_baska_yayincinin_lisansi_404()
    {
        var owner = await Kit.CreateAsync(_configured, SalesSyncMode.On, 500m);
        var stranger = await Kit.CreateAsync(_configured, SalesSyncMode.On, 500m);

        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"/api/v1/licenses/{owner.LicenseId}/customer-balance/preview?wpfCustomerId={owner.WpfCustomerId}");
        request.Headers.UserAgent.ParseAdd(Kit.OldUserAgent);
        var resp = await stranger.Sales.Client.SendAsync(request);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── Mod On: kapsam başına tek düşüm (§6.6) ───────────────────────────────

    /// <summary>
    /// §6.6, DM6: aynı asıl kayıt×kapsamda geri alınmamış düşüm varken yeni anahtarlı apply 409
    /// <c>scope-has-deduction</c> alır; yanıt düşümü <c>/scope</c> ucunun biçiminde taşır. Defter ve bakiye değişmez.
    /// </summary>
    [Fact]
    public async Task Mod_On_geri_alinmamis_dusumlu_kapsamda_yeni_anahtar_409_scope_has_deduction()
    {
        var kit = await Kit.CreateAsync(_configured, SalesSyncMode.On, 500m);
        var first = Guid.NewGuid();
        (await kit.ApplyAsync(100m, 2100m, first, Kit.Cumulative)).StatusCode.Should().Be(HttpStatusCode.OK);

        var second = await kit.ApplyAsync(150m, 3000m, Guid.NewGuid(), Kit.Cumulative);

        await ShouldBeScopeHasDeductionAsync(second, first, appliedAmount: 100m, productTotal: 2100m);
        (await kit.UnreversedDeductionIdsAsync()).Should().Equal(first);
        (await kit.BalanceAsync()).Should().Be(400m);
    }

    /// <summary>Anahtarsız istek de "yeni" sayılır: kapsamlı ikinci anahtarsız apply 409.</summary>
    [Fact]
    public async Task Mod_On_anahtarsiz_ikinci_apply_409_scope_has_deduction()
    {
        var kit = await Kit.CreateAsync(_configured, SalesSyncMode.On, 500m);
        var first = await kit.ApplyAsync(100m, 2100m, key: null, Kit.Cumulative);
        first.StatusCode.Should().Be(HttpStatusCode.OK, await first.Content.ReadAsStringAsync());
        var firstId = (await Kit.ReadApplyAsync(first)).TransactionId;

        var second = await kit.ApplyAsync(100m, 2100m, key: null, Kit.Cumulative);

        await ShouldBeScopeHasDeductionAsync(second, firstId, appliedAmount: 100m, productTotal: 2100m);
        (await kit.BalanceAsync()).Should().Be(400m);
    }

    /// <summary>
    /// Meşru revizyon akışı: istemci eski düşümü geri alır, yeni toplamla taze apply yapar — kapsamda geri alınmamış
    /// düşüm kalmadığı için 200.
    /// </summary>
    [Fact]
    public async Task Mod_On_onceki_dusum_geri_alinmissa_yeni_apply_200()
    {
        var kit = await Kit.CreateAsync(_configured, SalesSyncMode.On, 500m);
        var first = Guid.NewGuid();
        (await kit.ApplyAsync(100m, 2100m, first, Kit.Cumulative)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await kit.ReverseAsync(first)).StatusCode.Should().Be(HttpStatusCode.OK);
        var second = Guid.NewGuid();

        var resp = await kit.ApplyAsync(250m, 4000m, second, Kit.Cumulative);

        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        (await kit.UnreversedDeductionIdsAsync()).Should().Equal(second);
        (await kit.BalanceAsync()).Should().Be(250m);
    }

    /// <summary>
    /// Aynı anahtarın tekrarı kapsam kuralına takılmaz: kayıtlı sonuç oynatılır (200, aynı işlem), ikinci düşüm yok.
    /// Farklı içerikli tekrar <c>content-conflict</c> alır, <c>scope-has-deduction</c> değil.
    /// </summary>
    [Fact]
    public async Task Mod_On_ayni_anahtar_tekrari_200_oynatilir_farkli_icerik_content_conflict()
    {
        var kit = await Kit.CreateAsync(_configured, SalesSyncMode.On, 500m);
        var key = Guid.NewGuid();
        (await kit.ApplyAsync(100m, 2100m, key, Kit.Cumulative)).StatusCode.Should().Be(HttpStatusCode.OK);

        var replay = await kit.ApplyAsync(100m, 2100m, key, Kit.Cumulative);
        var conflict = await kit.ApplyAsync(100m, 3000m, key, Kit.Cumulative);

        replay.StatusCode.Should().Be(HttpStatusCode.OK, await replay.Content.ReadAsStringAsync());
        var body = await Kit.ReadApplyAsync(replay);
        body.TransactionId.Should().Be(key);
        body.AppliedAmount.Should().Be(100m);
        conflict.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await SalesKit.ProblemAsync(conflict)).GetProperty("title").GetString().Should().Be("content-conflict");
        (await kit.DeductionsAsync()).Should().ContainSingle();
        (await kit.BalanceAsync()).Should().Be(400m);
    }

    /// <summary>§6.6, §15-18: <c>legacy:</c> kapsamı muaf değildir — miras işin tekrarı da kurala girer.</summary>
    [Fact]
    public async Task Mod_On_legacy_kapsami_da_409_scope_has_deduction()
    {
        var kit = await Kit.CreateAsync(_configured, SalesSyncMode.On, 500m);
        var scope = $"legacy:{Guid.NewGuid():N}";
        var first = Guid.NewGuid();
        (await kit.ApplyAsync(100m, 2100m, first, scope)).StatusCode.Should().Be(HttpStatusCode.OK);

        var second = await kit.ApplyAsync(100m, 2100m, Guid.NewGuid(), scope);

        await ShouldBeScopeHasDeductionAsync(second, first, appliedAmount: 100m, productTotal: 2100m);
        (await kit.BalanceAsync()).Should().Be(400m);
    }

    /// <summary>Kapsamsız apply kuraldan etkilenmez: kapsamlı düşümün yanında ve birbiri ardına 200.</summary>
    [Fact]
    public async Task Mod_On_kapsamsiz_apply_etkilenmez()
    {
        var kit = await Kit.CreateAsync(_configured, SalesSyncMode.On, 500m);
        (await kit.ApplyAsync(100m, 2100m, Guid.NewGuid(), Kit.Cumulative)).StatusCode.Should().Be(HttpStatusCode.OK);

        var first = await kit.ApplyAsync(50m, 2100m, Guid.NewGuid(), saleScope: null);
        var second = await kit.ApplyAsync(50m, 2100m, Guid.NewGuid(), saleScope: null);

        first.StatusCode.Should().Be(HttpStatusCode.OK, await first.Content.ReadAsStringAsync());
        second.StatusCode.Should().Be(HttpStatusCode.OK, await second.Content.ReadAsStringAsync());
        (await kit.UnreversedDeductionIdsAsync()).Should().HaveCount(3);
        (await kit.BalanceAsync()).Should().Be(300m);
    }

    /// <summary>Kural kapsam ve asıl kayıt başınadır: başka kapsam ya da başka müşteri etkilenmez.</summary>
    [Fact]
    public async Task Mod_On_baska_kapsam_ve_baska_musteri_etkilenmez()
    {
        var kit = await Kit.CreateAsync(_configured, SalesSyncMode.On, 500m);
        var sessionA = $"session:{Guid.NewGuid():N}";
        var sessionB = $"session:{Guid.NewGuid():N}";
        (await kit.ApplyAsync(100m, 2100m, Guid.NewGuid(), sessionA)).StatusCode.Should().Be(HttpStatusCode.OK);
        var other = await Kit.CreateAsync(_configured, SalesSyncMode.On, 500m);

        var otherScope = await kit.ApplyAsync(100m, 2100m, Guid.NewGuid(), sessionB);
        var otherCustomer = await other.ApplyAsync(100m, 2100m, Guid.NewGuid(), sessionA);

        otherScope.StatusCode.Should().Be(HttpStatusCode.OK, await otherScope.Content.ReadAsStringAsync());
        otherCustomer.StatusCode.Should().Be(HttpStatusCode.OK, await otherCustomer.Content.ReadAsStringAsync());
        (await kit.BalanceAsync()).Should().Be(300m);
    }

    /// <summary>
    /// A5b: kural asıl kayıt düzeyindedir — kopyanın Id'siyle gelen apply asıl kaydın düşümünü görür ve 409 alır.
    /// </summary>
    [Fact]
    public async Task Mod_On_kopya_Id_ile_gelen_apply_asil_kaydin_dusumunu_gorur()
    {
        var kit = await Kit.CreateAsync(_configured, SalesSyncMode.On, 500m);
        var alias = await kit.SeedAliasAsync();
        var first = Guid.NewGuid();
        (await kit.ApplyAsync(100m, 2100m, first, Kit.Cumulative)).StatusCode.Should().Be(HttpStatusCode.OK);

        var viaAlias = await kit.ApplyAsync(100m, 2100m, Guid.NewGuid(), Kit.Cumulative, customerId: alias);

        await ShouldBeScopeHasDeductionAsync(viaAlias, first, appliedAmount: 100m, productTotal: 2100m);
        (await kit.BalanceAsync()).Should().Be(400m);
    }

    /// <summary>Güncel istemci mod On'da önizlemeyi bugünkü gibi alır.</summary>
    [Fact]
    public async Task Mod_On_guncel_istemci_onizleme_200()
    {
        var kit = await Kit.CreateAsync(_configured, SalesSyncMode.On, 500m);

        var preview = await kit.PreviewAsync();

        preview.StatusCode.Should().Be(HttpStatusCode.OK, await preview.Content.ReadAsStringAsync());
    }

    // ── Yardımcılar ───────────────────────────────────────────────────────────

    private static async Task ShouldBeClientUpdateRequiredAsync(HttpResponseMessage resp)
    {
        resp.StatusCode.Should().Be(HttpStatusCode.Conflict, await resp.Content.ReadAsStringAsync());
        (await SalesKit.ProblemAsync(resp)).GetProperty("title").GetString().Should().Be("client-update-required");
    }

    private static async Task ShouldBeScopeHasDeductionAsync(
        HttpResponseMessage resp, Guid transactionId, decimal appliedAmount, decimal productTotal)
    {
        resp.StatusCode.Should().Be(HttpStatusCode.Conflict, await resp.Content.ReadAsStringAsync());
        var problem = await SalesKit.ProblemAsync(resp);
        problem.GetProperty("title").GetString().Should().Be("scope-has-deduction");
        problem.GetProperty("transactionId").GetGuid().Should().Be(transactionId);
        problem.GetProperty("appliedAmount").GetDecimal().Should().Be(appliedAmount);
        problem.GetProperty("productTotal").GetDecimal().Should().Be(productTotal);
    }
}
