namespace OrderDeck.LicenseServer.Services.IntakeForm;

/// <summary>
/// Kayıt formundaki KVKK aydınlatma metninin sürümü. Her gönderimle birlikte
/// <see cref="Domain.IntakeFormSubmission.KvkkTextVersion"/> olarak saklanır:
/// "bu kişi HANGİ metni okuduğunu onayladı" sorusunun tek cevabı bu değer.
/// Metnin kendisi kodda (<c>Pages/Public/IntakeForm.cshtml</c>, <c>p.kvkk</c>
/// paragrafı + altındaki onay kutusu etiketi); o sürümün tam metni git
/// geçmişinde bu sabitin değiştiği commit'te durur.
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
/// <para><c>IntakeFormKvkkTests.Metin_degisirse_surum_de_degismeli</c> metnin
/// özetini bu değere bağlıyor; metin değişip sürüm değişmezse kırmızı yanar.</para>
/// </summary>
public static class KvkkNotice
{
    public const string TextVersion = "kvkk-2026-10-13";
}
