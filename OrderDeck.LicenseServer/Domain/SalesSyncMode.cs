namespace OrderDeck.LicenseServer.Domain;

/// <summary>
/// Lisans başına satış senkronu kipi (<see cref="License.SalesSyncMode"/>,
/// yayın ve etiket senkronu spec'i 2026-10-13 §2 "Mod kapısı"). Kolon
/// <c>tinyint</c>; değerler telde ve istemcinin bildirdiği görülen kipte
/// (<see cref="WpfReplica.SeenMode"/>) aynıdır, bu yüzden sabittir.
/// </summary>
public enum SalesSyncMode : byte
{
    /// <summary>Bugünkü davranış: her bilgisayar kendi yayınlarını push'la
    /// yazar. Varsayılan; yalnız CLI değiştirir.</summary>
    Off = 0,

    /// <summary>Akış, komutlar, sunucuya ait kargo, lisans geneli kargo ayarı
    /// ve ödeme kirası birlikte açık.</summary>
    On = 1,
}
