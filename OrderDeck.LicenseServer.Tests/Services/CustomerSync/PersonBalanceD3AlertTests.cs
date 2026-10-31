using FluentAssertions;
using OrderDeck.LicenseServer.Services.CustomerSync;
using Xunit;
using V = OrderDeck.LicenseServer.Services.CustomerSync.PersonBalanceVerifier;

namespace OrderDeck.LicenseServer.Tests.Services.CustomerSync;

/// <summary>
/// D3 uyarısı — spec 2026-10-11 §8.1 son madde, plan S2 fark 16: havuz kipindeki lisansta (<c>PoolSince</c> dolu)
/// taban satırlarıyla açıklanamayan havuzu &lt; 0 kişi <c>Alerts</c>'a girer; tabandan bağımsızdır ("taban yoksa
/// yalnız sayı" istisnası eksi/D1/kopya kalemleri içindir). Açıklanabilir = kişinin bütün eksi satırları tabanda ve
/// hiçbiri tabandaki değerinin altında değil (<see cref="PersonBalanceVerifier.LicenseReport.NegativePersonsExplained"/>).
/// Satır kipinde 6. madde yalnız sayıdır.
/// </summary>
public sealed class PersonBalanceD3AlertTests
{
    private static readonly Guid Lic = Guid.NewGuid();
    private static readonly DateTimeOffset Issued = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Captured = new(2026, 10, 20, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset PoolSince = new(2026, 11, 2, 0, 0, 0, TimeSpan.Zero);

    private const string D3Line = "D3: havuzu < 0 kişi 1 (tabanla açıklanamayan, havuz kipi)";

    /// <summary><c>PersonBalanceVerifierTests.Snap</c> kalıbı; lisans satırı kip bayrağını taşır.</summary>
    private static V.Snapshot Snap(
        DateTimeOffset? poolSince, IEnumerable<V.ProjectionRow> projections, IEnumerable<V.BalanceRow> balances,
        IEnumerable<V.LedgerRow> ledger, IEnumerable<V.BaselineRow>? baselines = null) =>
        new([new V.LicenseRow(Lic, Issued, poolSince)],
            projections.ToList(),
            balances.ToList(),
            ledger.ToList(),
            new List<V.DrawRow>(),
            new HashSet<Guid>(),
            (baselines ?? Array.Empty<V.BaselineRow>()).ToList(),
            new List<V.ActivationRow>(),
            ProjectionCount: 0, TransactionCount: 0);

    private static V.LicenseReport Single(V.Snapshot s) => V.Analyze(s).Licenses.Should().ContainSingle().Subject;

    private static V.BaselineRow Marker() => new(Lic, V.LicenseMarker, 0m, 0m, Captured, null);

    /// <summary>Kişi {A = 10, B = <paramref name="balanceOfB"/>} (aynı üretilmiş numara); defter bakiyeyle tutarlı
    /// (D1 yok).</summary>
    private static (Guid A, Guid B, V.ProjectionRow[] Projections, V.BalanceRow[] Balances, V.LedgerRow[] Ledger)
        Person(decimal balanceOfB = -60m)
    {
        var g = $"g{Guid.NewGuid():N}";
        var (a, b) = (Guid.NewGuid(), Guid.NewGuid());
        return (a, b,
            new V.ProjectionRow[] { new(a, Lic, g, null, false), new(b, Lic, g, null, false) },
            new V.BalanceRow[] { new(Lic, a, 10m), new(Lic, b, balanceOfB) },
            new V.LedgerRow[] { new(Lic, a, 10m, 1), new(Lic, b, balanceOfB, 1) });
    }

    [Fact]
    public void Havuz_kipinde_aciklanamayan_eksi_kisi_D3_uyarisi()
    {
        var p = Person();

        var r = Single(Snap(PoolSince, p.Projections, p.Balances, p.Ledger));

        (r.NegativePersons, r.NegativePersonsExplained).Should().Be((1, 0), "10 − 60 < 0, eksi satır tabanda değil");
        r.Alerts.Should().Contain(a => a.StartsWith("D3: havuzu < 0 kişi 1", StringComparison.Ordinal));
        r.Alerts.Should().Contain(D3Line);
    }

    [Fact]
    public void Satir_kipinde_yalniz_sayi_uyari_yok()
    {
        var p = Person();

        var r = Single(Snap(poolSince: null, p.Projections, p.Balances, p.Ledger));

        (r.NegativePersons, r.NegativePersonsExplained).Should().Be((1, 0));
        r.Alerts.Should().NotContain(a => a.StartsWith("D3", StringComparison.Ordinal),
            "§8.1: D3 uyarısı yalnız havuz kipindeki lisansta; satır kipinde 6. madde yalnız sayı");
        r.Alerts.Should().BeEmpty("taban da yok: eksi satır yalnız sayılır");
    }

    [Fact]
    public void Tabanla_aciklanan_eksi_kisi_uyari_degil()
    {
        var same = Person(-60m);
        var accepted = Single(Snap(PoolSince, same.Projections, same.Balances, same.Ledger,
            [Marker(), new V.BaselineRow(Lic, same.B, -60m, -60m, Captured, Captured)]));

        (accepted.NegativePersons, accepted.NegativePersonsExplained).Should().Be((1, 1),
            "eksi satır tabanda ve tabandaki değerinde");
        accepted.Alerts.Should().BeEmpty("açıklanan eksi kişi D3 değildir; satır da taban içi");

        var worse = Person(-70m);
        var r = Single(Snap(PoolSince, worse.Projections, worse.Balances, worse.Ledger,
            [Marker(), new V.BaselineRow(Lic, worse.B, -60m, -60m, Captured, Captured)]));

        (r.NegativePersons, r.NegativePersonsExplained).Should().Be((1, 0), "eksi satır tabandakinin altına indi");
        r.Alerts.Should().Equal("eksi: taban dışı 0, daha da eksiye inen 1", D3Line);
    }

    [Fact]
    public void Taban_yokken_de_D3_uyarisi()
    {
        var p = Person();
        // Taban varken eksi, D1 ve kopya kalemlerinin üçünü de tetikleyecek satırlar: B eksi, broken'da
        // Balance ≠ Σ, copy kopyada bakiye.
        var (broken, head, copy) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var projections = p.Projections.Concat(new V.ProjectionRow[]
        {
            new(broken, Lic, null, null, false), new(head, Lic, null, null, false), new(copy, Lic, null, head, false),
        });
        var balances = p.Balances.Concat(new V.BalanceRow[] { new(Lic, broken, 50m), new(Lic, copy, 3m) });
        var ledger = p.Ledger.Concat(new V.LedgerRow[] { new(Lic, broken, 40m, 1), new(Lic, copy, 3m, 1) });

        var r = Single(Snap(PoolSince, projections, balances, ledger));

        r.HasBaseline.Should().BeFalse();
        (r.NegativeRows.Outside, r.LedgerMismatches.Outside, r.CopyRows.Outside).Should().Be((1, 1, 1));
        r.Alerts.Should().Equal(new[] { D3Line },
            "plan S2 fark 16: D3 tabandan bağımsız; eksi/D1/kopya kalemleri taban yokken yalnız sayı");
    }
}
