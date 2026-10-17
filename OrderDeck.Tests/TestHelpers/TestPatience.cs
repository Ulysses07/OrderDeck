namespace OrderDeck.Tests.TestHelpers;

/// <summary>
/// Testlerde "olması gereken bir şeyi bekle" sınırları için ortak ÜST SINIR (ör. tutulan isteğin sunucuya varması,
/// arka plan turunun bitmesi). Bekleme süresi değildir: koşul sağlanınca hemen döner, yeşil koşu uzamaz. Tam pakette
/// (prod boyu göç provası, uzun yayın stres testi aynı anda) 10 sn'lik sınırlar tek başına 1 sn süren testleri aralıklı
/// olarak düşürüyordu. Bir şeyin OLMADIĞINI ya da süresini sınayan sınırlar (bütçe, "canlı yol beklemez") buraya
/// bağlanmaz; onlar davranışın kendisidir.
/// </summary>
internal static class TestPatience
{
    public static readonly TimeSpan Upper = TimeSpan.FromSeconds(30);
}
