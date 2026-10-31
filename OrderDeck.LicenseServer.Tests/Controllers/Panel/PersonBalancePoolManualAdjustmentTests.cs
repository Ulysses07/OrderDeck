using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;
using Kit = OrderDeck.LicenseServer.Tests.Controllers.Licenses.PersonBalanceTestKit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Panel;

/// <summary>
/// K5 — panel eksi <c>manual-adjustment</c> (spec 2026-10-11 §4.8, §4.13, M7, M12). Havuz kipinde eksi ayar kişinin
/// havuzundan çok bacaklı çekimdir: çapa rota satırının asıl kaydında (<c>Reason</c> yalnız orada), kalan S'nin öbür
/// pozitif üyelerinden (silinmişler önce); havuz yetmezse hiçbir şey yazılmaz → 409 <c>insufficient-balance</c>.
/// Artı tutar iki kipte, eksi tutar satır kipinde bugünkü gibi (R47). Açıklamalar üretilir. Çakışma ve 503 yalnız
/// Testcontainers'ta kanıtlanır (InMemory'de SaveChanges atomik değil).
/// </summary>
public sealed class PersonBalancePoolManualAdjustmentTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public PersonBalancePoolManualAdjustmentTests(ApiFactory factory) => _factory = factory;

    private sealed record BalanceDto(Guid WpfCustomerId, Guid LicenseId, decimal Balance, DateTimeOffset UpdatedAt);
    private sealed record TransactionDto(Guid Id, decimal Amount, string Kind, decimal? OriginalAmount,
        decimal? ShippingDeducted, string? Reason, Guid? ReversesTransactionId, DateTimeOffset CreatedAt);
    private sealed record MemberDto(Guid WpfCustomerId, decimal Balance, bool Purged);
    private sealed record Response(BalanceDto Balance, TransactionDto[] Transactions, MemberDto[] Members);

    private static string NewReason() => $"r-{Guid.NewGuid():N}";

    /// <summary>Yanıt gövdesiz (<c>Ok()</c>): çapa, üretilmiş açıklamasıyla bulunur (açıklama yalnız çapada).</summary>
    private static Task<Guid> AnchorIdAsync(Kit kit, string reason) =>
        kit.Sales.ReadDbAsync(db => db.CustomerBalanceTransactions.AsNoTracking()
            .Where(t => t.LicenseId == kit.LicenseId && t.Kind == Kit.KindManual && t.Reason == reason)
            .Select(t => t.Id)
            .SingleAsync());

    private static Task<List<CustomerBalanceTransaction>> RowsWithReasonAsync(Kit kit, string reason) =>
        kit.Sales.ReadDbAsync(db => db.CustomerBalanceTransactions.AsNoTracking()
            .Where(t => t.LicenseId == kit.LicenseId && t.Reason == reason)
            .ToListAsync());

    private static Task<int> TransactionCountAsync(Kit kit) =>
        kit.Sales.ReadDbAsync(db => db.CustomerBalanceTransactions.AsNoTracking()
            .CountAsync(t => t.LicenseId == kit.LicenseId));

    /// <summary>Asıl kayda birleşmiş bir kopya (A5b): grubu ve bakiye satırı yok.</summary>
    private static async Task<Guid> CopyOfAsync(Kit kit, Guid canonical)
    {
        var id = Guid.NewGuid();
        await kit.Sales.WithDbAsync(db =>
        {
            db.WpfCustomerProjections.Add(new WpfCustomerProjection
            {
                Id = id, LicenseId = kit.LicenseId, Platform = "youtube", Username = $"k{Guid.NewGuid():N}",
                MergedIntoId = canonical, UpdatedAt = DateTimeOffset.UtcNow,
            });
            return Task.CompletedTask;
        });
        return id;
    }

    [Fact]
    public async Task K5_ornegi_A10_B40_rota_A_eksi30()
    {
        // Spec §4.8 örneği: çapa (A, −10, Reason), bacak (B, −20) → A = 0, B = 20.
        var kit = await Kit.CreateAsync(_factory);
        await kit.EnablePoolAsync();
        var g = Kit.NewGroup();
        var a = await kit.MemberAsync(g, 10m);
        var b = await kit.MemberAsync(g, 40m);
        var reason = NewReason();

        var resp = await kit.PanelManualAdjustmentAsync(a, -30m, reason);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var anchor = await AnchorIdAsync(kit, reason);
        var rows = await kit.GroupRowsAsync(anchor);
        rows.Should().HaveCount(2);
        rows[0].Should().BeEquivalentTo(new
        {
            Id = anchor, WpfCustomerId = a, Amount = -10m, Kind = Kit.KindManual, Reason = reason,
            DrawGroupId = (Guid?)anchor, OriginalAmount = (decimal?)null, SaleScope = (string?)null,
            ReversesTransactionId = (Guid?)null,
        });
        rows[1].Should().BeEquivalentTo(new
        {
            WpfCustomerId = b, Amount = -20m, Kind = Kit.KindManual, Reason = (string?)null,
            DrawGroupId = (Guid?)anchor,
        });
        rows.Select(r => r.CreatedAt).Distinct().Should().ContainSingle("tek SaveChanges, ortak CreatedAt");
        rows.Should().OnlyContain(r => r.CreatedByCustomerId == kit.CustomerId);
        (await kit.BalanceAsync(a)).Should().Be(0m);
        (await kit.BalanceAsync(b)).Should().Be(20m);
        (await kit.LedgerHoldsAsync(a)).Should().BeTrue("D1");
        (await kit.LedgerHoldsAsync(b)).Should().BeTrue("D1");
    }

    [Fact]
    public async Task Havuz_yetmezse_409_insufficient_balance_hicbir_sey_yazilmaz()
    {
        // Σ_S = 10 + 40 − 20 = 30 < 40: eksi üye havuzu küçültür; kısmi ayar yok (hep-ya-hiç).
        var kit = await Kit.CreateAsync(_factory);
        await kit.EnablePoolAsync();
        var g = Kit.NewGroup();
        var a = await kit.MemberAsync(g, 10m);
        var b = await kit.MemberAsync(g, 40m);
        var c = await kit.MemberAsync(g, -20m);
        var before = await TransactionCountAsync(kit);

        var resp = await kit.PanelManualAdjustmentAsync(a, -40m, NewReason());

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Kit.TitleAsync(resp)).Should().Be("insufficient-balance");
        (await TransactionCountAsync(kit)).Should().Be(before, "hiçbir şey yazılmaz");
        (await kit.BalanceAsync(a)).Should().Be(10m);
        (await kit.BalanceAsync(b)).Should().Be(40m);
        (await kit.BalanceAsync(c)).Should().Be(-20m);
    }

    [Fact]
    public async Task Havuz_tam_yeterse_eksi_uyeye_dokunulmaz()
    {
        // Sınır: max(0, Σ_S) = 30 = |tutar| → yeter. Para yalnız pozitif üyelerden; C'nin eksisi yalnız toplamda.
        var kit = await Kit.CreateAsync(_factory);
        await kit.EnablePoolAsync();
        var g = Kit.NewGroup();
        var a = await kit.MemberAsync(g, 10m);
        var b = await kit.MemberAsync(g, 40m);
        var c = await kit.MemberAsync(g, -20m);
        var reason = NewReason();

        (await kit.PanelManualAdjustmentAsync(a, -30m, reason)).StatusCode.Should().Be(HttpStatusCode.OK);

        var rows = await kit.GroupRowsAsync(await AnchorIdAsync(kit, reason));
        rows.Select(r => (r.WpfCustomerId, r.Amount)).Should().Equal((a, -10m), (b, -20m));
        (await kit.BalanceAsync(a)).Should().Be(0m);
        (await kit.BalanceAsync(b)).Should().Be(20m);
        (await kit.BalanceAsync(c)).Should().Be(-20m);
    }

    [Fact]
    public async Task Silinmis_uye_canlidan_once()
    {
        // M7: çapa → silinmiş üyeler → canlı üyeler. P (silinmiş, 35) B'den (canlı, 40) küçük olsa da önce çekilir.
        // Bakiye sırası tek başına B'yi seçerdi; P'yi yalnız silinmiş-önce kuralı seçtirebilir (eşitlik kırıcıya kalmaz).
        var kit = await Kit.CreateAsync(_factory);
        await kit.EnablePoolAsync();
        var g = Kit.NewGroup();
        var a = await kit.MemberAsync(g);
        var p = await kit.MemberAsync(g, 35m, purged: true);
        var b = await kit.MemberAsync(g, 40m);
        var bBefore = (await kit.BalanceRowAsync(b))!;
        var reason = NewReason();

        (await kit.PanelManualAdjustmentAsync(a, -30m, reason)).StatusCode.Should().Be(HttpStatusCode.OK);

        var rows = await kit.GroupRowsAsync(await AnchorIdAsync(kit, reason));
        rows.Select(r => (r.WpfCustomerId, r.Amount)).Should().Equal((a, 0m), (p, -30m));
        rows[0].Reason.Should().Be(reason);
        rows[1].Reason.Should().BeNull();
        (await kit.BalanceAsync(a)).Should().Be(0m);
        (await kit.BalanceAsync(p)).Should().Be(5m);
        var bAfter = (await kit.BalanceRowAsync(b))!;
        bAfter.Balance.Should().Be(40m, "canlı üyeye sıra gelmedi");
        bAfter.UpdatedAt.Should().BeAfter(bBefore.UpdatedAt,
            "kişi kilidi: S'nin bakiye satırı olan bütün üyelerinin jetonuna dokunulur (§4.13, R56)");
        (await kit.LedgerHoldsAsync(p)).Should().BeTrue("D1");
    }

    [Fact]
    public async Task Havuz_kipinde_kurus_alti_eksi_400_invalid_money_scale_hicbir_sey_yazilmaz()
    {
        // Havuz (50) tutarı karşılar; tek engel ölçek. DrawAsync kuruş altı Requested'ta istisna atar (Görev 4):
        // denetleyici çağırmadan önce apply'ın emsaliyle 400 döner, 500 değil (spec ↔ kod notu).
        var kit = await Kit.CreateAsync(_factory);
        await kit.EnablePoolAsync();
        var g = Kit.NewGroup();
        var a = await kit.MemberAsync(g, 10m);
        var b = await kit.MemberAsync(g, 40m);
        var before = await TransactionCountAsync(kit);

        var resp = await kit.PanelManualAdjustmentAsync(a, -10.005m, NewReason());

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Kit.TitleAsync(resp)).Should().Be("invalid-money-scale");
        (await TransactionCountAsync(kit)).Should().Be(before, "hiçbir şey yazılmaz");
        (await kit.BalanceAsync(a)).Should().Be(10m);
        (await kit.BalanceAsync(b)).Should().Be(40m);
    }

    [Fact]
    public async Task Rota_satirinin_bakiye_satiri_yoksa_0_capa()
    {
        var kit = await Kit.CreateAsync(_factory);
        await kit.EnablePoolAsync();
        var g = Kit.NewGroup();
        var a = await kit.MemberAsync(g, balanceRow: false);
        var b = await kit.MemberAsync(g, 40m);
        (await kit.BalanceRowAsync(a)).Should().BeNull();
        var reason = NewReason();

        (await kit.PanelManualAdjustmentAsync(a, -30m, reason)).StatusCode.Should().Be(HttpStatusCode.OK);

        var opened = await kit.BalanceRowAsync(a);
        opened.Should().NotBeNull("çapanın bakiye satırı 0 ile açılır ve kilide girer (§4.2-5)");
        opened!.Balance.Should().Be(0m);
        var rows = await kit.GroupRowsAsync(await AnchorIdAsync(kit, reason));
        rows.Select(r => (r.WpfCustomerId, r.Amount)).Should().Equal((a, 0m), (b, -30m));
        (await kit.BalanceAsync(b)).Should().Be(10m);
        (await kit.LedgerHoldsAsync(a)).Should().BeTrue("D1");
        (await kit.LedgerHoldsAsync(b)).Should().BeTrue("D1");
    }

    [Fact]
    public async Task Rota_kopya_Id_ise_asil_kayit_capadir()
    {
        var kit = await Kit.CreateAsync(_factory);
        await kit.EnablePoolAsync();
        var g = Kit.NewGroup();
        var a = await kit.MemberAsync(g, 10m);
        var b = await kit.MemberAsync(g, 40m);
        var copy = await CopyOfAsync(kit, a);
        var reason = NewReason();

        (await kit.PanelManualAdjustmentAsync(copy, -30m, reason)).StatusCode.Should().Be(HttpStatusCode.OK);

        var rows = await kit.GroupRowsAsync(await AnchorIdAsync(kit, reason));
        rows.Select(r => (r.WpfCustomerId, r.Amount)).Should().Equal((a, -10m), (b, -20m));
        (await kit.BalanceRowAsync(copy)).Should().BeNull("kopyaya asla bakiye satırı açılmaz (A5b)");
        (await kit.Sales.ReadDbAsync(db => db.CustomerBalanceTransactions.AsNoTracking()
            .AnyAsync(t => t.WpfCustomerId == copy))).Should().BeFalse();
    }

    [Fact]
    public async Task Arti_tutar_havuz_kipinde_de_bugunku_gibi()
    {
        var kit = await Kit.CreateAsync(_factory);
        await kit.EnablePoolAsync();
        var g = Kit.NewGroup();
        var a = await kit.MemberAsync(g, 10m);
        var b = await kit.MemberAsync(g, 40m);
        var reason = NewReason();

        (await kit.PanelManualAdjustmentAsync(a, 25m, reason)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await RowsWithReasonAsync(kit, reason)).Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            WpfCustomerId = a, Amount = 25m, Kind = Kit.KindManual, DrawGroupId = (Guid?)null,
        }, "artı tutar iki kipte rota satırına tek satır (R47)");
        (await kit.BalanceAsync(a)).Should().Be(35m);
        (await kit.BalanceAsync(b)).Should().Be(40m);
    }

    [Fact]
    public async Task Satir_kipinde_eksi_bugunku_gibi()
    {
        // Bayrak boş: kişinin havuzu (50) yetse de yalnız rota satırı (10) sayılır.
        var kit = await Kit.CreateAsync(_factory);
        var g = Kit.NewGroup();
        var a = await kit.MemberAsync(g, 10m);
        var b = await kit.MemberAsync(g, 40m);
        var before = await TransactionCountAsync(kit);

        var refused = await kit.PanelManualAdjustmentAsync(a, -30m, NewReason());

        refused.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Kit.TitleAsync(refused)).Should().Be("insufficient-balance");
        (await TransactionCountAsync(kit)).Should().Be(before);

        var reason = NewReason();
        (await kit.PanelManualAdjustmentAsync(a, -5m, reason)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await RowsWithReasonAsync(kit, reason)).Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            WpfCustomerId = a, Amount = -5m, Kind = Kit.KindManual, DrawGroupId = (Guid?)null,
        }, "satır kipi tek satır yazar, DrawGroupId NULL");
        (await kit.BalanceAsync(a)).Should().Be(5m);
        (await kit.BalanceAsync(b)).Should().Be(40m);
        (await kit.LedgerHoldsAsync(a)).Should().BeTrue("D1");
    }

    [Fact]
    public async Task Panel_listesinde_tek_grup()
    {
        // §4.6 havuz kipi: grup çapa Id'siyle tek satır, tutarı S'deki bacakların toplamı.
        var kit = await Kit.CreateAsync(_factory);
        await kit.EnablePoolAsync();
        var g = Kit.NewGroup();
        var a = await kit.MemberAsync(g, 10m);
        await kit.MemberAsync(g, 40m);
        var reason = NewReason();
        (await kit.PanelManualAdjustmentAsync(a, -30m, reason)).StatusCode.Should().Be(HttpStatusCode.OK);
        var anchor = await AnchorIdAsync(kit, reason);

        var list = (await (await kit.PanelGetAsync(a)).Content.ReadFromJsonAsync<Response>())!;

        list.Balance.Balance.Should().Be(20m, "havuz kipinde Σ_S");
        list.Transactions.Where(t => t.Kind == Kit.KindManual).Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new { Id = anchor, Amount = -30m, Reason = reason });
        list.Transactions.Sum(t => t.Amount).Should().Be(list.Balance.Balance);
    }
}
