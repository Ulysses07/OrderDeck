using FluentAssertions;
using OrderDeck.LicenseServer.Tools;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Tools;

/// <summary>
/// <c>sales-sync</c> CLI'nin argüman ayrıştırması — veritabanı istemez (spec 2026-10-13 §4.8, §4.10). Alt komutlar:
/// <c>status</c>, <c>shipping set</c>, <c>force-end</c>, <c>adopt-orders</c>, <c>enable</c> ve <c>disable</c>. Kullanım hataları (tanınmayan alt
/// komut dahil) 2 döner ve yapılandırmaya/veritabanına HİÇ dokunmadan döner. Gövdeler gerçek SQL Server'da
/// <see cref="SalesSyncRelationalTests"/>'te.
/// </summary>
public sealed class SalesSyncTests
{
    public static TheoryData<string[]> UsageErrors()
    {
        var license = Guid.NewGuid().ToString();
        var replica = Guid.NewGuid().ToString();
        var order = Guid.NewGuid().ToString();
        var orders = $"{Guid.NewGuid()},{Guid.NewGuid()}";
        var shipment = Guid.NewGuid().ToString();
        var overLimit = string.Join(",", Enumerable.Range(0, SalesSync.MaxAdoptOrders + 1).Select(_ => Guid.NewGuid()));
        return new TheoryData<string[]>
        {
            // Alt komut yok ya da bilinmeyen alt komut.
            new[] { "sales-sync" },
            new[] { "sales-sync", "statu", "--license", license },
            new[] { "sales-sync", "enabled", "--license", license },
            new[] { "sales-sync", "disabled", "--license", license, "--apply" },
            new[] { "sales-sync", "shipping" },
            new[] { "sales-sync", "shipping", "get", "--license", license },
            new[] { "sales-sync", "shipping", "--license", license, "--off" },
            new[] { "sales-sync", "set", "shipping", "--license", license, "--off" },

            // ── shipping set ──
            // Lisans eksik, bozuk ya da iki kez.
            new[] { "sales-sync", "shipping", "set", "--threshold", "500", "--fee", "60" },
            new[] { "sales-sync", "shipping", "set", "--license", "--threshold", "500", "--fee", "60" },
            new[] { "sales-sync", "shipping", "set", "--license", "lisans-degil", "--off" },
            new[] { "sales-sync", "shipping", "set", "--license", license, "--license", license, "--off" },
            // Değer yok: ne eşik/ücret ne --off.
            new[] { "sales-sync", "shipping", "set", "--license", license },
            new[] { "sales-sync", "shipping", "set", "--license", license, "--apply" },
            // Yalnız biri.
            new[] { "sales-sync", "shipping", "set", "--license", license, "--threshold", "500" },
            new[] { "sales-sync", "shipping", "set", "--license", license, "--fee", "60" },
            // --off ile değer birlikte.
            new[] { "sales-sync", "shipping", "set", "--license", license, "--off", "--threshold", "500", "--fee", "60" },
            new[] { "sales-sync", "shipping", "set", "--license", license, "--off", "--fee", "60" },
            // Geçersiz tutar: sıfır, negatif, kuruştan ince, Türkçe virgül, binlik ayraç, üs, boşluk, sayı değil.
            new[] { "sales-sync", "shipping", "set", "--license", license, "--threshold", "0", "--fee", "60" },
            new[] { "sales-sync", "shipping", "set", "--license", license, "--threshold", "500", "--fee", "0" },
            new[] { "sales-sync", "shipping", "set", "--license", license, "--threshold", "-500", "--fee", "60" },
            new[] { "sales-sync", "shipping", "set", "--license", license, "--threshold", "500.005", "--fee", "60" },
            new[] { "sales-sync", "shipping", "set", "--license", license, "--threshold", "500,5", "--fee", "60" },
            new[] { "sales-sync", "shipping", "set", "--license", license, "--threshold", "1,500", "--fee", "60" },
            new[] { "sales-sync", "shipping", "set", "--license", license, "--threshold", "5e2", "--fee", "60" },
            new[] { "sales-sync", "shipping", "set", "--license", license, "--threshold", " 500", "--fee", "60" },
            new[] { "sales-sync", "shipping", "set", "--license", license, "--threshold", "beşyüz", "--fee", "60" },
            new[] { "sales-sync", "shipping", "set", "--license", license, "--threshold", "99999999999999999", "--fee", "60" },
            // Değeri eksik ya da değer yerine seçenek: "--threshold --apply" kuru koşu ya da tutar sanılmasın.
            new[] { "sales-sync", "shipping", "set", "--license", license, "--threshold", "--fee", "60" },
            new[] { "sales-sync", "shipping", "set", "--license", license, "--threshold", "500", "--fee" },
            new[] { "sales-sync", "shipping", "set", "--license", license, "--threshold", "500", "--fee", "--apply" },
            // İki kez verilen seçenek.
            new[] { "sales-sync", "shipping", "set", "--license", license, "--threshold", "500", "--threshold", "600", "--fee", "60" },
            new[] { "sales-sync", "shipping", "set", "--license", license, "--off", "--off" },
            new[] { "sales-sync", "shipping", "set", "--license", license, "--off", "--apply", "--apply" },
            // Tanınmayan argüman (--apply yazım hatası) kuru çalıştırma sanılmasın.
            new[] { "sales-sync", "shipping", "set", "--license", license, "--off", "--aply" },
            new[] { "sales-sync", "shipping", "set", "--license", license, "--off", "evet" },

            // ── status ──
            new[] { "sales-sync", "status" },
            new[] { "sales-sync", "status", "--license" },
            new[] { "sales-sync", "status", "--license", "--apply" },
            new[] { "sales-sync", "status", "--license", "lisans-degil" },
            new[] { "sales-sync", "status", "--license", license, "--license", license },
            // Salt okunur: --apply bir şey yazacağı sanılmasın.
            new[] { "sales-sync", "status", "--license", license, "--apply" },
            new[] { "sales-sync", "status", "--license", license, "--session", license },
            new[] { "sales-sync", "status", "--license", license, "evet" },

            // ── force-end ──
            new[] { "sales-sync", "force-end" },
            new[] { "sales-sync", "force-end", "--apply" },
            new[] { "sales-sync", "force-end", "--session" },
            new[] { "sales-sync", "force-end", "--session", "--apply" },
            new[] { "sales-sync", "force-end", "--session", "yayin-degil" },
            new[] { "sales-sync", "force-end", "--session", order, "--session", order },
            new[] { "sales-sync", "force-end", "--session", order, "--apply", "--apply" },
            // Yayın Id'si lisanslar arasında tekildir: --license tanınmaz, karışıklık yaratmasın.
            new[] { "sales-sync", "force-end", "--license", license },
            new[] { "sales-sync", "force-end", "--session", order, "--license", license },
            new[] { "sales-sync", "force-end", "--session", order, "--aply" },
            new[] { "sales-sync", "force-end", "--session", order, "evet" },

            // ── adopt-orders ──
            // Her zorunlu seçenek eksik.
            new[] { "sales-sync", "adopt-orders", "--orders", orders, "--replica", replica },
            new[] { "sales-sync", "adopt-orders", "--license", license, "--replica", replica },
            new[] { "sales-sync", "adopt-orders", "--license", license, "--orders", orders },
            new[] { "sales-sync", "adopt-orders", "--license", license, "--orders", orders, "--replica", replica, "--orders" },
            // İki kez.
            new[] { "sales-sync", "adopt-orders", "--license", license, "--license", license, "--orders", orders, "--replica", replica },
            new[] { "sales-sync", "adopt-orders", "--license", license, "--orders", orders, "--orders", order, "--replica", replica },
            new[] { "sales-sync", "adopt-orders", "--license", license, "--orders", orders, "--replica", replica, "--replica", replica },
            new[] { "sales-sync", "adopt-orders", "--license", license, "--orders", orders, "--replica", replica, "--apply", "--apply" },
            // GUID olmayan lisans ya da kopya.
            new[] { "sales-sync", "adopt-orders", "--license", "lisans-degil", "--orders", orders, "--replica", replica },
            new[] { "sales-sync", "adopt-orders", "--license", license, "--orders", orders, "--replica", "PC-A" },
            // Boş, geçersiz ya da tavanı aşan sipariş listesi.
            new[] { "sales-sync", "adopt-orders", "--license", license, "--orders", "", "--replica", replica },
            new[] { "sales-sync", "adopt-orders", "--license", license, "--orders", ",", "--replica", replica },
            new[] { "sales-sync", "adopt-orders", "--license", license, "--orders", $"{order},", "--replica", replica },
            new[] { "sales-sync", "adopt-orders", "--license", license, "--orders", $",{order}", "--replica", replica },
            new[] { "sales-sync", "adopt-orders", "--license", license, "--orders", $"{order},,{replica}", "--replica", replica },
            new[] { "sales-sync", "adopt-orders", "--license", license, "--orders", $"{order},siparis-degil", "--replica", replica },
            new[] { "sales-sync", "adopt-orders", "--license", license, "--orders", $"{order} {replica}", "--replica", replica },
            new[] { "sales-sync", "adopt-orders", "--license", license, "--orders", overLimit, "--replica", replica },
            new[] { "sales-sync", "adopt-orders", "--license", license, "--orders", "--apply", "--replica", replica },
            // Tanınmayan argüman.
            new[] { "sales-sync", "adopt-orders", "--license", license, "--orders", orders, "--replica", replica, "--aply" },
            new[] { "sales-sync", "adopt-orders", "--license", license, "--orders", orders, "--replica", replica, "--session", order },

            // ── enable ──
            // Lisans eksik, bozuk, değersiz ya da iki kez.
            new[] { "sales-sync", "enable" },
            new[] { "sales-sync", "enable", "--apply" },
            new[] { "sales-sync", "enable", "--accept-shipment", shipment },
            new[] { "sales-sync", "enable", "--license" },
            new[] { "sales-sync", "enable", "--license", "--apply" },
            new[] { "sales-sync", "enable", "--license", "lisans-degil" },
            new[] { "sales-sync", "enable", "--license", license, "--license", license },
            new[] { "sales-sync", "enable", "--license", "--accept-shipment", shipment },
            // --accept-shipment değersiz, değer yerine seçenek, GUID değil ya da liste.
            new[] { "sales-sync", "enable", "--license", license, "--accept-shipment" },
            new[] { "sales-sync", "enable", "--license", license, "--accept-shipment", "--apply" },
            new[] { "sales-sync", "enable", "--license", license, "--accept-shipment", "dosya-degil" },
            new[] { "sales-sync", "enable", "--license", license, "--accept-shipment", "" },
            new[] { "sales-sync", "enable", "--license", license, "--accept-shipment", $"{shipment},{order}" },
            new[] { "sales-sync", "enable", "--license", license, "--accept-shipment", shipment, "--accept-shipment", "dosya-degil" },
            // İki kez --apply, tanınmayan argüman.
            new[] { "sales-sync", "enable", "--license", license, "--apply", "--apply" },
            new[] { "sales-sync", "enable", "--license", license, "--aply" },
            new[] { "sales-sync", "enable", "--license", license, "--accept", shipment },
            new[] { "sales-sync", "enable", "--license", license, "--session", order },
            new[] { "sales-sync", "enable", "--license", license, "evet" },

            // ── disable ──
            // Lisans eksik, bozuk, değersiz ya da iki kez.
            new[] { "sales-sync", "disable" },
            new[] { "sales-sync", "disable", "--apply" },
            new[] { "sales-sync", "disable", "--license" },
            new[] { "sales-sync", "disable", "--license", "--apply" },
            new[] { "sales-sync", "disable", "--license", "lisans-degil" },
            new[] { "sales-sync", "disable", "--license", license, "--license", license },
            new[] { "sales-sync", "disable", "--license", license, "--license", license, "--apply" },
            // İki kez --apply, tanınmayan argüman (--aply yazım hatası kuru koşu sanılmasın).
            new[] { "sales-sync", "disable", "--license", license, "--apply", "--apply" },
            new[] { "sales-sync", "disable", "--license", license, "--aply" },
            new[] { "sales-sync", "disable", "--license", license, "evet" },
            // enable'ın seçenekleri ve yayın Id'si disable'da tanınmaz.
            new[] { "sales-sync", "disable", "--license", license, "--accept-shipment", shipment },
            new[] { "sales-sync", "disable", "--license", license, "--session", order },
            new[] { "sales-sync", "disable", "--session", order, "--apply" },
        };
    }

    [Theory]
    [MemberData(nameof(UsageErrors))]
    public async Task Kullanim_hatasi_2_doner(string[] args)
        => (await SalesSync.RunAsync(args)).Should().Be(2);

    [Theory]
    [MemberData(nameof(UsageErrors))]
    public void Ayristirici_kullanim_hatalarini_reddeder(string[] args)
        => SalesSync.TryParseArgs(args, out _).Should().BeFalse();

    [Fact]
    public void Esik_ve_ucret_kabul_edilir_kuru_kosu_varsayilan()
    {
        var license = Guid.NewGuid();

        SalesSync.TryParseArgs(
                ["sales-sync", "shipping", "set", "--license", license.ToString(), "--threshold", "500", "--fee", "59.90"],
                out var parsed)
            .Should().BeTrue();

        parsed.Should().Be(new SalesSync.ShippingSetArgs(license, 500m, 59.90m, Apply: false));
    }

    [Fact]
    public void Secenek_sirasi_serbest_apply_ve_N_bicimi_kabul_edilir()
    {
        var license = Guid.NewGuid();

        SalesSync.TryParseArgs(
                ["sales-sync", "shipping", "set", "--apply", "--fee", "60", "--license", license.ToString("N"), "--threshold", "750.5"],
                out var parsed)
            .Should().BeTrue();

        parsed.Should().Be(new SalesSync.ShippingSetArgs(license, 750.5m, 60m, Apply: true));
    }

    [Fact]
    public void Off_iki_degeri_bos_birakir()
    {
        var license = Guid.NewGuid();

        SalesSync.TryParseArgs(["sales-sync", "shipping", "set", "--license", license.ToString(), "--off"], out var dry)
            .Should().BeTrue();
        dry.Should().Be(new SalesSync.ShippingSetArgs(license, null, null, Apply: false));

        SalesSync.TryParseArgs(["sales-sync", "shipping", "set", "--off", "--apply", "--license", license.ToString()], out var apply)
            .Should().BeTrue();
        apply.Should().Be(new SalesSync.ShippingSetArgs(license, null, null, Apply: true));
    }

    [Fact]
    public void Status_yalniz_lisans_alir()
    {
        var license = Guid.NewGuid();

        SalesSync.TryParseArgs(["sales-sync", "status", "--license", license.ToString("N")], out var parsed)
            .Should().BeTrue();

        parsed.Should().Be(new SalesSync.StatusArgs(license));
    }

    [Fact]
    public void Force_end_yayin_alir_kuru_kosu_varsayilan()
    {
        var session = Guid.NewGuid();

        SalesSync.TryParseArgs(["sales-sync", "force-end", "--session", session.ToString()], out var dry)
            .Should().BeTrue();
        dry.Should().Be(new SalesSync.ForceEndArgs(session, Apply: false));

        SalesSync.TryParseArgs(["sales-sync", "force-end", "--apply", "--session", session.ToString("N")], out var apply)
            .Should().BeTrue();
        apply.Should().Be(new SalesSync.ForceEndArgs(session, Apply: true));
    }

    /// <summary>Sipariş listesi virgülle ayrılır; tekrarlar ilk görüldükleri sırada bir kez kalır, biçim (D, N)
    /// serbest. Tavan tekrarlar ayıklandıktan sonraki sayıya uygulanır.</summary>
    [Fact]
    public void Adopt_orders_listeyi_tekrarsiz_alir_kuru_kosu_varsayilan()
    {
        var license = Guid.NewGuid();
        var replica = Guid.NewGuid();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        SalesSync.TryParseArgs(
                ["sales-sync", "adopt-orders", "--replica", replica.ToString(), "--orders", $"{a},{b:N},{a:N},{b}",
                 "--license", license.ToString()],
                out var dry)
            .Should().BeTrue();
        dry.Should().Be(new SalesSync.AdoptOrdersArgs(license, [a, b], replica, Apply: false));

        SalesSync.TryParseArgs(
                ["sales-sync", "adopt-orders", "--license", license.ToString(), "--orders", b.ToString(), "--apply",
                 "--replica", replica.ToString("N")],
                out var apply)
            .Should().BeTrue();
        apply.Should().Be(new SalesSync.AdoptOrdersArgs(license, [b], replica, Apply: true));
    }

    [Fact]
    public void Adopt_orders_tavana_kadar_kabul_eder_tekrarlar_sayilmaz()
    {
        var ids = Enumerable.Range(0, SalesSync.MaxAdoptOrders).Select(_ => Guid.NewGuid()).ToList();
        var withRepeat = string.Join(",", ids.Append(ids[0]));

        SalesSync.TryParseArgs(
                ["sales-sync", "adopt-orders", "--license", Guid.NewGuid().ToString(), "--orders", withRepeat,
                 "--replica", Guid.NewGuid().ToString()],
                out var parsed)
            .Should().BeTrue();

        parsed.Should().BeOfType<SalesSync.AdoptOrdersArgs>().Which.Orders.Should().Equal(ids);
    }

    /// <summary><c>enable</c>: kuru koşu varsayılan, kabul listesi boş olabilir; seçenek sırası serbest.</summary>
    [Fact]
    public void Enable_lisans_alir_kuru_kosu_varsayilan()
    {
        var license = Guid.NewGuid();

        SalesSync.TryParseArgs(["sales-sync", "enable", "--license", license.ToString()], out var dry)
            .Should().BeTrue();
        dry.Should().Be(new SalesSync.EnableArgs(license, [], Apply: false));

        SalesSync.TryParseArgs(["sales-sync", "enable", "--apply", "--license", license.ToString("N")], out var apply)
            .Should().BeTrue();
        apply.Should().Be(new SalesSync.EnableArgs(license, [], Apply: true));
    }

    /// <summary><c>disable</c>: yalnız <c>--license</c> ve <c>--apply</c>; kuru koşu varsayılan, seçenek sırası serbest.</summary>
    [Fact]
    public void Disable_lisans_alir_kuru_kosu_varsayilan()
    {
        var license = Guid.NewGuid();

        SalesSync.TryParseArgs(["sales-sync", "disable", "--license", license.ToString()], out var dry)
            .Should().BeTrue();
        dry.Should().Be(new SalesSync.DisableArgs(license, Apply: false));

        SalesSync.TryParseArgs(["sales-sync", "disable", "--apply", "--license", license.ToString("N")], out var apply)
            .Should().BeTrue();
        apply.Should().Be(new SalesSync.DisableArgs(license, Apply: true));
    }

    /// <summary><c>--accept-shipment</c> tekrarlanabilir (her biri tek GUID, biçim serbest); tekrarlar ilk
    /// görüldükleri sırada bir kez kalır; <c>--license</c> ve <c>--apply</c> arasına girebilir.</summary>
    [Fact]
    public void Enable_accept_shipment_tekrarlanabilir_tekrarsiz_alinir()
    {
        var license = Guid.NewGuid();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        SalesSync.TryParseArgs(
                ["sales-sync", "enable", "--accept-shipment", a.ToString(), "--license", license.ToString(),
                 "--accept-shipment", b.ToString("N"), "--apply", "--accept-shipment", a.ToString("N"),
                 "--accept-shipment", b.ToString()],
                out var parsed)
            .Should().BeTrue();

        parsed.Should().Be(new SalesSync.EnableArgs(license, [a, b], Apply: true));
    }

    /// <summary>Kabul listesi içerikle (sırasıyla) karşılaştırılır.</summary>
    [Fact]
    public void EnableArgs_esitligi_listeyi_icerikle_karsilastirir()
    {
        var license = Guid.NewGuid();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        new SalesSync.EnableArgs(license, [a, b], false)
            .Should().Be(new SalesSync.EnableArgs(license, new List<Guid> { a, b }, false));
        new SalesSync.EnableArgs(license, [a, b], false)
            .Should().NotBe(new SalesSync.EnableArgs(license, [b, a], false));
    }

    /// <summary>Sipariş listesi içerikle karşılaştırılır (kayıt eşitliği liste referansına bakmasın).</summary>
    [Fact]
    public void AdoptOrdersArgs_esitligi_listeyi_icerikle_karsilastirir()
    {
        var license = Guid.NewGuid();
        var replica = Guid.NewGuid();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        new SalesSync.AdoptOrdersArgs(license, [a, b], replica, true)
            .Should().Be(new SalesSync.AdoptOrdersArgs(license, new List<Guid> { a, b }, replica, true));
        new SalesSync.AdoptOrdersArgs(license, [a, b], replica, true)
            .Should().NotBe(new SalesSync.AdoptOrdersArgs(license, [b, a], replica, true));
    }
}
