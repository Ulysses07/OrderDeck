using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OrderDeck.LicenseServer.Controllers.Licenses;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;
using Kit = OrderDeck.LicenseServer.Tests.Controllers.Licenses.PersonBalanceTestKit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Licenses;

/// <summary>
/// Ortak bakiye S2, havuzlu apply (spec 2026-10-11 §4.2): çekim <c>PersonBalance.DrawAsync</c>'ten geçer, para
/// <c>P</c>'den çok bacaklı yazılır (Örnek 1, Örnek 2; Örnek 3 ve §5.4 KVKK'nın geri alması; §5.2 yanlış birleştirme)
/// ve WPF reverse'e havuz sürüm kapısı konmaz (§7.5); sıra doğrulamalar →
/// sahiplik → satış kapısı → asıl kayıt → yeniden oynatma → havuz sürüm kapısı → <c>memberIds</c> → <c>P</c>
/// (plan farkları 3, 4). §6.6 kapsam kuralı çapa düzeyinde ve tutarı <c>−Σ(grup)</c> (§15-2). Bayrak boşken satır
/// kipi birebir (R02: <c>DrawGroupId</c> NULL). InMemory: yarış, PK, 1205 ve 503 Görev 14–16'da (Testcontainers).
/// </summary>
public sealed class PersonBalancePoolApplyTests
    : IClassFixture<PersonBalanceConfiguredApiFactory>, IClassFixture<LogApiFactory>
{
    private readonly PersonBalanceConfiguredApiFactory _factory;
    private readonly LogApiFactory _unconfigured;

    public PersonBalancePoolApplyTests(PersonBalanceConfiguredApiFactory factory, LogApiFactory unconfigured)
    {
        _factory = factory;
        _unconfigured = unconfigured;
    }

    // ── Örnekler (spec §4.2, §4.5, §5.2, §5.4) ──────────────────────────────

    /// <summary>
    /// Örnek 1'in YAZIMI (§4.2): T 0, I 100, Y 30; 120 → K (T, 0,00, OA 120, session:42, DG K), L1 (I, −100),
    /// L2 (Y, −20); bacaklarda açıklama yok, ortak CreatedAt; yanıt (K, 120, 10); transactions/K ve scope okumaları
    /// grubun toplamını verir; scope(I) 204 (bacak kapsam taşımaz). D1 üç satırda.
    /// </summary>
    [Fact]
    public async Task Ornek1_apply_satirlari_birebir()
    {
        var kit = await Kit.CreateAsync(_factory);
        await kit.EnablePoolAsync();

        var ex = await Ornek1ApplyAsync(kit);

        var rows = await kit.GroupRowsAsync(ex.K);
        rows.Should().HaveCount(3);
        rows[0].Should().BeEquivalentTo(new
        {
            Id = ex.K, LicenseId = kit.LicenseId, WpfCustomerId = ex.T, Amount = 0m, Kind = Kit.KindDeduction,
            OriginalAmount = (decimal?)120m, SaleScope = Kit.Session42, Reason = (string?)null,
            ReversesTransactionId = (Guid?)null, DrawGroupId = (Guid?)ex.K, CreatedByCustomerId = kit.CustomerId,
        });
        rows.Single(r => r.WpfCustomerId == ex.I).Should().BeEquivalentTo(new
        {
            Amount = -100m, Kind = Kit.KindDeduction, OriginalAmount = (decimal?)null, SaleScope = (string?)null,
            Reason = (string?)null, ReversesTransactionId = (Guid?)null, DrawGroupId = (Guid?)ex.K,
        });
        rows.Single(r => r.WpfCustomerId == ex.Y).Should().BeEquivalentTo(new
        {
            Amount = -20m, Kind = Kit.KindDeduction, OriginalAmount = (decimal?)null, SaleScope = (string?)null,
            Reason = (string?)null, ReversesTransactionId = (Guid?)null, DrawGroupId = (Guid?)ex.K,
        });
        rows.Skip(1).Should().OnlyContain(r => r.Id != ex.K, "bacak Id'si sunucunun ürettiği yeni Guid");
        rows.Select(r => r.CreatedAt).Distinct().Should().ContainSingle("çekim tek anda yazılır (§3.2)");

        (await kit.BalanceAsync(ex.T)).Should().Be(0m);
        (await kit.BalanceAsync(ex.I)).Should().Be(0m);
        (await kit.BalanceAsync(ex.Y)).Should().Be(10m);
        foreach (var row in new[] { ex.T, ex.I, ex.Y })
            (await kit.LedgerHoldsAsync(row)).Should().BeTrue("D1");

        var tx = await kit.TransactionAsync(ex.K);
        tx.StatusCode.Should().Be(HttpStatusCode.OK);
        (await tx.Content.ReadFromJsonAsync<LicensesCustomerBalanceApplyController.TransactionStatusResponse>())
            .Should().Be(new LicensesCustomerBalanceApplyController.TransactionStatusResponse(ex.K, 120m, false));

        var onAnchor = await kit.ScopeAsync(ex.T, Kit.Session42);
        onAnchor.StatusCode.Should().Be(HttpStatusCode.OK);
        (await onAnchor.Content.ReadFromJsonAsync<LicensesCustomerBalanceApplyController.ScopeResponse>())
            .Should().BeEquivalentTo(new { TransactionId = ex.K, AppliedAmount = 120m, ProductTotal = 120m, PersonPool = true });
        (await kit.ScopeAsync(ex.I, Kit.Session42)).StatusCode.Should().Be(HttpStatusCode.NoContent,
            "bacak kapsam taşımaz (Örnek 1)");
    }

    /// <summary>
    /// Örnek 2 (§4.5): Örnek 1'den sonra toplam 120'den 90'a iner; WPF K'yi geri alır, taze K′ ile apply eder.
    /// Mod On: geri alınmış K kapsam kuralını tetiklemez. K′ (T, 0), L3 (I, −90) → I 10, Y 30, havuz 40.
    /// </summary>
    [Fact]
    public async Task Ornek2_reverse_sonrasi_taze_apply()
    {
        var kit = await Kit.CreateAsync(_factory, SalesSyncMode.On);
        await kit.EnablePoolAsync();
        var ex = await Ornek1ApplyAsync(kit);
        (await kit.ReverseAsync(ex.K)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await kit.BalanceAsync(ex.I)).Should().Be(100m);
        (await kit.BalanceAsync(ex.Y)).Should().Be(30m);
        var fresh = Guid.NewGuid();

        var resp = await kit.ApplyAsync(ex.T, 90m, 90m, fresh, Kit.Session42, [ex.T, ex.I, ex.Y]);

        (await ReadAsync(resp)).Should().Be(new LicensesCustomerBalanceApplyController.ApplyResponse(fresh, 90m, 40m));
        var rows = await kit.GroupRowsAsync(fresh);
        rows.Should().HaveCount(2);
        rows[0].Should().BeEquivalentTo(new
        {
            Id = fresh, WpfCustomerId = ex.T, Amount = 0m, OriginalAmount = (decimal?)90m, SaleScope = Kit.Session42,
            DrawGroupId = (Guid?)fresh,
        });
        rows[1].Should().BeEquivalentTo(new { WpfCustomerId = ex.I, Amount = -90m, DrawGroupId = (Guid?)fresh });
        (await kit.BalanceAsync(ex.T)).Should().Be(0m);
        (await kit.BalanceAsync(ex.I)).Should().Be(10m);
        (await kit.BalanceAsync(ex.Y)).Should().Be(30m);
        foreach (var row in new[] { ex.T, ex.I, ex.Y })
            (await kit.LedgerHoldsAsync(row)).Should().BeTrue("D1");
    }

    /// <summary>§5.2 "Yanlış birleştirme — harcama olduysa": X 100, Y 50; Y'nin malı 80, sahip Y → çapa Y −50, bacak X −30.</summary>
    [Fact]
    public async Task Yanlis_birlestirme_X100_Y50_sahip_Y_80()
    {
        var kit = await Kit.CreateAsync(_factory);
        var g = Kit.NewGroup();
        var x = await kit.MemberAsync(g, 100m);
        var y = await kit.MemberAsync(g, 50m);
        await kit.EnablePoolAsync();
        var key = Guid.NewGuid();

        var resp = await kit.ApplyAsync(y, 80m, 80m, key, null, [x, y]);

        (await ReadAsync(resp)).Should().Be(new LicensesCustomerBalanceApplyController.ApplyResponse(key, 80m, 70m));
        var rows = await kit.GroupRowsAsync(key);
        rows.Should().HaveCount(2);
        rows[0].Should().BeEquivalentTo(new { Id = key, WpfCustomerId = y, Amount = -50m, DrawGroupId = (Guid?)key },
            "alıcının kendi parası önce (K1)");
        rows[1].Should().BeEquivalentTo(new { WpfCustomerId = x, Amount = -30m, DrawGroupId = (Guid?)key });
        (await kit.BalanceAsync(x)).Should().Be(70m);
        (await kit.BalanceAsync(y)).Should().Be(0m);
    }

    /// <summary>
    /// §5.4 KVKK: kişi {A 0, P silinmiş 40}; WPF silinmiş satırı göndermez (W = {A}), sunucu P'yi S'den alır.
    /// 30 → çapa A 0, bacak P −30 → P 10.
    /// </summary>
    [Fact]
    public async Task KVKK_A0_silinmis_P40_30()
    {
        var kit = await Kit.CreateAsync(_factory);
        var g = Kit.NewGroup();
        var a = await kit.MemberAsync(g);
        var p = await kit.MemberAsync(g, 40m, purged: true);
        await kit.EnablePoolAsync();
        var key = Guid.NewGuid();

        var resp = await kit.ApplyAsync(a, 30m, 30m, key, null, [a]);

        (await ReadAsync(resp)).Should().Be(new LicensesCustomerBalanceApplyController.ApplyResponse(key, 30m, 10m));
        var rows = await kit.GroupRowsAsync(key);
        rows.Should().HaveCount(2);
        rows[0].Should().BeEquivalentTo(new { Id = key, WpfCustomerId = a, Amount = 0m });
        rows[1].Should().BeEquivalentTo(new { WpfCustomerId = p, Amount = -30m, DrawGroupId = (Guid?)key });
        (await kit.BalanceAsync(p)).Should().Be(10m);
        (await kit.LedgerHoldsAsync(p)).Should().BeTrue("D1");
    }

    /// <summary>
    /// §5.4 K3'ün ikinci yarısı: <see cref="KVKK_A0_silinmis_P40_30"/>'un çekimi geri alınırsa bacak silinmiş üyeye
    /// döner (P +30 → 40, A 0) ve para yine havuzdan kullanılabilir — sunucu P'yi S'den sayar, W = {A} iken de.
    /// </summary>
    [Fact]
    public async Task KVKK_geri_alinca_para_silinmis_uyeye_doner_ve_havuzdan_kullanilir()
    {
        var kit = await Kit.CreateAsync(_factory);
        var g = Kit.NewGroup();
        var a = await kit.MemberAsync(g);
        var p = await kit.MemberAsync(g, 40m, purged: true);
        await kit.EnablePoolAsync();
        var key = Guid.NewGuid();
        (await ReadAsync(await kit.ApplyAsync(a, 30m, 30m, key, null, [a])))
            .Should().Be(new LicensesCustomerBalanceApplyController.ApplyResponse(key, 30m, 10m));
        var group = await kit.GroupRowsAsync(key);

        (await kit.ReverseAsync(key)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await kit.BalanceAsync(p)).Should().Be(40m);
        (await kit.BalanceAsync(a)).Should().Be(0m);
        var reversals = await kit.ReversalsOfAsync(group.Select(r => r.Id));
        reversals.Should().HaveCount(2);
        foreach (var leg in group)
            reversals.Single(r => r.ReversesTransactionId == leg.Id).Should().BeEquivalentTo(
                new { leg.WpfCustomerId, Amount = -leg.Amount, Kind = Kit.KindReversal },
                "her bacak kendi satırına döner; P'nin 30'u silinmiş satıra");
        foreach (var row in new[] { a, p })
            (await kit.LedgerHoldsAsync(row)).Should().BeTrue("D1");
        var preview = await kit.PreviewAsync(a, [a]);
        preview.StatusCode.Should().Be(HttpStatusCode.OK, await preview.Content.ReadAsStringAsync());
        (await preview.Content.ReadFromJsonAsync<LicensesCustomerBalanceApplyController.PreviewResponse>())
            .Should().BeEquivalentTo(new { Balance = 40m, PersonPool = true },
                "silinmiş üye W'de olmasa da S'den gelir (K3)");
    }

    /// <summary>
    /// Örnek 3 (§5.2, K1): Örnek 1'den sonra Ayır (I ve Y ayrı kişi) veri işlemi yapmaz — T 0, I 0, Y 10. Ardından
    /// K iptal edilirse (WPF reverse) her bacak ayrılmış olsa bile kendi satırına döner: T +0, I +100, Y +20 → I 100,
    /// Y 30. I'nin parası T'ye değil I'ye döner; I artık tek başına kişi, havuzu 100. D1 üç satırda.
    /// </summary>
    [Fact]
    public async Task Ornek3_ayirmadan_sonra_geri_alma_her_bacagi_kendi_satirina_dondurur()
    {
        var kit = await Kit.CreateAsync(_factory);
        await kit.EnablePoolAsync();
        var ex = await Ornek1ApplyAsync(kit);
        var group = await kit.GroupRowsAsync(ex.K);

        (await kit.SyncGroupAsync(ex.I, null)).EnsureSuccessStatusCode();
        (await kit.SyncGroupAsync(ex.Y, null)).EnsureSuccessStatusCode();
        (await kit.BalanceAsync(ex.T)).Should().Be(0m, "Ayır veri işlemi yapmaz (§5.2)");
        (await kit.BalanceAsync(ex.I)).Should().Be(0m);
        (await kit.BalanceAsync(ex.Y)).Should().Be(10m);

        (await kit.ReverseAsync(ex.K)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await kit.BalanceAsync(ex.T)).Should().Be(0m);
        (await kit.BalanceAsync(ex.I)).Should().Be(100m, "I'nin parası T'ye değil I'ye döner (K1)");
        (await kit.BalanceAsync(ex.Y)).Should().Be(30m);
        var reversals = await kit.ReversalsOfAsync(group.Select(r => r.Id));
        reversals.Should().HaveCount(3);
        var r0 = reversals.Single(r => r.ReversesTransactionId == ex.K);
        reversals.Should().OnlyContain(r => r.DrawGroupId == r0.Id && r.Kind == Kit.KindReversal,
            "geri alma tek grup: ortak DrawGroupId, geri alma çapasının Id'si");
        foreach (var leg in group)
            reversals.Single(r => r.ReversesTransactionId == leg.Id).Should().BeEquivalentTo(
                new { leg.WpfCustomerId, Amount = -leg.Amount }, "her bacak kendi satırına döner");
        foreach (var row in new[] { ex.T, ex.I, ex.Y })
            (await kit.LedgerHoldsAsync(row)).Should().BeTrue("D1");
        var preview = await kit.PreviewAsync(ex.I, [ex.I]);
        preview.StatusCode.Should().Be(HttpStatusCode.OK, await preview.Content.ReadAsStringAsync());
        (await preview.Content.ReadFromJsonAsync<LicensesCustomerBalanceApplyController.PreviewResponse>())
            .Should().BeEquivalentTo(new { Balance = 100m, PersonPool = true }, "ayrılmış I'nin havuzu yalnız kendisi");
    }

    // ── Havuz ve 409 kuralları (§4.0, §4.2 adım 1–3) ────────────────────────

    /// <summary>
    /// §4.0-4, §4.13: S∖P'nin eksisi havuza sayılır ve kilitlenir, ondan çekilmez. A 100, B −60, W = {A}; 100
    /// istenir → Σ 40, uygulanan 40, kalan 0; B −60 kalır.
    /// </summary>
    [Fact]
    public async Task Eksiler_kilitlenir_A100_B_eksi60_W_A()
    {
        var kit = await Kit.CreateAsync(_factory);
        var g = Kit.NewGroup();
        var a = await kit.MemberAsync(g, 100m);
        var b = await kit.MemberAsync(g, -60m);
        await kit.EnablePoolAsync();
        var key = Guid.NewGuid();

        var resp = await kit.ApplyAsync(a, 100m, 100m, key, null, [a]);

        (await ReadAsync(resp)).Should().Be(new LicensesCustomerBalanceApplyController.ApplyResponse(key, 40m, 0m));
        (await kit.GroupRowsAsync(key)).Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new { WpfCustomerId = a, Amount = -40m, DrawGroupId = (Guid?)key });
        (await kit.BalanceAsync(a)).Should().Be(60m);
        (await kit.BalanceAsync(b)).Should().Be(-60m, "eksi satıra yazılmaz");
    }

    /// <summary>§4.2 adım 2: Σ ≤ 0 → 409 no-balance (A 50, B −60, W = {A, B}); hiçbir şey yazılmaz.</summary>
    [Fact]
    public async Task Havuz_sifirsa_409_no_balance_yazim_yok()
    {
        var kit = await Kit.CreateAsync(_factory);
        var g = Kit.NewGroup();
        var a = await kit.MemberAsync(g, 50m);
        var b = await kit.MemberAsync(g, -60m);
        await kit.EnablePoolAsync();

        var resp = await kit.ApplyAsync(a, 30m, 30m, Guid.NewGuid(), null, [a, b]);

        await ShouldBeProblemAsync(resp, HttpStatusCode.Conflict, "no-balance");
        (await DeductionCountAsync(kit)).Should().Be(0);
        (await kit.BalanceAsync(a)).Should().Be(50m);
    }

    /// <summary>§4.0-1 kesin kol: a da W'nin hiçbir Id'si de sunucuda yok → 409 no-balance. Preview bu kolda 200
    /// <c>Balance = 0</c>, <c>PersonPool = true</c> döner (sahip kararı 1, plan fark 19; Görev 5
    /// <c>Kural1_hicbir_uye_sunucuda_yok_200_bakiye_sifir</c>); 409 yalnız apply'da.</summary>
    [Fact]
    public async Task Kural1_kesin_409_no_balance()
    {
        var kit = await Kit.CreateAsync(_factory);
        await kit.EnablePoolAsync();
        var unsynced = Guid.NewGuid();

        var resp = await kit.ApplyAsync(unsynced, 10m, 10m, Guid.NewGuid(), null, [unsynced, Guid.NewGuid()]);

        await ShouldBeProblemAsync(resp, HttpStatusCode.Conflict, "no-balance");
        (await DeductionCountAsync(kit)).Should().Be(0);
    }

    /// <summary>§4.0-3b: S'de olup W'de olmayan parası olan canlı üye (B 40) → 409 customer-not-synced; yazım yok.</summary>
    [Fact]
    public async Task Kural3b_409_customer_not_synced_yazim_yok()
    {
        var kit = await Kit.CreateAsync(_factory);
        var g = Kit.NewGroup();
        var a = await kit.MemberAsync(g);
        var b = await kit.MemberAsync(g, 40m);
        await kit.EnablePoolAsync();

        var resp = await kit.ApplyAsync(a, 10m, 10m, Guid.NewGuid(), null, [a]);

        await ShouldBeProblemAsync(resp, HttpStatusCode.Conflict, "customer-not-synced");
        (await DeductionCountAsync(kit)).Should().Be(0);
        (await kit.BalanceAsync(b)).Should().Be(40m);
    }

    /// <summary>§4.2 adım 5: çapanın bakiye satırı yoksa Balance 0 ile açılır; çapa tutarı 0, para I'den.</summary>
    [Fact]
    public async Task Capanin_bakiye_satiri_yoksa_0_ile_acilir()
    {
        var kit = await Kit.CreateAsync(_factory);
        var g = Kit.NewGroup();
        var t = await kit.MemberAsync(g, balanceRow: false);
        var i = await kit.MemberAsync(g, 50m);
        await kit.EnablePoolAsync();
        (await kit.BalanceRowAsync(t)).Should().BeNull();
        var key = Guid.NewGuid();

        var resp = await kit.ApplyAsync(t, 30m, 30m, key, null, [t, i]);

        (await ReadAsync(resp)).Should().Be(new LicensesCustomerBalanceApplyController.ApplyResponse(key, 30m, 20m));
        (await kit.BalanceRowAsync(t)).Should().NotBeNull().And.BeEquivalentTo(new { Balance = 0m });
        var rows = await kit.GroupRowsAsync(key);
        rows[0].Should().BeEquivalentTo(new { Id = key, WpfCustomerId = t, Amount = 0m });
        rows[1].Should().BeEquivalentTo(new { WpfCustomerId = i, Amount = -30m });
        (await kit.LedgerHoldsAsync(t)).Should().BeTrue("0'lık satır D1'i bozmaz");
    }

    // ── Sıra ve kapılar (§4.0 "Sürüm denetimi", plan farkları 3, 4) ──────────

    /// <summary>§4.0, §7.2: havuz kipinde Wh altı, sürümsüz ya da bozuk UA → 409 client-update-required; yazım yok.</summary>
    [Theory]
    [InlineData(Kit.OldUserAgent)]
    [InlineData(null)]
    [InlineData("OrderDeck-WPF/bozuk")]
    public async Task Eski_surum_409_client_update_required(string? userAgent)
    {
        var kit = await Kit.CreateAsync(_factory);
        var a = await kit.MemberAsync(null, 50m);
        await kit.EnablePoolAsync();

        var resp = await kit.ApplyAsync(a, 10m, 10m, Guid.NewGuid(), null, [a], userAgent);

        await ShouldBeProblemAsync(resp, HttpStatusCode.Conflict, "client-update-required");
        (await DeductionCountAsync(kit)).Should().Be(0);
        (await kit.BalanceAsync(a)).Should().Be(50m);
    }

    /// <summary>
    /// Plan fark 3 (§6.2, §8.5): sürüm kapısı memberIds denetiminden ÖNCE — memberIds göndermeyen v0.9.9/v0.9.10
    /// 400 değil 409 client-update-required alır.
    /// </summary>
    [Fact]
    public async Task Eski_surum_memberIds_siz_de_409_client_update_required()
    {
        var kit = await Kit.CreateAsync(_factory);
        var a = await kit.MemberAsync(null, 50m);
        await kit.EnablePoolAsync();

        var resp = await kit.ApplyAsync(a, 10m, 10m, Guid.NewGuid(), null, null, Kit.OldUserAgent);

        await ShouldBeProblemAsync(resp, HttpStatusCode.Conflict, "client-update-required");
    }

    /// <summary>§4.0: havuz kipinde güncel istemci memberIds'siz (yok ya da boş, plan fark 18f) → 400 member-ids-required.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task memberIds_yok_400_member_ids_required(bool empty)
    {
        var kit = await Kit.CreateAsync(_factory);
        var a = await kit.MemberAsync(null, 50m);
        await kit.EnablePoolAsync();
        IReadOnlyCollection<Guid>? memberIds = empty ? Array.Empty<Guid>() : null;

        var resp = await kit.ApplyAsync(a, 10m, 10m, Guid.NewGuid(), null, memberIds);

        await ShouldBeProblemAsync(resp, HttpStatusCode.BadRequest, "member-ids-required");
        (await DeductionCountAsync(kit)).Should().Be(0);
    }

    /// <summary>
    /// §7.2, plan fark 18j: havuz kipinde PersonBalance:MinClientVersion tanımsız → kapalı-hata 409
    /// client-update-required ve günlüğe Error (lisans Id'siyle).
    /// </summary>
    [Fact]
    public async Task Alt_sinir_tanimsiz_409_ve_hata_gunlugu()
    {
        var kit = await Kit.CreateAsync(_unconfigured);
        var a = await kit.MemberAsync(null, 50m);
        await kit.EnablePoolAsync();

        var resp = await kit.ApplyAsync(a, 10m, 10m, Guid.NewGuid(), null, [a]);

        await ShouldBeProblemAsync(resp, HttpStatusCode.Conflict, "client-update-required");
        _unconfigured.Log.Entries.Should().Contain(e => e.Level == LogLevel.Error
            && e.Message.Contains("apply") && e.Message.Contains(kit.LicenseId.ToString()));
        (await DeductionCountAsync(kit)).Should().Be(0);
    }

    /// <summary>
    /// §4.2 "İdempotency ve A11" (spec `:202`): yeniden oynatma havuz kapısından ve memberIds denetiminden ÖNCE —
    /// kayıtlı anahtar eski sürümle ve memberIds'siz de oynatılır (K, 120, Σ_S = 10); ikinci düşüm yok.
    /// </summary>
    [Fact]
    public async Task Kayitli_anahtar_eski_surumle_ve_memberIds_siz_oynatilir()
    {
        var kit = await Kit.CreateAsync(_factory);
        await kit.EnablePoolAsync();
        var ex = await Ornek1ApplyAsync(kit);

        var resp = await kit.ApplyAsync(ex.T, 120m, 120m, ex.K, Kit.Session42, null, Kit.OldUserAgent);

        (await ReadAsync(resp)).Should().Be(new LicensesCustomerBalanceApplyController.ApplyResponse(ex.K, 120m, 10m));
        (await DeductionCountAsync(kit)).Should().Be(3, "yalnız Örnek 1'in üç satırı");
        (await kit.BalanceAsync(ex.Y)).Should().Be(10m);
    }

    /// <summary>A11 (§4.2): aynı anahtar farklı ProductTotal → 409 content-conflict; yan etki yok.</summary>
    [Fact]
    public async Task Ayni_anahtar_farkli_icerik_409_content_conflict()
    {
        var kit = await Kit.CreateAsync(_factory);
        await kit.EnablePoolAsync();
        var ex = await Ornek1ApplyAsync(kit);

        var resp = await kit.ApplyAsync(ex.T, 120m, 999m, ex.K, Kit.Session42, [ex.T, ex.I, ex.Y]);

        await ShouldBeProblemAsync(resp, HttpStatusCode.Conflict, "content-conflict");
        (await DeductionCountAsync(kit)).Should().Be(3);
        (await kit.BalanceAsync(ex.Y)).Should().Be(10m);
    }

    /// <summary>
    /// §7.5 son madde (olumsuz gereksinim): WPF reverse'e havuz sürüm kapısı konmaz. Havuz kipindeki lisansta Wh
    /// altı istemci (0.9.10) çok bacaklı çekimi geri alır: 200, 409 client-update-required DEĞİL; grup geri alınır.
    /// Satış senkronu mod Off: §6.5-3 kapısı devrede değil, ölçülen yalnız havuz kapısının yokluğu. Kurulum elle
    /// tohumlanmış Örnek 1 (bu görevin apply koduna bağlı değil); kod değişmez, test kapının yokluğunu sabitler.
    /// </summary>
    [Fact]
    public async Task Havuz_kipinde_eski_surumle_WPF_reverse_200_surum_kapisi_yok()
    {
        var kit = await Kit.CreateAsync(_factory);
        var ex = await kit.Example1Async();
        await kit.EnablePoolAsync();

        var resp = await kit.SendAsync(HttpMethod.Post,
            $"/api/v1/licenses/{kit.LicenseId}/customer-balance/transactions/{ex.K}/reverse",
            userAgent: Kit.OldUserAgent);

        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        (await kit.ReversalsOfAsync([ex.K, ex.L1, ex.L2])).Should().HaveCount(3, "grup olarak geri alınır");
        (await kit.BalanceAsync(ex.T)).Should().Be(0m);
        (await kit.BalanceAsync(ex.I)).Should().Be(100m);
        (await kit.BalanceAsync(ex.Y)).Should().Be(30m);
    }

    // ── §6.6 kapsam kuralı, çapa düzeyinde (§4.2, §15-2) ────────────────────

    /// <summary>
    /// §15-2: mod On, aynı çapa×kapsam, yeni anahtar (ya da anahtarsız) → 409 scope-has-deduction; tutar
    /// <c>−Σ(grup)</c> = 120 (çapanın kendi payı 0 iken bile), productTotal 120. Bakiyeye dokunulmaz.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Mod_On_kapsam_ikinci_farkli_anahtar_409_scope_has_deduction_eksi_sigma(bool withKey)
    {
        var kit = await Kit.CreateAsync(_factory, SalesSyncMode.On);
        await kit.EnablePoolAsync();
        var ex = await Ornek1ApplyAsync(kit);

        var resp = await kit.ApplyAsync(ex.T, 50m, 50m, withKey ? Guid.NewGuid() : null, Kit.Session42, [ex.T, ex.I, ex.Y]);

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict, await resp.Content.ReadAsStringAsync());
        var problem = await SalesCommandTestKit.ProblemAsync(resp);
        problem.GetProperty("title").GetString().Should().Be("scope-has-deduction");
        problem.GetProperty("transactionId").GetGuid().Should().Be(ex.K);
        problem.GetProperty("appliedAmount").GetDecimal().Should().Be(120m, "çapa payı 0; tutar grubun toplamı");
        problem.GetProperty("productTotal").GetDecimal().Should().Be(120m);
        (await DeductionCountAsync(kit)).Should().Be(3);
        (await kit.BalanceAsync(ex.Y)).Should().Be(10m);
    }

    /// <summary>
    /// M10, §4.2 "Çapa düzeyinde kalır": aynı kişinin BAŞKA üyesine çapalı aynı kapsam 409 olmaz (#506'nın çok
    /// izli akışı). Örnek 1'den sonra I'ye çapalı 10 → K3 (I, 0), bacak (Y, −10).
    /// </summary>
    [Fact]
    public async Task Mod_On_baska_uyedeki_capa_kapsam_kuralini_tetiklemez()
    {
        var kit = await Kit.CreateAsync(_factory, SalesSyncMode.On);
        await kit.EnablePoolAsync();
        var ex = await Ornek1ApplyAsync(kit);
        var other = Guid.NewGuid();

        var resp = await kit.ApplyAsync(ex.I, 10m, 10m, other, Kit.Session42, [ex.T, ex.I, ex.Y]);

        (await ReadAsync(resp)).Should().Be(new LicensesCustomerBalanceApplyController.ApplyResponse(other, 10m, 0m));
        var rows = await kit.GroupRowsAsync(other);
        rows.Should().HaveCount(2);
        rows[0].Should().BeEquivalentTo(new { Id = other, WpfCustomerId = ex.I, Amount = 0m, SaleScope = Kit.Session42 });
        rows[1].Should().BeEquivalentTo(new { WpfCustomerId = ex.Y, Amount = -10m, SaleScope = (string?)null });
        (await kit.BalanceAsync(ex.Y)).Should().Be(0m);
    }

    /// <summary>§4.2 "Koşul": mod Off'ta kural yok — aynı kapsama iki farklı anahtar ikisi de 200; kapsam yine çapada.</summary>
    [Fact]
    public async Task Mod_Off_kapsam_kurali_yok()
    {
        var kit = await Kit.CreateAsync(_factory);
        var a = await kit.MemberAsync(null, 100m);
        await kit.EnablePoolAsync();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        (await ReadAsync(await kit.ApplyAsync(a, 30m, 30m, first, Kit.Session42, [a])))
            .Should().Be(new LicensesCustomerBalanceApplyController.ApplyResponse(first, 30m, 70m));
        (await ReadAsync(await kit.ApplyAsync(a, 30m, 30m, second, Kit.Session42, [a])))
            .Should().Be(new LicensesCustomerBalanceApplyController.ApplyResponse(second, 30m, 40m));

        foreach (var id in new[] { first, second })
            (await kit.GroupRowsAsync(id)).Should().ContainSingle().Which.Should().BeEquivalentTo(
                new { Id = id, WpfCustomerId = a, Amount = -30m, SaleScope = Kit.Session42, DrawGroupId = (Guid?)id },
                "havuz yazımı tek bacakta da DrawGroupId = Id (§4.2 adım 5)");
        (await kit.BalanceAsync(a)).Should().Be(40m);
    }

    // ── Satır kipi (R02, R70) ────────────────────────────────────────────────

    /// <summary>
    /// Bayrak boş: satır kipi birebir — memberIds verilse de okunmaz, yalnız istenen satırdan çekilir (A 100 / 120
    /// istenir → 100), tek satır, DrawGroupId NULL; B'ye dokunulmaz.
    /// </summary>
    [Fact]
    public async Task Satir_kipinde_apply_DrawGroupId_NULL()
    {
        var kit = await Kit.CreateAsync(_factory);
        var g = Kit.NewGroup();
        var a = await kit.MemberAsync(g, 100m);
        var b = await kit.MemberAsync(g, 50m);
        var key = Guid.NewGuid();

        var resp = await kit.ApplyAsync(a, 120m, 120m, key, Kit.Session42, [a, b]);

        (await ReadAsync(resp)).Should().Be(new LicensesCustomerBalanceApplyController.ApplyResponse(key, 100m, 0m));
        (await kit.GroupRowsAsync(key)).Should().ContainSingle().Which.Should().BeEquivalentTo(
            new { Id = key, WpfCustomerId = a, Amount = -100m, DrawGroupId = (Guid?)null });
        (await kit.BalanceAsync(b)).Should().Be(50m);
    }

    /// <summary>§7.4 kill switch: EnablePool → havuz yazımı (DrawGroupId dolu); DisablePool → satır kipi (NULL).</summary>
    [Fact]
    public async Task Bayrak_kapatilinca_satir_kipine_doner()
    {
        var kit = await Kit.CreateAsync(_factory);
        var g = Kit.NewGroup();
        var a = await kit.MemberAsync(g, 100m);
        var b = await kit.MemberAsync(g, 50m);
        await kit.EnablePoolAsync();
        var pooled = Guid.NewGuid();
        (await ReadAsync(await kit.ApplyAsync(a, 30m, 30m, pooled, null, [a, b])))
            .Should().Be(new LicensesCustomerBalanceApplyController.ApplyResponse(pooled, 30m, 120m));

        await kit.DisablePoolAsync();
        var plain = Guid.NewGuid();
        (await ReadAsync(await kit.ApplyAsync(a, 30m, 30m, plain, null, [a, b])))
            .Should().Be(new LicensesCustomerBalanceApplyController.ApplyResponse(plain, 30m, 40m));

        (await kit.GroupRowsAsync(pooled)).Should().ContainSingle().Which.DrawGroupId.Should().Be(pooled);
        (await kit.GroupRowsAsync(plain)).Should().ContainSingle().Which.DrawGroupId.Should().BeNull();
        (await kit.BalanceAsync(a)).Should().Be(40m);
        (await kit.BalanceAsync(b)).Should().Be(50m);
    }

    // ── Yardımcılar ───────────────────────────────────────────────────────────

    private sealed record Ornek1Sonuc(Guid T, Guid I, Guid Y, Guid K);

    /// <summary>Örnek 1'in kişisi (T 0, I 100, Y 30; kip zaten açık) ve havuzlu apply'ı: K, 120, session:42 → (K, 120, 10).</summary>
    private static async Task<Ornek1Sonuc> Ornek1ApplyAsync(Kit kit)
    {
        var g = Kit.NewGroup();
        var t = await kit.MemberAsync(g);
        var i = await kit.MemberAsync(g, 100m, platform: "instagram");
        var y = await kit.MemberAsync(g, 30m);
        var k = Guid.NewGuid();

        var resp = await kit.ApplyAsync(t, 120m, 120m, k, Kit.Session42, [t, i, y]);

        (await ReadAsync(resp)).Should().Be(new LicensesCustomerBalanceApplyController.ApplyResponse(k, 120m, 10m));
        return new Ornek1Sonuc(t, i, y, k);
    }

    private static async Task<LicensesCustomerBalanceApplyController.ApplyResponse> ReadAsync(HttpResponseMessage resp)
    {
        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        return (await resp.Content.ReadFromJsonAsync<LicensesCustomerBalanceApplyController.ApplyResponse>())!;
    }

    private static async Task ShouldBeProblemAsync(HttpResponseMessage resp, HttpStatusCode status, string title)
    {
        resp.StatusCode.Should().Be(status, await resp.Content.ReadAsStringAsync());
        (await Kit.TitleAsync(resp)).Should().Be(title);
    }

    /// <summary>Lisansın bütün düşüm satırları (çapa + bacak).</summary>
    private static Task<int> DeductionCountAsync(Kit kit) =>
        kit.Sales.ReadDbAsync(db => db.CustomerBalanceTransactions.AsNoTracking()
            .CountAsync(t => t.LicenseId == kit.LicenseId && t.Kind == Kit.KindDeduction));
}
