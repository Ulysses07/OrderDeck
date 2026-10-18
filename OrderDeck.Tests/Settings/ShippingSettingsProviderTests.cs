using System;
using Dapper;
using FluentAssertions;
using OrderDeck.Core.Settings;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.Tests.Settings;

/// <summary>
/// Lisans geneli kargo ayarı sağlayıcısı (spec 2026-10-13 §6.2, Y4): yerel kip 1 ise <c>sales/state</c>'in önbelleği,
/// değilse CANLI <see cref="AppSettings.Shipping"/>. Her okumada yeniden okur.
/// </summary>
public sealed class ShippingSettingsProviderTests : IDisposable
{
    private readonly InMemorySqlite _db = new();
    private readonly SalesSyncStateRepository _state;
    private readonly AppSettings _live = new();
    private readonly ShippingSettingsProvider _sut;

    public ShippingSettingsProviderTests()
    {
        new MigrationRunner(_db).Run();
        _state = new SalesSyncStateRepository(_db);
        _live.Shipping.FreeShippingThreshold = 500m;
        _live.Shipping.ShippingFee = 60m;
        _sut = new ShippingSettingsProvider(_state, _live);
    }

    public void Dispose() => _db.Dispose();

    private void Mode(int mode)
    {
        using var conn = _db.Open();
        conn.Execute("UPDATE SalesSyncState SET Mode = @mode WHERE Id = 1", new { mode });
    }

    [Fact]
    public void Mod_Off_canli_yerel_ornegi_doner_sonradan_yapilan_duzenleme_dahil_surum_yok()
    {
        // Sunucu ayarı önbellekte dursa da (sales/state iki kipte bildirir) mod Off'ta okunmaz.
        _state.SaveServerState(3, 1000m, 80m, scrubOrderText: false);

        var snapshot = _sut.Current();

        snapshot.Settings.Should().BeSameAs(_live.Shipping, "canlı örneğin kendisi, kopya değil");
        snapshot.Version.Should().BeNull("yerel ayarın sürümü yok");
        snapshot.LicenseWide.Should().BeFalse();

        // Ayar ekranı canlı örneği düzenler; disable turunun kopyası örneği değiştirir (§8.5-2).
        _live.Shipping.FreeShippingThreshold = 700m;
        _sut.Current().Settings.FreeShippingThreshold.Should().Be(700m);
        _live.Shipping = new ShippingSettings { FreeShippingThreshold = 900m, ShippingFee = 70m };
        _sut.Current().Settings.Should().BeSameAs(_live.Shipping);
        _sut.Current().Settings.ShippingFee.Should().Be(70m);
    }

    [Fact]
    public void Mod_On_onbellegi_doner_yerel_ayar_farkli_olsa_da()
    {
        _state.SaveServerState(3, 1000m, 80m, scrubOrderText: false);
        Mode(1);

        var snapshot = _sut.Current();

        snapshot.Settings.FreeShippingThreshold.Should().Be(1000m);
        snapshot.Settings.ShippingFee.Should().Be(80m);
        snapshot.Settings.IsEnabled.Should().BeTrue();
        snapshot.Version.Should().Be(3);
        snapshot.LicenseWide.Should().BeTrue();
        snapshot.Settings.Should().NotBeSameAs(_live.Shipping);
        _live.Shipping.FreeShippingThreshold.Should().Be(500m, "yerel ayara yazılmaz");

        // Sonraki state turu yeni değeri saklar: sağlayıcı önbelleklemez.
        _state.SaveServerState(4, 1200m, 90m, scrubOrderText: false);
        _sut.Current().Should().Match<ShippingSnapshot>(s =>
            s.Version == 4 && s.Settings.FreeShippingThreshold == 1200m && s.Settings.ShippingFee == 90m);
    }

    [Fact]
    public void Mod_On_bos_onbellek_kapali_ayar_surum_0()
    {
        // Sunucuda ayar hiç yazılmadı (sales/state üç alanı boş bildirir): sunucu satır yokken sürümü 0 sayar (§4.8).
        Mode(1);

        var snapshot = _sut.Current();

        snapshot.Settings.IsEnabled.Should().BeFalse();
        snapshot.Settings.FreeShippingThreshold.Should().BeNull();
        snapshot.Settings.ShippingFee.Should().BeNull();
        snapshot.Version.Should().Be(0);
        snapshot.LicenseWide.Should().BeTrue();
    }

    private void CopyPending()
    {
        using var conn = _db.Open();
        conn.Execute("INSERT INTO SyncCursor (Name, LicenseKey, Seq) VALUES (@name, '', 1)",
            new { name = SalesSyncStateRepository.ModeOffShippingCopyMarker });
    }

    [Fact]
    public void Mod_Offa_donuste_kopya_bitmediyse_lisans_geneli_onbellek_okunur()
    {
        // §6.2/§8.5: Off turu Mode=0'ı yazdı ama ayarın yerel kopyası yazılamadı (dosya kilitli, disk dolu) — işaret kaldı.
        // Sağlayıcı yalnız kipe bakıyordu: kopya bitene kadar bu bilgisayar mod On öncesinin eski yerel ayarıyla (500/60)
        // istiyor, öbürleri lisans genelinin (1000/80) kopyasıyla — iki PC farklı kargo isterdi.
        _state.SaveServerState(3, 1000m, 80m, scrubOrderText: false);
        CopyPending();

        var snapshot = _sut.Current();

        snapshot.Settings.FreeShippingThreshold.Should().Be(1000m);
        snapshot.Settings.ShippingFee.Should().Be(80m);
        snapshot.Version.Should().BeNull("mod Off: sürüm yok, kira yok");
        snapshot.LicenseWide.Should().BeFalse("ayar ekranı yerel ayarı düzenler");

        _state.ClearModeOffShippingCopyPending();
        _sut.Current().Settings.Should().BeSameAs(_live.Shipping, "kopya bitti: yerel ayar (artık lisans geneli değeri)");
    }

    [Fact]
    public void Kopya_bekliyor_ama_sunucuda_ayar_yoksa_yerel_ayar()
    {
        // Sunucuda ayar hiç yazılmadıysa tur yerel ayarı korur (kopya işareti silinir): sağlayıcı da yerel okur.
        CopyPending();

        _sut.Current().Settings.Should().BeSameAs(_live.Shipping);
    }

    [Fact]
    public void Mod_On_sunucuda_kapatilmis_ayar_kapali_surumuyle()
    {
        // Ayar yazılmış ama kapalı (ikisi de boş): kapalı, sürüm sunucununki.
        _state.SaveServerState(5, null, null, scrubOrderText: false);
        Mode(1);

        var snapshot = _sut.Current();

        snapshot.Settings.IsEnabled.Should().BeFalse();
        snapshot.Version.Should().Be(5);
    }
}
