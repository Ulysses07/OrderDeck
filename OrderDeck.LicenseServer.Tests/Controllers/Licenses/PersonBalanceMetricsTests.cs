using System.Diagnostics.Metrics;
using System.Net;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.CustomerSync;
using OrderDeck.LicenseServer.Services.Observability;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;
using Kit = OrderDeck.LicenseServer.Tests.Controllers.Licenses.PersonBalanceTestKit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Licenses;

/// <summary>
/// Ortak bakiye metrikleri — spec 2026-10-11 §8.6, §8.5 ("metrikte <c>client-update-required</c> görünür"), plan S2
/// fark 18h: <c>customer-not-synced</c>, <c>client-update-required</c> (<c>gate</c> + <c>endpoint</c>),
/// <c>balance-busy</c>, çok bacaklı çekim (<c>legs</c>). Sayaçlar BCL <see cref="MeterListener"/>'la okunur; dinleyici
/// yalnız bu fabrikanın <see cref="OrderDeckMetrics"/> örneğinin aletlerini açar (paralel sınıfların fabrikaları aynı
/// <see cref="OrderDeckMetrics.MeterName"/>'i paylaşır). <c>balance-busy</c> InMemory'de kurulamaz (üç ardışık jeton
/// çakışması): <c>endpoint=apply</c> Görev 14'ün, <c>endpoint=panel/manual-adjustment</c> Görev 16'nın ilişkisel
/// 503 testinde doğrulanır; <c>panel/reverse</c> ve WPF <c>reverse</c> etiketlerinin otomatik testi yoktur.
/// </summary>
public sealed class PersonBalanceMetricsTests : IClassFixture<PersonBalanceConfiguredApiFactory>
{
    private readonly PersonBalanceConfiguredApiFactory _factory;
    private readonly OrderDeckMetrics _metrics;

    public PersonBalanceMetricsTests(PersonBalanceConfiguredApiFactory factory)
    {
        _factory = factory;
        _metrics = factory.Services.GetRequiredService<OrderDeckMetrics>();
    }

    /// <param name="Tags">"ad=değer", ordinal sıralı.</param>
    private sealed record Olcum(long Value, IReadOnlyList<string> Tags);

    /// <summary>Tek bir sayacın ölçümleri. Ölçüm isteğin içinde, yanıttan önce yazılır.</summary>
    private sealed class Recorder : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly List<Olcum> _items = [];

        public Recorder(Instrument watched)
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (ReferenceEquals(instrument, watched)) listener.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
            {
                var formatted = new List<string>();
                foreach (var tag in tags) formatted.Add($"{tag.Key}={tag.Value}");
                formatted.Sort(StringComparer.Ordinal);
                lock (_items) _items.Add(new Olcum(value, formatted));
            });
            _listener.Start();
        }

        public IReadOnlyList<Olcum> Items
        {
            get { lock (_items) return _items.ToList(); }
        }

        public void Dispose() => _listener.Dispose();
    }

    [Fact]
    public async Task customer_not_synced_preview_te_sayilir()
    {
        var kit = await Kit.CreateAsync(_factory);
        var g = Kit.NewGroup();
        var a = await kit.MemberAsync(g, 10m);
        await kit.MemberAsync(g, 20m);   // S∖W'de artı üye: kural 3b
        await kit.EnablePoolAsync();
        using var recorder = new Recorder(_metrics.BalanceCustomerNotSynced);

        var response = await kit.PreviewAsync(a, [a]);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Kit.TitleAsync(response)).Should().Be(PersonBalance.CodeCustomerNotSynced);
        var olcum = recorder.Items.Should().ContainSingle().Subject;
        olcum.Value.Should().Be(1);
        olcum.Tags.Should().Equal("endpoint=preview");
    }

    [Fact]
    public async Task client_update_required_havuz_kapisi()
    {
        var kit = await Kit.CreateAsync(_factory);
        var a = await kit.MemberAsync(null, 10m);
        await kit.EnablePoolAsync();
        using var recorder = new Recorder(_metrics.BalanceClientUpdateRequired);

        var response = await kit.PreviewAsync(a, [a], Kit.OldUserAgent);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Kit.TitleAsync(response)).Should().Be("client-update-required");
        recorder.Items.Should().ContainSingle("mod Off: satış kapısı koşmaz")
            .Which.Tags.Should().Equal("endpoint=preview", "gate=person-balance");
    }

    [Fact]
    public async Task client_update_required_satis_kapisi()
    {
        var kit = await Kit.CreateAsync(_factory, SalesSyncMode.On);
        var a = await kit.MemberAsync(null, 10m);
        using var recorder = new Recorder(_metrics.BalanceClientUpdateRequired);

        var preview = await kit.PreviewAsync(a, null, Kit.OldUserAgent);
        // v0.9.10 anahtarsız işte önce scope'u sorar (R6-01); onun 409'u da sayılır.
        var scope = await kit.SendAsync(HttpMethod.Get,
            $"/api/v1/licenses/{kit.LicenseId}/customer-balance/scope?wpfCustomerId={a}&saleScope={Uri.EscapeDataString(Kit.Session42)}",
            userAgent: Kit.OldUserAgent);

        foreach (var response in new[] { preview, scope })
        {
            response.StatusCode.Should().Be(HttpStatusCode.Conflict);
            (await Kit.TitleAsync(response)).Should().Be("client-update-required");
        }
        recorder.Items.Select(m => string.Join(",", m.Tags)).Should().Equal(
            "endpoint=preview,gate=sales-sync", "endpoint=scope,gate=sales-sync");
    }

    /// <summary>
    /// Plan farkı 3–4: havuz kipi + mod On'da satış kapısı havuz kapısından önce keser. Fabrikada iki alt sınır da
    /// 0.9.11 ve yanıt iki kapıda aynı; 409'un hangi kapıdan geldiğini yalnız <c>gate</c> etiketi ayırt eder
    /// (§8.5'in v0.9.9 denemesi, mod On'da <c>gate="sales-sync"</c> bekler).
    /// </summary>
    [Fact]
    public async Task client_update_required_havuz_kipinde_mod_On_once_satis_kapisi()
    {
        var kit = await Kit.CreateAsync(_factory, SalesSyncMode.On);
        var a = await kit.MemberAsync(null, 10m);
        await kit.EnablePoolAsync();
        using var recorder = new Recorder(_metrics.BalanceClientUpdateRequired);

        var preview = await kit.PreviewAsync(a, [a], Kit.OldUserAgent);
        var apply = await kit.ApplyAsync(a, 5m, 5m, Guid.NewGuid(), null, [a], Kit.OldUserAgent);

        foreach (var response in new[] { preview, apply })
        {
            response.StatusCode.Should().Be(HttpStatusCode.Conflict);
            (await Kit.TitleAsync(response)).Should().Be("client-update-required");
        }
        recorder.Items.Select(m => string.Join(",", m.Tags)).Should().Equal(
            "endpoint=preview,gate=sales-sync", "endpoint=apply,gate=sales-sync");
    }

    [Fact]
    public async Task cok_bacakli_cekim_bacak_sayisiyla()
    {
        // Spec Örnek 1: T (sahip, 0), I 100, Y 30; 120 → K (T, 0), (I, −100), (Y, −20): çapa dahil üç satır.
        var kit = await Kit.CreateAsync(_factory);
        var g = Kit.NewGroup();
        var t = await kit.MemberAsync(g);
        var i = await kit.MemberAsync(g, 100m);
        var y = await kit.MemberAsync(g, 30m);
        await kit.EnablePoolAsync();
        using var recorder = new Recorder(_metrics.BalanceMultiLegDraws);

        var response = await kit.ApplyAsync(t, 120m, 120m, Guid.NewGuid(), Kit.Session42, [t, i, y]);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var olcum = recorder.Items.Should().ContainSingle().Subject;
        (olcum.Value, string.Join(",", olcum.Tags)).Should().Be((1L, "legs=3"));
    }

    [Fact]
    public async Task tek_bacakli_cekim_sayilmaz()
    {
        var kit = await Kit.CreateAsync(_factory);
        var a = await kit.MemberAsync(null, 100m);
        await kit.EnablePoolAsync();
        using var recorder = new Recorder(_metrics.BalanceMultiLegDraws);

        var response = await kit.ApplyAsync(a, 30m, 30m, Guid.NewGuid(), null, [a]);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        recorder.Items.Should().BeEmpty("yalnız çapaya yazılan çekim çok bacaklı değildir");
    }

    [Fact]
    public async Task panel_elle_eksi_cok_bacakli_cekim_bacak_sayisiyla()
    {
        // Spec §4.8 (K5) örneği, Görev 9'un K5_ornegi_A10_B40_rota_A_eksi30'u: çapa (A, −10), bacak (B, −20).
        var kit = await Kit.CreateAsync(_factory);
        var g = Kit.NewGroup();
        var a = await kit.MemberAsync(g, 10m);
        await kit.MemberAsync(g, 40m);
        await kit.EnablePoolAsync();
        using var recorder = new Recorder(_metrics.BalanceMultiLegDraws);

        var response = await kit.PanelManualAdjustmentAsync(a, -30m, $"r-{Guid.NewGuid():N}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var olcum = recorder.Items.Should().ContainSingle().Subject;
        (olcum.Value, string.Join(",", olcum.Tags)).Should().Be((1L, "legs=2"));
    }

    [Fact]
    public async Task panel_iade_iptali_cok_bacakli_cekim_bacak_sayisiyla()
    {
        // Spec Örnek 4, Görev 10'un Ornek4_baska_uyenin_iadesini_iptal'i: A'ya iade R (+80), eski düşüm A'dan 50
        // → A = 30; B = 60. B'den R'nin iptali: çapa (A, −30), bacak (B, −50).
        var kit = await Kit.CreateAsync(_factory);
        var g = Kit.NewGroup();
        var a = await kit.MemberAsync(g);
        var refund = await kit.FundAsync(a, 80m);
        await kit.SeedLegacyDeductionAsync(a, 50m, 50m);
        var b = await kit.MemberAsync(g, 60m);
        await kit.EnablePoolAsync();
        using var recorder = new Recorder(_metrics.BalanceMultiLegDraws);

        var response = await kit.PanelReverseAsync(b, refund);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var olcum = recorder.Items.Should().ContainSingle().Subject;
        (olcum.Value, string.Join(",", olcum.Tags)).Should().Be((1L, "legs=2"));
    }
}
