namespace OrderDeck.LicenseServer.Services.IntakeForm;

/// <summary>
/// Kayıt formundaki KVKK aydınlatma metninin sürümü. Her gönderimle birlikte
/// <see cref="Domain.IntakeFormSubmission.KvkkTextVersion"/> olarak saklanır:
/// "bu kişi HANGİ metni okuduğunu onayladı" sorusunun cevabı bu değer.
/// Metnin kendisi kodda (<c>Pages/Public/IntakeForm.cshtml</c>, <c>p.kvkk</c>
/// paragrafı + altındaki onay kutusu etiketi + etiketin "okudum" dediği,
/// formun içinde açılan Gizlilik Politikası kutusu
/// <c>Pages/Public/_GizlilikPolitikasi.cshtml</c>; o da
/// <c>web/app/(tr)/gizlilik-politikasi/page.tsx</c>'in kopyası, ikisi aynı
/// tutuluyor); o sürümün tam metni git geçmişinde bu sabitin değiştiği
/// commit'te durur.
///
/// <para><b>Kayda giden değer müşterinin GÖRDÜĞÜ sürüm.</b> Sayfa bu sabiti
/// gizli alana basıyor; sunucu onayı yalnız gelen değer o anki sabite eşitse
/// kabul ediyor (<c>IntakeFormModel.RequireKvkkAcknowledgement</c>). Sürüm
/// artarken açık kalmış bir sekmeden gelen onay reddedilir ve müşteri yeni
/// metni görüp yeniden onaylar; böylece hiçbir satır, müşterinin görmediği
/// bir sürümü taşımaz.</para>
///
/// <para><b>Artırma kuralı.</b> Aydınlatma paragrafının, onay kutusu
/// etiketinin ya da formdaki Gizlilik Politikası kutusunun (bağlantı
/// adresleri dahil) ANLAMINI değiştiren her düzenlemede yeni değer ver —
/// biçim <c>kvkk-YYYY-AA-GG</c>, tarih yeni metnin yayına girdiği gün.
/// Politikanın web sayfası ile form kopyası birlikte değişir: birini
/// değiştiren diğerini de günceller ve bu değeri artırır. Yazım/boşluk
/// düzeltmesi gibi anlamı değiştirmeyen dokunuşlar artırma gerektirmez.
/// Eski değeri yeniden KULLANMA: eski satırlar onu taşıyor, aynı ada iki
/// farklı metin bağlanırsa ispat değeri kalmaz.</para>
///
/// <para><c>IntakeFormKvkkTests.Metin_degisirse_surum_de_degismeli</c> form
/// metninin, formda gösterilen politika metninin ve web politika kaynağının
/// özetini bu değere bağlıyor; biri değişince kırmızı yanar ve artırma
/// kararını bilinçli verdirir.</para>
///
/// <para>Sürüm geçmişi: <c>kvkk-2026-10-13</c> politika orderdeckapp.com'a
/// yeni sekmede giden bağlantıydı; <c>kvkk-2026-10-14</c> politika metni
/// formun içinde açılan kutuda; <c>kvkk-2026-10-19</c> banka hesap
/// hareketleri (politika Bölüm 1.4, saklama ve haklar) ve form paragrafında
/// havale cümlesi; <c>kvkk-2026-10-21</c> hukuki ön inceleme notuna göre
/// düzeltilmiş sürüm (v2): Bölüm 1.4 kodun davranışına göre yeniden yazıldı
/// (rol amaca göre ayrıldı, yanlış olgu beyanları çıktı), yeni Bölüm 1.2a
/// (yayıncının sunucuda tutulan müşteri ve satış kayıtları), 1.5 (müşteri
/// uygulaması ve dekontlar, yayıncılar arası dekont özeti karşılaştırması),
/// 1.6 (hizmet sağlayıcılar ve yurt dışına aktarım) ve 1.7 (WhatsApp
/// yazışmaları; geçmiş sohbet ve rehber aktarımı, hesapla silinen dosyalar), 1.2'de
/// Instagram kayıt bağlantısı istisnası, 1.3 ve
/// 1.5'te hukuki sebepler, Bölüm 5–7 ve 9 yeniden yazıldı; form paragrafı iki
/// katmanlı (veri sorumlusu, amaç, havale / izinler), onay etiketi
/// "Yukarıdaki bilgilendirmeyi okudum".</para>
///
/// <para><b>kvkk-2026-10-21 TASLAKTIR, yayın kapılıdır.</b> Anlattığı kod
/// (#534 banka verisi, #542 silme ve saklama) master'da; metin ayrıca canlıda
/// doğrulanacak ve elle yapılacak prod adımlarına dayanır; "[YER TUTUCU: …]"
/// işaretli yerleri avukat ve Burak kararı bekler. Yayın kapısının tam listesi bu metnin PR açıklamasında. Tarih
/// yer tutucudur: yayın günü gerçek tarihe çekilir (iki kopyanın yürürlük
/// tarihi ve "Son değişiklik" satırı, Veri Silme sayfaları, İngilizce sayfanın
/// EFFECTIVE_DATE ve TURKISH_EFFECTIVE_DATE'i, IntakeFormKvkkTests'in
/// özetleriyle birlikte).</para>
/// </summary>
public static class KvkkNotice
{
    public const string TextVersion = "kvkk-2026-10-21";
}
