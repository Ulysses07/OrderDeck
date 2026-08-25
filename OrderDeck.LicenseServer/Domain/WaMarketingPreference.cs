namespace OrderDeck.LicenseServer.Domain;

/// <summary><see cref="WaMarketingPreference.Preference"/> için sabitler —
/// Meta'nın <c>user_preferences</c> webhook'undaki <c>value</c> alanı.</summary>
public static class WaMarketingPreferences
{
    public const string Stop = "stop";
    public const string Resume = "resume";

    /// <summary>Meta'nın bugün belgelediği tek kategori. Tercih webhook'unda
    /// gelen kategori olduğu gibi kullanılır; bu sabite yalnızca kararı
    /// <b>biz çıkarsadığımızda</b> (131050) ihtiyaç var.</summary>
    public const string MarketingCategory = "marketing_messages";
}

/// <summary><see cref="WaMarketingPreference.Source"/> için sabitler: kararı
/// nereden öğrendik.</summary>
public static class WaMarketingPreferenceSources
{
    /// <summary>Müşterinin kendi beyanı — <c>user_preferences</c> webhook'u.</summary>
    public const string UserPreferences = "user_preferences";

    /// <summary>Çıkarım — pazarlama mesajımız <c>131050</c> ile düştü.</summary>
    public const string Error131050 = "error_131050";
}

/// <summary>
/// Müşterinin pazarlama mesajı tercihi: Meta'nın <c>user_preferences</c>
/// webhook'undan gelen "beni çıkar / geri al" kararının kalıcı defteri.
///
/// <para><b>Neden veritabanı, neden log değil:</b> Meta bu tercihi okumak için
/// bir uç nokta sunmuyor ve olayı yeniden göndermiyor. Webhook tek kaynak ve
/// fire-and-forget. Kaçırılan bir <c>stop</c> geri getirilemez — log rotasyona
/// uğrayıp yok olduğunda müşterinin kararı da yok olur. Kaybı görünür kılmak
/// (bkz. <c>DroppedNoPhoneUserIds</c>) burada yetmiyor; kaydetmek gerekiyor.</para>
///
/// <para><b>Neden iki kimlik alanı:</b> payload hem <c>wa_id</c> hem BSUID
/// (<c>user_id</c>) taşıyabiliyor ve kullanıcı adı özelliğini açmış müşterilerde
/// <c>wa_id</c> hiç gelmiyor. Yalnız telefona anahtarlamak, defteri tam da
/// korumaya çalıştığı müşterilerde kırardı.</para>
///
/// <para><b>Şu an tüketicisi YOK.</b> Gönderim öncesi engelleme bilinçli olarak
/// yapılmadı: Meta zaten pazarlama şablonunu düşürüp durum webhook'unda
/// <c>failed</c> + <c>131050</c> döndürüyor. Burada tutulan şey, o kararın
/// bizde de bir karşılığının olması. Engelleme kuralı, elde gerçek veri
/// biriktikten sonra yazılacak.</para>
///
/// <para><b>Bilinen sınır:</b> aynı kişi için iki satır oluşabilir — önce yalnız
/// BSUID'li bir olay, sonra yalnız telefonlu bir olay gelirse eşleşme kurulamaz.
/// Defterin işi kararı kaybetmemek olduğu için bu kabul edildi; birleştirme
/// kuralı, BSUID sohbet kimliği yapıldığında oradaki kuralla birlikte yazılacak.
/// Uydurma bir eşleştirme, kaydın kendisinden daha tehlikeli olurdu.</para>
/// </summary>
public sealed class WaMarketingPreference
{
    public Guid Id { get; set; }

    public Guid LicenseId { get; set; }
    public License? License { get; set; }

    /// <summary>Meta'daki <c>wa_id</c>, kanonik biçimde. Müşteri kullanıcı adı
    /// özelliğini açmışsa <c>null</c> gelir.</summary>
    public string? CustomerPhone { get; set; }

    /// <summary>Meta'daki <c>user_id</c> (business-scoped user ID,
    /// <c>US.13491208655302741918</c> gibi). Her payload'da bulunmayabilir.</summary>
    public string? BsuId { get; set; }

    /// <summary>Meta'daki <c>category</c>; bugün tek belgelenmiş değer
    /// <c>marketing_messages</c>. Meta yeni kategori eklerse ayrı satır olur —
    /// tercih kategori başına tutuluyor.</summary>
    public string Category { get; set; } = "";

    /// <summary>Meta'daki <c>value</c>: <c>stop</c> veya <c>resume</c>.
    /// Tanımadığımız bir değer gelirse olduğu gibi yazılır; yorumlamak yerine
    /// saklamak, sessizce elemekten iyidir.</summary>
    public string Preference { get; set; } = "";

    /// <summary>
    /// Meta'nın olaya bastığı zaman damgası — <b>sıralama otoritesi budur</b>.
    ///
    /// <para>Webhook'lar sırayla gelmeyebilir ve Hangfire yeniden denemesi eski
    /// bir paketi yeniden işleyebilir. Sıralamayı kendi işleme anımıza göre
    /// yaparsak geç işlenen eski bir <c>resume</c>, yeni bir <c>stop</c>'u
    /// ezer ve müşteriye çıkmak istediği mesajı göndeririz.</para>
    /// </summary>
    public DateTimeOffset PreferenceAt { get; set; }

    /// <summary>
    /// Kararı nereden öğrendik — bkz. <see cref="WaMarketingPreferenceSources"/>.
    /// Yürürlükteki <see cref="Preference"/> ile birlikte güncellenir; satırın
    /// değil, <b>kararın</b> özelliğidir.
    ///
    /// <para><b>Neden ayrı sütun:</b> defterde iki farklı güçte kanıt birikiyor.
    /// <c>user_preferences</c> müşterinin kendi beyanı; <c>131050</c> ise bizim
    /// çıkarımımız — Meta mesajı düşürdü, biz de "demek ki çıkmış" dedik. İkisi
    /// aynı sütuna yazılıp ayırt edilemez hâle gelirse defter, müşterinin
    /// söylemediği bir şeyi söylemiş gibi gösterir. Gönderim engelleme kuralı
    /// yazılırken ya da "ben çıkmadım" itirazı geldiğinde sorulacak soru tam
    /// olarak "bunu nereden biliyoruz" olacak.</para>
    ///
    /// <para>Sütunu sonradan eklemek de mümkündü, ama o zaman mevcut her satır
    /// kalıcı olarak "bilinmiyor" kalırdı; şu an defter boş olduğu için bedeli
    /// sıfır.</para>
    /// </summary>
    public string Source { get; set; } = "";

    /// <summary>Satırın bizde en son ne zaman yazıldığı (teşhis için;
    /// karar sıralamasında kullanılmaz).</summary>
    public DateTimeOffset UpdatedAt { get; set; }
}
