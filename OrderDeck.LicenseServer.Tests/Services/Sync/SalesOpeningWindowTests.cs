using FluentAssertions;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Sync;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Sync;

/// <summary>
/// Açılış penceresi doğruluk tablosu — spec 2026-10-13 §4.0, §8.4, §8.7, M16, M21. Pencere yalnız mod On'da,
/// <c>SeenMode = 0</c> bildiren ve dört koşulu birlikte tutan kopyaya açılır; her koşulun TEK BAŞINA bozulması
/// pencereyi kapatmalı (koşullar "ve" ile bağlı, hiçbiri gereksiz değil).
/// </summary>
public sealed class SalesOpeningWindowTests
{
    private static readonly DateTimeOffset ModeChangedAt = new(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);

    /// <summary>Dört koşulun hepsini tutan kopya: mod açılmadan önce vardı, geri yüklenmemiş, klon değil, modu
    /// görmemiş.</summary>
    private static ReplicaHistory OpenHistory() => new(
        FirstSeenAt: ModeChangedAt.AddDays(-3),
        PreviousReplicaId: null,
        SeenModeOnAt: null,
        FingerprintConflict: false);

    private static bool Applies(ReplicaHistory? before,
        SalesSyncMode mode = SalesSyncMode.On, SalesSyncMode seen = SalesSyncMode.Off,
        DateTimeOffset? changedAt = null)
        => SalesOpeningWindow.Applies(mode, changedAt ?? ModeChangedAt, seen, before);

    [Fact]
    public void Dort_kosul_birlikte_tutarsa_pencere_acik()
        => Applies(OpenHistory()).Should().BeTrue();

    [Fact]
    public void Mod_acilmadan_once_yoksa_pencere_kapali()
    {
        Applies(OpenHistory() with { FirstSeenAt = ModeChangedAt.AddMinutes(1) }).Should().BeFalse();
        Applies(OpenHistory() with { FirstSeenAt = ModeChangedAt }).Should().BeFalse("sınır: aynı an önce değildir");
    }

    [Fact]
    public void Geri_yuklenmis_kopya_hic_pencere_sayilmaz()
    {
        var restored = OpenHistory() with { PreviousReplicaId = Guid.NewGuid() };
        Applies(restored).Should().BeFalse();
        // Başka hiçbir koşul onu pencereye sokamaz (§8.7): modu hiç görmemiş, çok eski bir kopya olsa bile.
        Applies(restored with { FirstSeenAt = ModeChangedAt.AddYears(-1), SeenModeOnAt = null }).Should().BeFalse();
    }

    [Fact]
    public void Ikinci_parmak_izi_varsa_pencere_kapali()
        => Applies(OpenHistory() with { FingerprintConflict = true }).Should().BeFalse();

    [Fact]
    public void Modu_bu_acilistan_beri_gormus_kopya_pencerede_degil()
    {
        Applies(OpenHistory() with { SeenModeOnAt = ModeChangedAt.AddSeconds(30) }).Should().BeFalse();
        Applies(OpenHistory() with { SeenModeOnAt = ModeChangedAt }).Should().BeFalse("sınır: aynı an eski değildir");
        // Önceki On döneminde görmüş, mod kapatılıp yeniden açılmış: yeni dönemi görmemiş → pencere açık.
        Applies(OpenHistory() with { SeenModeOnAt = ModeChangedAt.AddDays(-1) }).Should().BeTrue();
    }

    [Fact]
    public void Mod_Off_iken_pencere_hic_yok()
        => Applies(OpenHistory(), mode: SalesSyncMode.Off).Should().BeFalse();

    [Fact]
    public void SeenMode_1_bildiren_istek_pencerede_degil()
        => Applies(OpenHistory(), seen: SalesSyncMode.On).Should().BeFalse();

    [Fact]
    public void Bu_istekle_dogan_kopya_pencerede_degil()
        => Applies(before: null).Should().BeFalse();

    [Fact]
    public void Kip_degisim_ani_bilinmiyorsa_pencere_kapali()
        => SalesOpeningWindow.Applies(SalesSyncMode.On, null, SalesSyncMode.Off, OpenHistory())
            .Should().BeFalse("belirsizlikte On kuralları: karar korunur");
}
