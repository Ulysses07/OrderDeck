using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;
using Kit = OrderDeck.LicenseServer.Tests.Controllers.Licenses.PersonBalanceTestKit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Licenses;

/// <summary>
/// Ortak bakiye S1, WPF yüzeyinin okuyucuları (spec 2026-10-11 §4.1–§4.4): çok bacaklı (S2 biçimli, elle kurulmuş)
/// bir düşüm yalnız çapada bulunur ve tutarı grubun toplamıdır; bacak Id'si çapaya çözülür ama idempotency anahtarı
/// olamaz; preview ve scope <c>PersonPool = false</c> döner. Satır kipi (mod Off).
/// </summary>
public sealed class PersonBalanceWpfReadersTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public PersonBalanceWpfReadersTests(ApiFactory factory) => _factory = factory;

    private sealed record ScopeDto(Guid TransactionId, decimal AppliedAmount, decimal ProductTotal, DateTimeOffset CreatedAt, bool PersonPool);
    private sealed record TxDto(Guid TransactionId, decimal AppliedAmount, bool Reversed);
    private sealed record PreviewDto(Guid WpfCustomerId, decimal Balance, DateTimeOffset UpdatedAt, bool PersonPool);
    private sealed record ApplyDto(Guid TransactionId, decimal AppliedAmount, decimal RemainingBalance);

    /// <summary>Gövdede <c>personPool</c> alanı gerçekten var mı (eksik alan record'da da false okunurdu).</summary>
    private static async Task<bool> PersonPoolFieldAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("personPool").GetBoolean();
    }

    [Fact]
    public async Task Ornek_1_scope_yalniz_capada_bulunur_tutari_grup_toplamidir()
    {
        var kit = await Kit.CreateAsync(_factory);
        var ex = await kit.Example1Async();

        var onAnchor = await kit.ScopeAsync(ex.T, Kit.Session42);
        onAnchor.StatusCode.Should().Be(HttpStatusCode.OK);
        (await onAnchor.Content.ReadFromJsonAsync<ScopeDto>()).Should().BeEquivalentTo(new
        {
            TransactionId = ex.K, AppliedAmount = 120m, ProductTotal = 120m, PersonPool = false,
        });
        (await PersonPoolFieldAsync(onAnchor)).Should().BeFalse("S1 kip bayrağını okumaz (§4.3)");

        (await kit.ScopeAsync(ex.I, Kit.Session42)).StatusCode.Should().Be(HttpStatusCode.NoContent,
            "bacak kapsam taşımaz (§4.3, Örnek 1)");
    }

    [Fact]
    public async Task Transactions_bacak_Id_si_capaya_cozulur_tutar_grup_toplami()
    {
        var kit = await Kit.CreateAsync(_factory);
        var ex = await kit.Example1Async();

        foreach (var id in new[] { ex.K, ex.L1, ex.L2 })
        {
            var resp = await kit.TransactionAsync(id);
            resp.StatusCode.Should().Be(HttpStatusCode.OK);
            (await resp.Content.ReadFromJsonAsync<TxDto>()).Should().Be(new TxDto(ex.K, 120m, false));
        }
    }

    [Fact]
    public async Task Transactions_capanin_geri_alma_satiri_varsa_Reversed_true()
    {
        var kit = await Kit.CreateAsync(_factory);
        var ex = await kit.Example1Async();
        await kit.Sales.WithDbAsync(db =>
        {
            db.CustomerBalanceTransactions.Add(new CustomerBalanceTransaction
            {
                Id = Guid.NewGuid(), LicenseId = kit.LicenseId, WpfCustomerId = ex.T, Amount = 0m,
                Kind = Kit.KindReversal, ReversesTransactionId = ex.K, CreatedByCustomerId = kit.CustomerId,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            return Task.CompletedTask;
        });

        (await (await kit.TransactionAsync(ex.L1)).Content.ReadFromJsonAsync<TxDto>())!.Reversed.Should().BeTrue();
    }

    [Fact]
    public async Task Eski_tek_bacakli_dusumde_scope_ve_transactions_bugunku_gibi()
    {
        var kit = await Kit.CreateAsync(_factory);
        var a = await kit.MemberAsync(null, 80m);
        var d = await kit.SeedLegacyDeductionAsync(a, 30m, 50m, Kit.Session42);

        (await (await kit.ScopeAsync(a, Kit.Session42)).Content.ReadFromJsonAsync<ScopeDto>())
            .Should().BeEquivalentTo(new { TransactionId = d, AppliedAmount = 30m, ProductTotal = 50m, PersonPool = false });
        (await (await kit.TransactionAsync(d)).Content.ReadFromJsonAsync<TxDto>()).Should().Be(new TxDto(d, 30m, false));
    }

    [Fact]
    public async Task Bacak_Id_si_idempotency_anahtari_olarak_gelirse_404()
    {
        var kit = await Kit.CreateAsync(_factory);
        var ex = await kit.Example1Async();

        var resp = await kit.ApplyAsync(ex.I, 20m, 120m, key: ex.L1, Kit.Session42);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound, "bacak anahtar olamaz (§4.2 A11)");
        (await kit.BalanceAsync(ex.I)).Should().Be(0m);
    }

    [Fact]
    public async Task Capa_anahtarinin_tekrari_grup_toplamini_oynatir_kucuk_tutar_content_conflict()
    {
        var kit = await Kit.CreateAsync(_factory);
        var ex = await kit.Example1Async();

        var replay = await kit.ApplyAsync(ex.T, 120m, 120m, key: ex.K, Kit.Session42);
        replay.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await replay.Content.ReadFromJsonAsync<ApplyDto>();
        body!.TransactionId.Should().Be(ex.K);
        body.AppliedAmount.Should().Be(120m, "çapanın kendi payı 0 — oynatma grubun toplamını döner");

        var smaller = await kit.ApplyAsync(ex.T, 100m, 120m, key: ex.K, Kit.Session42);
        smaller.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Kit.TitleAsync(smaller)).Should().Be("content-conflict", "−Σ(grup) = 120 > istenen 100 (A11)");
    }

    [Fact]
    public async Task Preview_PersonPool_false_ve_bakiye_satir_kipinde_bugunku_gibi()
    {
        var kit = await Kit.CreateAsync(_factory);
        var ex = await kit.Example1Async();

        var response = await kit.PreviewAsync(ex.Y);
        var preview = await response.Content.ReadFromJsonAsync<PreviewDto>();

        preview!.Balance.Should().Be(10m, "satır kipi: yalnız istenen satır");
        preview.PersonPool.Should().BeFalse();
        preview.WpfCustomerId.Should().Be(ex.Y);
        (await PersonPoolFieldAsync(response)).Should().BeFalse("S1 kip bayrağını okumaz (§4.1)");
    }
}

/// <summary>
/// Satış senkronu mod On (S3'ün kapsam kuralı, §4.2): 409 <c>scope-has-deduction</c>'ın tutarı çapanın değil grubun
/// toplamıdır — çapanın payı 0 olan planda WPF'in benimsediği düşüm 0 ₺ görünüp parayı ikinci kez isterdi.
/// </summary>
public sealed class PersonBalanceScopeRuleSumTests : IClassFixture<SalesSyncConfiguredApiFactory>
{
    private readonly SalesSyncConfiguredApiFactory _factory;

    public PersonBalanceScopeRuleSumTests(SalesSyncConfiguredApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Mod_On_kapsamda_cok_bacakli_dusum_varken_409_tutari_grup_toplami()
    {
        var kit = await Kit.CreateAsync(_factory, SalesSyncMode.On);
        var ex = await kit.Example1Async();

        var resp = await kit.ApplyAsync(ex.T, 50m, 120m, key: Guid.NewGuid(), Kit.Session42);

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var problem = await SalesCommandTestKit.ProblemAsync(resp);
        problem.GetProperty("title").GetString().Should().Be("scope-has-deduction");
        problem.GetProperty("transactionId").GetGuid().Should().Be(ex.K);
        problem.GetProperty("appliedAmount").GetDecimal().Should().Be(120m);
        problem.GetProperty("productTotal").GetDecimal().Should().Be(120m);
    }
}
