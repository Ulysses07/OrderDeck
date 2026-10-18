using OrderDeck.Core.Storage.Repositories;

namespace OrderDeck.Core.Settings;

/// <summary>
/// Kargo eşiği ve ücretinin o anki kaynağı (spec 2026-10-13 §6.2, sahip kararı Y4).
/// </summary>
/// <param name="Settings">Eşik ve ücret. Mod Off'ta CANLI <c>AppSettings.Shipping</c> örneğinin kendisi (kopya değil); mod
/// On'da lisans geneli ayarın yerel önbelleğinden kurulan yeni bir örnek.</param>
/// <param name="Version">Lisans geneli ayarın sürümü — <c>payment-requests/begin</c>'in <c>shippingSettingsVersion</c>'ı
/// (§6.4-g). Mod On'da önbellek boşsa (sunucuda ayar hiç yazılmadı) 0: sunucu satır yokken 0 sayar. Mod Off'ta
/// <c>null</c>: yerel ayarın sürümü yoktur.</param>
/// <param name="LicenseWide">true: değer lisans geneli ayardan (mod On).</param>
public sealed record ShippingSnapshot(ShippingSettings Settings, int? Version, bool LicenseWide);

/// <summary>
/// Kargo ayarının TEK okuma noktası (§6.2). Ödeme isteğinin kargo hesabı (<c>PaymentRequestService.ComputeShipping</c>
/// yalnız <see cref="ShippingSettings"/> alır), #508 planı, dekont eşleştirmesi, eşik değerlendirmesi ve dekont ekranının
/// eşik metni değeri buradan alır; yerel ayarın mod On'da yanlışlıkla okunduğu bir yol böylece kurulamaz.
///
/// <para><b>Okumayan iki yer (bilerek):</b> <c>sales/hello</c>'nun <c>reportedShipping</c>'i her kipte yerel
/// <c>AppSettings.Shipping</c>'i bildirir (§6.2 "Geçiş": Burak farkı <c>sales-sync status</c>'ta görür) ve
/// <c>disable</c> turu sunucu değerini yerel ayara kopyalar (§8.5-2).</para>
/// </summary>
public interface IShippingSettingsProvider
{
    /// <summary>Şu anki ayar. Her çağrıda yeniden okunur (önbelleklenmez): kip ve sunucu değeri tur başına
    /// değişebilir, yerel ayar ayar ekranından ya da <c>disable</c> turunun kopyasından.</summary>
    ShippingSnapshot Current();
}

/// <summary>
/// <see cref="IShippingSettingsProvider"/>'ın uygulaması (AppHost'ta tek örnek). Yerel <c>SalesSyncState.Mode</c> 1 ise
/// <c>sales/state</c>'in her turda sakladığı lisans geneli ayarın önbelleği (<c>ShippingVersion</c>,
/// <c>FreeShippingThreshold</c>, <c>ShippingFee</c>; <see cref="SalesSyncStateRepository.SaveServerState"/>); değilse CANLI
/// <see cref="AppSettings"/> örneğinin <see cref="AppSettings.Shipping"/>'i — mod Off'ta (bugün her lisans) değer
/// bugünkünün aynısı.
/// </summary>
public sealed class ShippingSettingsProvider : IShippingSettingsProvider
{
    private readonly SalesSyncStateRepository _state;
    private readonly AppSettings _live;

    /// <param name="live">Uygulamanın CANLI ayar örneği (DI tekili): ayar ekranı ve <c>disable</c> turunun kopyası ona
    /// yazar. Özellik her okumada yeniden okunur — kopya <see cref="AppSettings.Shipping"/>'i yeni bir örnekle
    /// değiştirir.</param>
    public ShippingSettingsProvider(SalesSyncStateRepository state, AppSettings live)
    {
        _state = state;
        _live = live;
    }

    public ShippingSnapshot Current()
    {
        var cache = _state.GetShippingCache();
        if (cache.Mode != 1)
            return new ShippingSnapshot(_live.Shipping, Version: null, LicenseWide: false);

        // Sunucu ayarı hiç yazılmadıysa sales/state üç alanı da boş bildirir: kapalı ayar, sürüm 0 (§4.8, §6.4-g).
        if (cache.Version is not { } version)
            return new ShippingSnapshot(new ShippingSettings(), Version: 0, LicenseWide: true);
        return new ShippingSnapshot(
            new ShippingSettings { FreeShippingThreshold = cache.FreeShippingThreshold, ShippingFee = cache.ShippingFee },
            version, LicenseWide: true);
    }
}
