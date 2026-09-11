using Microsoft.Data.Sqlite;
using OrderDeck.Core.Customers;

namespace OrderDeck.Core.Storage;

/// <summary>
/// Arama anahtarlarını üreten uygulama tanımlı SQL fonksiyonları. Göç 035'teki
/// tetikleyiciler bunları çağırır; böylece <c>Customer.SearchKey</c> /
/// <c>PhoneKey</c> kolonlarını GÜNCEL TUTMAK hiçbir C# yazma yolunun sorumluluğu
/// değil — tabloya kim yazarsa yazsın SQLite tetikleyiciyi çalıştırır.
///
/// <para><b>Neden tetikleyici, neden her repo metodunda elle yazmıyoruz.</b>
/// <c>CustomerRepository</c>'de Username/DisplayName/FullName/Phone'a dokunan
/// dokuz ayrı ifade var (chat upsert, intake upsert, kişi upsert, FullName
/// backfill, telefon güncelleme, KVKK boşaltma...). Her birine anahtar
/// hesaplamayı eklemek bugünü çözer, yarını çözmez: onuncu yazma yolu eklendiği
/// gün arama sessizce eskir — ve bu sessiz bir arıza olur, çünkü kayıt DURUR,
/// sadece aranamaz hâle gelir. Tetikleyici bu sınıfı yapısal olarak imkânsız
/// kılıyor.</para>
///
/// <para><b>Kayıt edilmezse ne olur.</b> Yazma işlemleri
/// <c>no such function: od_search_key</c> ile PATLAR — sessizce yanlış anahtar
/// üretmez. Gürültülü arıza bilerek seçildi; bu yüzden fonksiyonlar
/// <see cref="IDbConnectionFactory"/>'nin HER açtığı bağlantıya kaydedilir.</para>
/// </summary>
public static class SqliteSearchFunctions
{
    public static void Register(SqliteConnection connection)
    {
        // isDeterministic: aynı girdi → aynı çıktı. SQLite'ın ifadeyi
        // önbelleklemesine / indeks bağlamında kullanmasına izin verir.
        connection.CreateFunction<string?, string?, string?, string>(
            "od_search_key",
            (username, displayName, fullName) =>
                CustomerSearch.BuildSearchKey(username, displayName, fullName),
            isDeterministic: true);

        connection.CreateFunction<string?, string>(
            "od_phone_key",
            phone => CustomerSearch.NormalizePhoneKey(phone),
            isDeterministic: true);
    }
}
