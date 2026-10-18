using System;
using OrderDeck.Core.Settings;

namespace OrderDeck.Tests.TestHelpers;

/// <summary>
/// Veritabanı kurmayan birim testlerinin kargo ayarı sağlayıcısı: gerçek <see cref="ShippingSettingsProvider"/>'ın mod Off
/// kolu (bugün her lisans) — her okumada verilen yerel ayarın <see cref="AppSettings.Shipping"/>'i, sürüm yok. Mod On'u
/// sınayan testler gerçek sağlayıcıyı test veritabanıyla kurar.
/// </summary>
public sealed class LocalShipping(Func<AppSettings> settings) : IShippingSettingsProvider
{
    /// <summary>Verilen örnek (testin sonradan yaptığı düzenleme dahil).</summary>
    public static LocalShipping Of(AppSettings settings) => new(() => settings);

    /// <summary>Ayar dosyası, her okumada yeniden (testler ayarı servis kurulduktan sonra da kaydediyor).</summary>
    public static LocalShipping Of(SettingsStore store) => new(store.Load);

    /// <summary>Kapalı ayar (eşik ve ücret boş).</summary>
    public static LocalShipping Off() => Of(new AppSettings());

    public ShippingSnapshot Current() => new(settings().Shipping, Version: null, LicenseWide: false);
}
