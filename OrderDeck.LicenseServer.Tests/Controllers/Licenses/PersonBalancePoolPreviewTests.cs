using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using OrderDeck.LicenseServer.Controllers.Licenses;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;
using Kit = OrderDeck.LicenseServer.Tests.Controllers.Licenses.PersonBalanceTestKit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Licenses;

/// <summary>
/// Ortak bakiye S2, havuzlu preview (spec 2026-10-11 §4.0, §4.1, §8.3; InMemory). Havuz kipinde sıra: satış senkronu
/// kapısı → havuz sürüm kapısı (<c>PersonBalance:MinClientVersion</c>) → <c>memberIds</c> → P ve kural 1–4 (plan
/// fark 3). <c>Balance = max(0, Σ)</c>, <c>UpdatedAt</c> P'nin son para hareketi (M9, fark 18g),
/// <c>PersonPool = true</c>, yanıttaki Id istekteki Id. Bayrak boşken preview bugünkü gibi; <c>memberIds</c>
/// bağlanır ama kullanılmaz (bozuk değer 400, fark 18f). <see cref="LogApiFactory"/>, <c>PersonBalance:MinClientVersion</c>'ı tanımsız sunucudur (fark 18j).
/// Kural 1–4'ün servis düzeyindeki tablosu <c>PersonBalanceDrawSetTests</c>'te; burada uçtaki eşlemeleri.
/// </summary>
public sealed class PersonBalancePoolPreviewTests
    : IClassFixture<PersonBalanceConfiguredApiFactory>, IClassFixture<LogApiFactory>
{
    private const string ClientUpdateRequired = "client-update-required";
    private const string CustomerNotSynced = "customer-not-synced";

    private static readonly string ControllerCategory = typeof(LicensesCustomerBalanceApplyController).FullName!;

    private readonly PersonBalanceConfiguredApiFactory _configured;
    private readonly LogApiFactory _unconfigured;

    public PersonBalancePoolPreviewTests(PersonBalanceConfiguredApiFactory configured, LogApiFactory unconfigured)
    {
        _configured = configured;
        _unconfigured = unconfigured;
    }

    // ── Satır kipi ────────────────────────────────────────────────────────────

    /// <summary>
    /// Bayrak boşken preview bugünkü gibi (R20): <c>memberIds</c> bağlanır ama kullanılmaz (bilinmeyen Id ret
    /// doğurmaz), bakiye istenen satırınki, <c>PersonPool</c> false. Havuz kapısı da yok: eski sürüm üye listesi
    /// göndermeden 200 alır. Tek kabul edilen fark (fark 18f): Guid'e bağlanamayan <c>memberIds</c> model
    /// bağlamada, kip okunmadan 400 <c>ValidationProblem</c> alır; <c>member-ids-required</c> başlığını almaz.
    /// </summary>
    [Fact]
    public async Task Satir_kipinde_memberIds_yok_sayilir_PersonPool_false()
    {
        var kit = await Kit.CreateAsync(_configured);
        var g = Kit.NewGroup();
        var a = await kit.MemberAsync(g, 30m);
        await kit.MemberAsync(g, 40m);

        var preview = await OkPreviewAsync(await kit.PreviewAsync(a, [Guid.NewGuid()]));

        preview.WpfCustomerId.Should().Be(a);
        preview.Balance.Should().Be(30m);
        preview.PersonPool.Should().BeFalse();
        (await kit.PreviewAsync(a, null, Kit.OldUserAgent)).StatusCode.Should().Be(HttpStatusCode.OK,
            "satır kipinde havuz sürüm kapısı ve memberIds zorunluluğu yok");

        var malformed = await kit.SendAsync(HttpMethod.Get,
            $"/api/v1/licenses/{kit.LicenseId}/customer-balance/preview?wpfCustomerId={a}&memberIds=bozuk");
        malformed.StatusCode.Should().Be(HttpStatusCode.BadRequest, await malformed.Content.ReadAsStringAsync());
        (await Kit.TitleAsync(malformed)).Should().NotBe("member-ids-required",
            "bozuk Guid kipten bağımsız model bağlama hatasıdır, havuz kipinin üye listesi kuralı değil");
    }

    // ── Havuz kipi: P ve kural 1–4 ──────────────────────────────────────────

    /// <summary>
    /// Spec §8.3: S∖W'de yalnız sıfır ya da eksi üye varken kesişimden çekilir, eksiler Σ'ya eklenir. A = 100,
    /// B = −60, W = {A} → kullanılabilir 40.
    /// </summary>
    [Fact]
    public async Task Havuz_kipinde_kesisim_ve_eksiler_A100_B_eksi60_W_A_40()
    {
        var kit = await Kit.CreateAsync(_configured);
        var g = Kit.NewGroup();
        var a = await kit.MemberAsync(g, 100m);
        await kit.MemberAsync(g, -60m);
        await kit.EnablePoolAsync();

        var preview = await OkPreviewAsync(await kit.PreviewAsync(a, [a]));

        preview.Should().BeEquivalentTo(new { WpfCustomerId = a, Balance = 40m, PersonPool = true });
    }

    /// <summary>Spec §4.1 örneği: A = 50, B = −60 → Σ = −10 → <c>Balance = 0</c> (satır kipinde A'nın 50'si).</summary>
    [Fact]
    public async Task Ornek_4_1_A50_B_eksi60_Balance_0()
    {
        var kit = await Kit.CreateAsync(_configured);
        var g = Kit.NewGroup();
        var a = await kit.MemberAsync(g, 50m);
        var b = await kit.MemberAsync(g, -60m);
        await kit.EnablePoolAsync();

        var preview = await OkPreviewAsync(await kit.PreviewAsync(a, [a, b]));

        preview.Balance.Should().Be(0m);
        preview.PersonPool.Should().BeTrue();
    }

    /// <summary>
    /// Kopya Id'si (A5b): istek ve üye listesindeki kopya asıl kayda çözülür, havuz asıl kaydın kişisinden okunur;
    /// yanıttaki Id istekteki (kopyanın) Id'sidir (§4.1).
    /// </summary>
    [Fact]
    public async Task Kopya_Id_ile_istenince_yanit_istekteki_Id()
    {
        var kit = await Kit.CreateAsync(_configured);
        var g = Kit.NewGroup();
        var a = await kit.MemberAsync(g, 50m);
        var b = await kit.MemberAsync(g, 20m);
        var copy = Guid.NewGuid();
        await kit.Sales.SeedProjectionAsync(copy, mergedInto: a);
        await kit.EnablePoolAsync();

        var preview = await OkPreviewAsync(await kit.PreviewAsync(copy, [copy, b]));

        preview.Should().BeEquivalentTo(new { WpfCustomerId = copy, Balance = 70m, PersonPool = true });
    }

    /// <summary>
    /// Kural 1, kesin kol: ne a ne W'nin bir Id'si sunucuda → preview 200 <c>Balance = 0</c>, <c>PersonPool = true</c>
    /// (sahip kararı 1, 29.10 (b); iskelet fark 19). Satır kipinin "satır yoksa 0" emsali; bugünkü WPF bakiye ≤ 0'ı
    /// "bakiye yok" yazar, 409'u ise yutup mesajı engellerdi. Spec §4.0-1'in 409 <c>no-balance</c>'ı apply'da kalır:
    /// aynı kurulumu Görev 7'nin <c>Kural1_kesin_409_no_balance</c> testi apply'la sınar.
    /// </summary>
    [Fact]
    public async Task Kural1_hicbir_uye_sunucuda_yok_200_bakiye_sifir()
    {
        var kit = await Kit.CreateAsync(_configured);
        await kit.EnablePoolAsync();
        var unsynced = Guid.NewGuid();

        var preview = await OkPreviewAsync(await kit.PreviewAsync(unsynced, [Guid.NewGuid()]));

        preview.Should().BeEquivalentTo(new { WpfCustomerId = unsynced, Balance = 0m, PersonPool = true });
    }

    /// <summary>Kural 1, öbür kol: a sunucuda yok ama W'nin bir üyesi var → 409 <c>customer-not-synced</c> (K2).</summary>
    [Fact]
    public async Task Kural1_a_yok_ama_uye_var_409_customer_not_synced()
    {
        var kit = await Kit.CreateAsync(_configured);
        var b = await kit.MemberAsync(Kit.NewGroup(), 20m);
        await kit.EnablePoolAsync();

        var resp = await kit.PreviewAsync(Guid.NewGuid(), [b]);

        await ShouldBeProblemAsync(resp, HttpStatusCode.Conflict, CustomerNotSynced);
    }

    /// <summary>Kural 2: sunucuda projeksiyonu olmayan W Id'si atılır; ret yok.</summary>
    [Fact]
    public async Task Kural2_bilinmeyen_W_idsi_atilir_200()
    {
        var kit = await Kit.CreateAsync(_configured);
        var a = await kit.MemberAsync(Kit.NewGroup(), 30m);
        await kit.EnablePoolAsync();

        var preview = await OkPreviewAsync(await kit.PreviewAsync(a, [a, Guid.NewGuid()]));

        preview.Should().BeEquivalentTo(new { WpfCustomerId = a, Balance = 30m, PersonPool = true });
    }

    /// <summary>Kural 3a: W'de olup S(a)'da olmayan canlı üye (başka kişide) → 409 <c>customer-not-synced</c>.</summary>
    [Fact]
    public async Task Kural3a_W_de_olup_S_de_olmayan_canli_uye_409_customer_not_synced()
    {
        var kit = await Kit.CreateAsync(_configured);
        var a = await kit.MemberAsync(Kit.NewGroup(), 10m);
        var x = await kit.MemberAsync(Kit.NewGroup());
        await kit.EnablePoolAsync();

        var resp = await kit.PreviewAsync(a, [a, x]);

        await ShouldBeProblemAsync(resp, HttpStatusCode.Conflict, CustomerNotSynced);
    }

    /// <summary>
    /// Kural 3b: S(a)'da olup W'de olmayan, bakiyesi &gt; 0 canlı üye → 409 <c>customer-not-synced</c>. Kesişimle
    /// çekmek o parayı görmezden gelirdi (önizleme 0, WPF <c>MarkNoBalance</c> yazardı).
    /// </summary>
    [Fact]
    public async Task Kural3b_S_de_olup_W_de_olmayan_pozitif_uye_409_customer_not_synced()
    {
        var kit = await Kit.CreateAsync(_configured);
        var g = Kit.NewGroup();
        var a = await kit.MemberAsync(g);
        await kit.MemberAsync(g, 40m);
        await kit.EnablePoolAsync();

        var resp = await kit.PreviewAsync(a, [a]);

        await ShouldBeProblemAsync(resp, HttpStatusCode.Conflict, CustomerNotSynced);
    }

    /// <summary>
    /// Silinmiş üyeler W'den değil S'den alınır (K3): kişinin silinmiş üyesi P (40) W'de olmasa da havuzdadır;
    /// W'deki başka kişinin silinmiş üyesi Z yok sayılır (ne ret ne para).
    /// </summary>
    [Fact]
    public async Task Silinmis_uye_S_den_alinir_W_deki_silinmis_yok_sayilir()
    {
        var kit = await Kit.CreateAsync(_configured);
        var g = Kit.NewGroup();
        var a = await kit.MemberAsync(g);
        await kit.MemberAsync(g, 40m, purged: true);
        var z = await kit.MemberAsync(Kit.NewGroup(), 25m, purged: true);
        await kit.EnablePoolAsync();

        var preview = await OkPreviewAsync(await kit.PreviewAsync(a, [a, z]));

        preview.Should().BeEquivalentTo(new { WpfCustomerId = a, Balance = 40m, PersonPool = true });
    }

    // ── Havuz kipi: sürüm kapısı ve memberIds ────────────────────────────────

    /// <summary>
    /// R16: havuz kipinde <c>PersonBalance:MinClientVersion</c>'dan (0.9.11) eski, ön sürüm ekli, ayrıştırılamayan ya
    /// da hiç olmayan sürüm → 409 <c>client-update-required</c>; günlüğe lisans ve ham sürüm (yoksa "yok"),
    /// Information düzeyinde. Satış senkronu mod Off: bu 409 havuz kapısının.
    /// </summary>
    [Theory]
    [InlineData(Kit.OldUserAgent, "0.9.10")]
    [InlineData("OrderDeck-WPF/0.9.11-rc1", "0.9.11-rc1")]
    [InlineData("OrderDeck-WPF/bozuk", "bozuk")]
    [InlineData(null, "yok")]
    public async Task Eski_surum_havuz_kipinde_409_client_update_required(string? userAgent, string loggedVersion)
    {
        var kit = await Kit.CreateAsync(_configured);
        var a = await kit.MemberAsync(null, 30m);
        await kit.EnablePoolAsync();

        var resp = await kit.PreviewAsync(a, [a], userAgent);

        await ShouldBeProblemAsync(resp, HttpStatusCode.Conflict, ClientUpdateRequired);
        _configured.Log.Entries.Should().Contain(e => e.Category == ControllerCategory
            && e.Level == LogLevel.Information
            && e.Message.Contains(kit.LicenseId.ToString(), StringComparison.Ordinal)
            && e.Message.Contains($"sürümü {loggedVersion} alt", StringComparison.Ordinal));
    }

    /// <summary>
    /// Karşılaştırma <c>System.Version</c> ile (§7.2): "0.9.100" metin sırasında "0.9.11"den küçük görünür ama
    /// geçer; "+" sonrası derleme bilgisi sıralamaya girmez.
    /// </summary>
    [Theory]
    [InlineData("OrderDeck-WPF/0.9.100")]
    [InlineData("OrderDeck-WPF/0.9.11+abc")]
    [InlineData("OrderDeck-WPF/1.0.0")]
    public async Task Guncel_surum_havuz_kipinde_200_surum_sayisal_karsilastirilir(string userAgent)
    {
        var kit = await Kit.CreateAsync(_configured);
        var a = await kit.MemberAsync(null, 30m);
        await kit.EnablePoolAsync();

        var preview = await OkPreviewAsync(await kit.PreviewAsync(a, [a], userAgent));

        preview.PersonPool.Should().BeTrue();
    }

    /// <summary>
    /// Plan fark 3: sürüm denetimi <c>memberIds</c>'ten önce. Üye listesi göndermeyen eski sürüm (sahadaki v0.9.9 gibi)
    /// 400 değil 409 <c>client-update-required</c> alır; §6.2 ve §8.5'in saha denemesi bunu bekler.
    /// </summary>
    [Fact]
    public async Task Eski_surum_memberIds_gondermese_de_409_client_update_required()
    {
        var kit = await Kit.CreateAsync(_configured);
        var a = await kit.MemberAsync(null, 30m);
        await kit.EnablePoolAsync();

        var resp = await kit.PreviewAsync(a, null, Kit.OldUserAgent);

        await ShouldBeProblemAsync(resp, HttpStatusCode.Conflict, ClientUpdateRequired);
    }

    /// <summary>Havuz kipinde güncel sürüm üye listesi göndermezse 400 <c>member-ids-required</c> (R17).</summary>
    [Fact]
    public async Task memberIds_yok_400_member_ids_required()
    {
        var kit = await Kit.CreateAsync(_configured);
        var a = await kit.MemberAsync(null, 30m);
        await kit.EnablePoolAsync();

        var resp = await kit.PreviewAsync(a, null);

        await ShouldBeProblemAsync(resp, HttpStatusCode.BadRequest, "member-ids-required");
    }

    /// <summary>
    /// Fark 18j (§7.2 kapalı-hata): alt sınır tanımsız sunucuda havuz kipindeki lisansın güncel istemcisi de 409
    /// <c>client-update-required</c> alır ve günlüğe lisanslı bir Error yazılır. Aynı sunucuda satır kipi
    /// etkilenmez.
    /// </summary>
    [Fact]
    public async Task Alt_sinir_tanimsiz_havuz_kipinde_409_ve_hata_gunlugu()
    {
        var kit = await Kit.CreateAsync(_unconfigured);
        var a = await kit.MemberAsync(null, 30m);
        await kit.EnablePoolAsync();

        var resp = await kit.PreviewAsync(a, [a]);

        await ShouldBeProblemAsync(resp, HttpStatusCode.Conflict, ClientUpdateRequired);
        _unconfigured.Log.Entries.Should().Contain(e => e.Category == ControllerCategory
            && e.Level == LogLevel.Error
            && e.Message.Contains(kit.LicenseId.ToString(), StringComparison.Ordinal));

        await kit.DisablePoolAsync();
        (await OkPreviewAsync(await kit.PreviewAsync(a, null))).Balance.Should().Be(30m);
    }

    /// <summary>
    /// §5.5 / fark 18i: 409 <c>customer-not-synced</c> günlüğe lisans ve satır Id'siyle ("N") yazılır, Information
    /// düzeyinde; üyenin kullanıcı adı hiçbir günlük satırında geçmez.
    /// </summary>
    [Fact]
    public async Task customer_not_synced_gunlugu_lisans_ve_satir_Idsi_icerir()
    {
        var kit = await Kit.CreateAsync(_configured);
        var g = Kit.NewGroup();
        var name = $"u{Guid.NewGuid():N}";
        var a = await kit.MemberAsync(g);
        var b = await kit.MemberAsync(g, 40m, username: name);
        await kit.EnablePoolAsync();

        var resp = await kit.PreviewAsync(a, [a]);

        await ShouldBeProblemAsync(resp, HttpStatusCode.Conflict, CustomerNotSynced);
        var entry = _configured.Log.Entries.Should().ContainSingle(e => e.Category == ControllerCategory
            && e.Message.Contains(CustomerNotSynced, StringComparison.Ordinal)
            && e.Message.Contains(kit.LicenseId.ToString(), StringComparison.Ordinal)).Subject;
        entry.Level.Should().Be(LogLevel.Information);
        entry.Message.Should().Contain(b.ToString("N"));
        _configured.Log.Messages.Should().NotContain(m => m.Contains(name, StringComparison.Ordinal));
    }

    // ── Havuz kipi: UpdatedAt (M9) ve kip değişimi ───────────────────────────

    /// <summary>
    /// Fark 18g: <c>UpdatedAt</c> P'nin son para hareketidir. A'nın bakiye satırının <c>UpdatedAt</c>'i daha yeni
    /// (kurulum anı) ama hareket değil; S∖P'deki eksi C'nin daha yeni hareketi Σ'ya girer (10 − 5 = 5) ama
    /// <c>UpdatedAt</c>'e girmez.
    /// </summary>
    [Fact]
    public async Task UpdatedAt_P_nin_son_para_hareketi()
    {
        var kit = await Kit.CreateAsync(_configured);
        var g = Kit.NewGroup();
        var a = await kit.MemberAsync(g);
        var b = await kit.MemberAsync(g);
        await kit.MemberAsync(g, -5m);
        var at = new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
        await kit.FundAsync(a, 10m, at: at);
        await kit.EnablePoolAsync();

        var preview = await OkPreviewAsync(await kit.PreviewAsync(a, [a, b]));

        preview.Balance.Should().Be(5m);
        preview.UpdatedAt.Should().Be(at);
    }

    /// <summary>Fark 18g: P'de hiç para hareketi yoksa P'nin bakiye satırlarının en yeni <c>UpdatedAt</c>'i.</summary>
    [Fact]
    public async Task UpdatedAt_hareket_yoksa_bakiye_satirlarinin_en_yenisi()
    {
        var kit = await Kit.CreateAsync(_configured);
        var g = Kit.NewGroup();
        var a = await kit.MemberAsync(g);
        var b = await kit.MemberAsync(g);
        await kit.EnablePoolAsync();
        var newest = new[] { (await kit.BalanceRowAsync(a))!.UpdatedAt, (await kit.BalanceRowAsync(b))!.UpdatedAt }.Max();

        var preview = await OkPreviewAsync(await kit.PreviewAsync(a, [a, b]));

        preview.Balance.Should().Be(0m);
        preview.UpdatedAt.Should().Be(newest);
    }

    /// <summary>Kill switch (§7.2 <c>disable</c>): bayrak boşalınca aynı istek satır kipinin bakiyesini döner.</summary>
    [Fact]
    public async Task Bayrak_kapatilinca_satir_kipine_doner()
    {
        var kit = await Kit.CreateAsync(_configured);
        var g = Kit.NewGroup();
        var a = await kit.MemberAsync(g, 30m);
        var b = await kit.MemberAsync(g, 40m);
        await kit.EnablePoolAsync();

        var pooled = await OkPreviewAsync(await kit.PreviewAsync(a, [a, b]));
        await kit.DisablePoolAsync();
        var row = await OkPreviewAsync(await kit.PreviewAsync(a, [a, b]));

        pooled.Should().BeEquivalentTo(new { Balance = 70m, PersonPool = true });
        row.Should().BeEquivalentTo(new { Balance = 30m, PersonPool = false });
    }

    // ── Yardımcılar ───────────────────────────────────────────────────────────

    private static async Task<LicensesCustomerBalanceApplyController.PreviewResponse> OkPreviewAsync(
        HttpResponseMessage resp)
    {
        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        return (await resp.Content.ReadFromJsonAsync<LicensesCustomerBalanceApplyController.PreviewResponse>())!;
    }

    private static async Task ShouldBeProblemAsync(HttpResponseMessage resp, HttpStatusCode status, string title)
    {
        resp.StatusCode.Should().Be(status, await resp.Content.ReadAsStringAsync());
        (await Kit.TitleAsync(resp)).Should().Be(title);
    }
}
