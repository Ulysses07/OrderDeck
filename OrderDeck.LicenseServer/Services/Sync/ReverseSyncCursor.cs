namespace OrderDeck.LicenseServer.Services.Sync;

/// <summary>
/// WPF'in sunucudan artımlı çektiği <c>.../since</c> uçlarının ortak imleç
/// kuralları. Üç uç bu kuralı paylaşıyor: dekontlar, kargolar ve shopper
/// kayıtlarından türeyen müşteri projeksiyonları.
///
/// <para><b>İmleç neden bileşik.</b> Yalnız zaman damgası üstünde koşan bir
/// imleç, aynı damgayı paylaşan satırların ortasından geçen sayfa sınırında
/// satır kaybeder: istemci sayfanın en büyük damgasını imleç yapıp bir sonraki
/// çağrıda <c>&gt; imleç</c> sorar, aynı damgadaki kalan satırlar bir daha hiç
/// dönmez. Eşitlik varsayımsal değil — WPF tek push'ta 200 dekontu tek bir
/// <c>UpdatedAt</c> damgasıyla yazıyor ve istemcinin sayfa boyu da 200.
/// Damganın yanına birincil anahtar eklenince sıra toplam sıralama olur ve
/// sayfa sınırı bir daha belirsiz kalmaz.</para>
///
/// <para><b>Kararlılık ufku neden gerekli.</b> Bileşik imleç eşitlik sorununu
/// çözer ama daha sinsi olanına dokunmaz: <b>commit sırası damga sırasına eşit
/// değil.</b> Bir yazıcı <c>UtcNow</c>'ı okuduktan sonra commit'e kadar
/// milisaniyeler geçiyor; o arada başka bir yazıcı daha YENİ damgayla commit
/// edip imleci ileri taşıyabilir. Uçuştaki işlem sonradan commit edince satırı
/// imlecin GERİSİNE düşer ve istemci onu bir daha hiç görmez. Dekontta bunun
/// bedeli somut: mobilde onaylanmış bir dekont masaüstünde sonsuza dek
/// "Bekliyor" kalır — üstelik kendi kendine onarılmaz, çünkü karara bağlanmış
/// dekonta bir daha dokunulmaz.</para>
///
/// <para>Çözüm tabanı geriye çekmek (örtüşme payı) değil, TAVAN koymak: bu
/// ufkun üstündeki satırlar hiç okunmuyor. Uçuştaki işlem damgasını az önce
/// okuduğu için hep ufkun üstünde kalır, dolayısıyla imleç onu atlayamaz.
/// Örtüşme payı seçilmedi çünkü aynı damgayı paylaşan satır sayısı sayfa boyunu
/// aşarsa taban her çağrıda geriye kayar ve imleç hiç ilerlemez.</para>
///
/// <para><b>Neden 5 saniye</b> — stok ucundaki 60 saniye değil. Süre, tek bir
/// yazma işleminin damgasını okumasıyla commit etmesi arasındaki en uzun
/// aralığı örtmeli; dekont onayı tek satırlık bir UPDATE, batch push ise en
/// fazla 200 satırlık tek bir SaveChanges — ikisi de milisaniye mertebesinde,
/// 5 saniye üç basamak pay bırakıyor. Bedeli doğrudan kullanıcıya yansıdığı
/// için de kısa tutuldu: mobilde verilen onayın masaüstüne düşmesi bu ufuk
/// kadar gecikir. Stokta 60 saniye kabul edilebilirdi çünkü orada gecikme
/// görünmez; dekontta görünür.</para>
/// </summary>
public static class ReverseSyncCursor
{
    /// <summary>Bkz. sınıf açıklaması — bu sürenin içinde değişen satırlar okunmaz.</summary>
    public static readonly TimeSpan StabilityHorizon = TimeSpan.FromSeconds(5);

    /// <summary>Sorgunun okumayı göze alabileceği en yeni <c>UpdatedAt</c>.</summary>
    public static DateTimeOffset Horizon() => DateTimeOffset.UtcNow - StabilityHorizon;
}
