using FluentAssertions;
using OrderDeck.LicenseServer.Services.CustomerSync;
using Xunit;
using DrawLeg = OrderDeck.LicenseServer.Services.CustomerSync.PersonBalance.DrawLeg;
using Member = OrderDeck.LicenseServer.Services.CustomerSync.PersonBalance.Member;

namespace OrderDeck.LicenseServer.Tests.Services.CustomerSync;

/// <summary>
/// <see cref="PersonBalance.PlanDraw"/> — spec 2026-10-11 §4.2 adım 4, M7 ve §8 "PlanDraw tablo testleri" (saf,
/// fikstürsüz): çapa önce, silinmiş önce, büyük bakiye önce, Id ("N", ordinal) eşitliği, eksi satır atlanır, tutar
/// kırpma. Her iddia listenin TAMAMINI sırasıyla karşılaştırır; girdi bilerek karışık sırada verilir.
/// </summary>
public sealed class PersonBalancePlanDrawTests
{
    private static Member M(Guid id, decimal balance, bool purged = false) => new(id, balance, purged);

    private static DrawLeg L(Guid id, decimal amount) => new(id, amount);

    /// <summary>"N" metni bilinen sırada olan Id: <c>G('1')</c>'in metni <c>G('2')</c>'ninkinden ordinal küçük.</summary>
    private static Guid G(char c) => Guid.Parse(new string(c, 32));

    private static IReadOnlyList<DrawLeg> Plan(Guid anchor, decimal amount, params Member[] draw) =>
        PersonBalance.PlanDraw(anchor, draw, amount);

    [Fact]
    public void Ornek1_capa_sifir_I_once_sonra_Y()
    {
        // Spec Örnek 1 (§4.2): T = 0 (çapa), I = 100, Y = 30; applied 120 → K(T, 0), L1(I, −100), L2(Y, −20).
        var t = Guid.NewGuid();
        var i = Guid.NewGuid();
        var y = Guid.NewGuid();

        var legs = Plan(t, 120m, M(y, 30m), M(t, 0m), M(i, 100m));

        legs.Should().Equal(L(t, 0m), L(i, 100m), L(y, 20m));
        legs.Sum(l => l.Amount).Should().Be(120m, "bacakların toplamı uygulanan tutardır");
    }

    [Fact]
    public void Capa_once_cekilir()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        Plan(a, 30m, M(b, 100m), M(a, 50m)).Should().Equal(L(a, 30m));
    }

    [Fact]
    public void Capa_yetmezse_kalan_obur_uyelerden()
    {
        // Spec §4.8 örneği: A = 10, B = 40, rota A, −30 → çapa (A, −10), bacak (B, −20).
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        Plan(a, 30m, M(b, 40m), M(a, 10m)).Should().Equal(L(a, 10m), L(b, 20m));
    }

    [Fact]
    public void Silinmis_uyeler_canlilardan_once()
    {
        // Spec §5.4 (K3): {A = 0, P silinmiş = 40}; 30'luk ödeme → çapa A 0, bacak P −30 — büyük bakiyeli canlı B'ye
        // rağmen.
        var a = Guid.NewGuid();
        var p = Guid.NewGuid();
        var b = Guid.NewGuid();

        Plan(a, 30m, M(b, 100m), M(p, 40m, purged: true), M(a, 0m)).Should().Equal(L(a, 0m), L(p, 30m));
    }

    [Fact]
    public void Silinmisler_yetmezse_canlilara_gecilir()
    {
        var a = Guid.NewGuid();
        var p = Guid.NewGuid();
        var b = Guid.NewGuid();

        Plan(a, 30m, M(b, 100m), M(a, 0m), M(p, 10m, purged: true))
            .Should().Equal(L(a, 0m), L(p, 10m), L(b, 20m));
    }

    [Fact]
    public void Silinmislerde_buyuk_bakiye_once()
    {
        // Spec §4.2 adım 4.2: silinmişler kendi aralarında da bakiye büyükten küçüğe. Id ordinal sırası (G1 < G2)
        // bakiye sırasının tersine kurulu: yalnız Id'ye göre sıralayan bir uygulama G1'i önce koyar ve düşer.
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var g1 = G('1');
        var g2 = G('2');

        Plan(a, 45m, M(b, 100m), M(g1, 10m, purged: true), M(a, 0m), M(g2, 40m, purged: true))
            .Should().Equal(L(a, 0m), L(g2, 40m), L(g1, 5m));
    }

    [Fact]
    public void Buyuk_bakiye_once()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();

        Plan(a, 60m, M(a, 0m), M(b, 20m), M(c, 50m)).Should().Equal(L(a, 0m), L(c, 50m), L(b, 10m));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Esitlikte_Id_N_ordinal(bool tersSira)
    {
        var a = Guid.NewGuid();
        var g1 = G('1');
        var g2 = G('2');
        var draw = tersSira
            ? new[] { M(g2, 30m), M(a, 0m), M(g1, 30m) }
            : new[] { M(a, 0m), M(g1, 30m), M(g2, 30m) };

        PersonBalance.PlanDraw(a, draw, 40m).Should().Equal(L(a, 0m), L(g1, 30m), L(g2, 10m));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Silinmislerde_de_esitlik_Id_N_ordinal(bool tersSira)
    {
        // Plan farkı 18b: silinmişler arasında da eşitlik kırıcı Id ("N", ordinal). Canlı B'nin büyük bakiyesi sırayı
        // değiştirmez.
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var g3 = G('3');
        var g4 = G('4');
        var draw = tersSira
            ? new[] { M(g4, 30m, purged: true), M(b, 100m), M(g3, 30m, purged: true), M(a, 0m) }
            : new[] { M(a, 0m), M(b, 100m), M(g3, 30m, purged: true), M(g4, 30m, purged: true) };

        PersonBalance.PlanDraw(a, draw, 40m).Should().Equal(L(a, 0m), L(g3, 30m), L(g4, 10m));
    }

    [Fact]
    public void Eksi_ve_sifir_uyeler_atlanir()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();
        var d = Guid.NewGuid();

        Plan(a, 40m, M(b, -60m), M(a, 0m), M(c, 0m), M(d, 50m)).Should().Equal(L(a, 0m), L(d, 40m));
    }

    [Fact]
    public void Capa_eksi_bakiyeliyse_sifir_cekilir()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        Plan(a, 20m, M(a, -10m), M(b, 50m)).Should().Equal(L(a, 0m), L(b, 20m));
    }

    [Fact]
    public void Capa_listede_yoksa_sifir_tutarli_ilk_eleman()
    {
        // Çapanın bakiye satırı olmayabilir (§4.2 adım 5); yine de planın ilk elemanıdır.
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        Plan(a, 20m, M(b, 50m)).Should().Equal(L(a, 0m), L(b, 20m));
    }

    [Fact]
    public void Tutar_kirpilir_son_bacak_kismi()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();

        Plan(a, 40m, M(c, 20m), M(a, 0m), M(b, 30m)).Should().Equal(L(a, 0m), L(b, 30m), L(c, 10m));
    }

    [Fact]
    public void Tutar_artilarin_toplamini_asarsa_artilar_kadar()
    {
        // Savunma: DrawAsync tutarı Σ'ya kırptıktan sonra çağırır; PlanDraw yine de artıların ötesine geçmez.
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        Plan(a, 100m, M(a, 0m), M(b, 30m)).Should().Equal(L(a, 0m), L(b, 30m));
    }

    [Fact]
    public void Sifir_tutar_yalniz_sifir_capa()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        Plan(a, 0m, M(a, 50m), M(b, 40m)).Should().Equal(L(a, 0m));
        // Eksi tutar 0 sayılır (savunma; çağıran eksi tutar vermez).
        Plan(a, -5m, M(a, 50m), M(b, 40m)).Should().Equal(L(a, 0m));
    }

    [Fact]
    public void Tek_bacakli_cekim_yalniz_capa()
    {
        var a = Guid.NewGuid();

        Plan(a, 100m, M(a, 100m)).Should().Equal(L(a, 100m));
    }
}
