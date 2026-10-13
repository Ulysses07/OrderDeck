namespace OrderDeck.LicenseServer.Services.IntakeForm;

/// <summary>
/// Kayıt formundaki KVKK aydınlatma metninin sürümü. Her gönderimle birlikte
/// <see cref="Domain.IntakeFormSubmission.KvkkTextVersion"/> olarak saklanır:
/// "bu kişi HANGİ metni okuduğunu onayladı" sorusunun cevabı bu değer.
/// Metnin kendisi kodda (<c>Pages/Public/IntakeForm.cshtml</c>, <c>p.kvkk</c>
/// paragrafı + altındaki onay kutusu etiketi, etiketin bağlandığı
/// <c>web/app/(tr)/gizlilik-politikasi/page.tsx</c>); o sürümün tam metni git
/// geçmişinde bu sabitin değiştiği commit'te durur.
///
/// <para><b>Kayda giden değer müşterinin GÖRDÜĞÜ sürüm.</b> Sayfa bu sabiti
/// gizli alana basıyor; sunucu onayı yalnız gelen değer o anki sabite eşitse
/// kabul ediyor (<c>IntakeFormModel.RequireKvkkAcknowledgement</c>). Sürüm
/// artarken açık kalmış bir sekmeden gelen onay reddedilir ve müşteri yeni
/// metni görüp yeniden onaylar; böylece hiçbir satır, müşterinin görmediği
/// bir sürümü taşımaz.</para>
///
/// <para><b>Artırma kuralı.</b> Aydınlatma paragrafının ya da onay kutusu
/// etiketinin (bağlantı adresi dahil) ANLAMINI değiştiren her düzenlemede
/// yeni değer ver — biçim <c>kvkk-YYYY-AA-GG</c>, tarih yeni metnin yayına
/// girdiği gün. Bağlanan Gizlilik Politikası'nda form verisini ilgilendiren
/// esaslı bir değişiklik de aynı kurala tabi. Yazım/boşluk düzeltmesi gibi
/// anlamı değiştirmeyen dokunuşlar artırma gerektirmez. Eski değeri yeniden
/// KULLANMA: eski satırlar onu taşıyor, aynı ada iki farklı metin bağlanırsa
/// ispat değeri kalmaz.</para>
///
/// <para><c>IntakeFormKvkkTests.Metin_degisirse_surum_de_degismeli</c> form
/// metninin ve politika kaynağının özetini bu değere bağlıyor; ikisinden biri
/// değişince kırmızı yanar ve artırma kararını bilinçli verdirir.</para>
/// </summary>
public static class KvkkNotice
{
    public const string TextVersion = "kvkk-2026-10-13";
}
