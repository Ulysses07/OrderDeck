namespace OrderDeck.LicenseServer.Domain;

/// <summary>
/// Numarasız geldiği için panele düşemeyen gelen mesajların sayacı — müşteri
/// başına <b>tek satır</b>, mesaj başına değil.
///
/// <para><b>Neden veritabanı, neden log değil:</b> sunucuda kalıcı log yok.
/// Günlükler konteynerin <c>json-file</c> sürücüsüne yazılıyor ve her deploy
/// konteyneri yeniden yarattığı için geçmiş siliniyor — master'a her merge bir
/// deploy demek, yani ölçüm pratikte saatler yaşıyor. Bu tablonun tek işi,
/// "BSUID'i sohbet kimliği yapma" kararını tahminle değil <b>sayıyla</b>
/// verebilmek; o kararı verene kadar da hayatta kalmak.</para>
///
/// <para><b>Neden toplu sayaç:</b> soru "kaç mesaj düştü" değil, <b>"kaç
/// müşteriyi kaybediyoruz ve ne zamandan beri"</b>. Müşteri başına tek satır
/// hem bu soruyu doğrudan cevaplıyor hem de tabloyu etkilenen müşteri sayısıyla
/// sınırlıyor.</para>
///
/// <para><b>Mesaj içeriği BİLEREK saklanmıyor.</b> Saklasak bile kimse görmüyor:
/// panel bu mesajları göstermiyor. Canlı satışta değer dakikalarla ölçülüyor —
/// BSUID sohbet kimliği yapıldıktan sonra geri yüklenen bir mesajın ticari
/// karşılığı yok, yalnız KVKK yüzeyi büyür. Kayıp mesajın kendisi zaten Meta
/// tarafından yeniden gönderilmiyor.</para>
///
/// <para><b>Kimliksiz mesajlar burada yok.</b> Hem telefonu hem BSUID'i olmayan
/// bir mesaj kimin olduğunu söylemiyor; hepsini tek satırda toplamak sayacı
/// şişirir ve "kaç müşteri" sorusunun cevabını bozardı. Onlar yalnız
/// loglanıyor.</para>
/// </summary>
public sealed class WaDroppedInbound
{
    public Guid Id { get; set; }

    public Guid LicenseId { get; set; }
    public License? License { get; set; }

    /// <summary>Meta'daki <c>from_user_id</c> (business-scoped user ID).
    /// Sayacın anahtarı — telefon zaten yok.</summary>
    public string BsuId { get; set; } = "";

    /// <summary>Müşterinin yazdığı BİZİM numaramız. Yayıncının birden çok hattı
    /// varsa kaybın hangisinde olduğunu söyler.</summary>
    public string PhoneNumberId { get; set; } = "";

    /// <summary>Bu müşteriden kaç mesaj düştü. Israrla yazan bir müşteri,
    /// bir kez yazıp vazgeçenden farklı bir aciliyet demek.</summary>
    public int MessageCount { get; set; }

    /// <summary>İlk düşen mesajın Meta damgası — "ne zamandan beri
    /// kaybediyoruz" sorusunun cevabı. Kullanıcı adı özelliğinin bölgemize
    /// açıldığı anı da bu gösterecek.</summary>
    public DateTimeOffset FirstSeenAt { get; set; }

    /// <summary>En son düşen mesajın Meta damgası.</summary>
    public DateTimeOffset LastSeenAt { get; set; }
}
