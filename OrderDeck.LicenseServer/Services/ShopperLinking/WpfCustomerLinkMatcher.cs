using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Auth;

// Namespace kasten "ShopperLinking": "Services.Shopper" olsaydı aynı adı taşıyan
// Shopper varlığıyla ad çakışırdı (CS0118).
namespace OrderDeck.LicenseServer.Services.ShopperLinking;

/// <summary>
/// Bir shopper'ı yayıncının müşteri kaydına (<see cref="WpfCustomerProjection"/>)
/// bağlamanın TEK karar noktası.
///
/// Neden tek yer: aynı eşleştirme üç ayrı akışta yapılıyor — kayıt, yayıncıya
/// katılma ve WPF sync'inin geriye dönük eşleştirmesi. Üçü de yalnız
/// (Platform, Username) bakıyordu; bu bilgi ürünün kendi tasarımı gereği
/// HERKESE AÇIK (canlı yayın sohbetinde görünen kullanıcı adı). Yani bir
/// saldırgan sohbetten bir kullanıcı adı alıp o adla kaydolduğunda kurbanın
/// sipariş geçmişini ve bakiye/hesap hareketlerini okuyabiliyordu. Kural üç
/// kopyada yaşasaydı biri düzeltilip diğeri unutulabilirdi — nitekim geriye
/// dönük eşleştirme gözden kaçmaya en yatkın olanıydı: orada bir gedik kalsa
/// diğer iki düzeltme tamamen boşa giderdi, çünkü bekleyen link bir sonraki
/// WPF sync'inde kendiliğinden bağlanırdı.
///
/// Kanıt olarak telefon numarası seçildi: kullanıcı adının aksine herkese açık
/// değil. <c>Shopper.Phone</c> global benzersiz olduğu için kurbanın hesabı
/// zaten varsa saldırgan onun numarasıyla kaydolamaz bile.
/// </summary>
public static class WpfCustomerLinkMatcher
{
    /// <summary>
    /// Adaylar arasından sahipliği kanıtlanmış olanı seçer; kanıt yoksa
    /// <c>null</c> döner ve bağlantı "bekleyen" (WpfCustomerId = null) kalır.
    ///
    /// Telefonu olmayan aday da eşleşme SAYILMAZ: yayıncı tarafında telefonu
    /// kayıtlı olmayan müşteriler (sohbetten gelen, form doldurmamış olanlar)
    /// için ortada kanıt yoktur. Bu, meşru müşterilerin bir kısmının beklemede
    /// kalması demek; kurtarma yolu yayıncının WPF'te telefonu girmesi, ardından
    /// ilk sync'te geriye dönük eşleştirmenin bağlantıyı kurmasıdır.
    ///
    /// Karşılaştırma iki tarafı da normalize ederek yapılır. Bugün her giriş
    /// yolu (kayıt formu, WPF telefon girişi, shopper kaydı) zaten E.164
    /// üretiyor; normalize etmek, telefon alanının serbest metin olduğu
    /// dönemden kalan satırların sessizce eşleşmemesini engelliyor.
    /// </summary>
    public static WpfCustomerProjection? FindProven(
        IEnumerable<WpfCustomerProjection> candidates,
        string shopperPhone)
        => candidates.FirstOrDefault(c => PhoneProves(c.Phone, shopperPhone));

    /// <summary>
    /// Kuralın kendisi: yayıncı tarafındaki telefon ile shopper'ın telefonu aynı
    /// kişiye mi ait? Her iki taraf da boş/geçersiz olabilir; o durumda kanıt
    /// yoktur (<c>false</c>).
    /// </summary>
    public static bool PhoneProves(string? broadcasterSidePhone, string? shopperPhone)
        => PhoneNormalizer.TryNormalize(shopperPhone, out var shopperNorm)
           && PhoneNormalizer.TryNormalize(broadcasterSidePhone, out var candidateNorm)
           && candidateNorm == shopperNorm;
}
