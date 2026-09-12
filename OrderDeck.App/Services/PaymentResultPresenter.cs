namespace OrderDeck.App.Services;

/// <summary>
/// Ödeme isteği sonucunu operatöre anlatan TEK yer.
///
/// <para><b>R4-07 (2026-09-12) — neden ortak fonksiyon.</b> İki ViewModel
/// (müşteri arama, yayın raporu) sonucu kendi içinde <c>if/else if</c>
/// zinciriyle ele alıyordu. Zincir yalnız BİRİNCİ çağrıya uygulanıyordu;
/// <c>PhoneRequired</c> sonrası telefon kaydedilip yapılan İKİNCİ çağrının
/// dönüşü hiç okunmuyordu. O çağrı <c>BalanceUncertain</c>, <c>SendPending</c>
/// veya <c>LaunchFailed</c> dönerse ekranda hiçbir şey olmuyordu: telefonunu
/// düzelten operatör neden devam etmediğini göremiyor, "tekrar dene" denmesi
/// gereken yerde sessizlik kalıyordu.</para>
///
/// <para>Kopya zincir yerine tek fonksiyon: her çağrı yolunun aynı bildirimi
/// alması artık yapısal olarak garanti — birini güncelleyip diğerini unutmak
/// mümkün değil.</para>
/// </summary>
public static class PaymentResultPresenter
{
    /// <summary>Sonucu operatöre bildirir. <see cref="PaymentRequestResult.Opened"/>
    /// ve <see cref="PaymentRequestResult.Sent"/> sessizdir — başarı zaten
    /// ekranda görünür (WhatsApp penceresi açılır / mesaj gider).</summary>
    public static void Notify(IDialogService dialogs, PaymentRequestResult result)
    {
        switch (result)
        {
            case PaymentRequestResult.LaunchFailed:
                dialogs.ShowError("WhatsApp açılamadı. WhatsApp Desktop kurulu mu?");
                break;

            case PaymentRequestResult.SendPending:
                // Sunucu "aynı gönderim işleniyor" dedi: mesaj gitmiş de olabilir,
                // hiç gitmemiş de. Sessiz kalırsak operatör gittiğini varsayar;
                // otomatik wa.me açarsak çift mesaj riski var. Kararı ona bırakıyoruz.
                dialogs.ShowInfo(
                    "Gönderim işleniyor — WhatsApp'ta ulaştığını doğrulayın, aksi halde tekrar deneyin.");
                break;

            case PaymentRequestResult.BalanceUncertain:
                // R2-01..03: düşümün sonucu kesinleşmedi, mesaj GÖNDERİLMEDİ.
                // Tekrar deneme aynı anahtarla replay yapar — çift düşüm imkânsız.
                //
                // Hata değil UYARI: kaybedilmiş bir işlem yok ve operatörün
                // yapabileceği somut bir şey var. Kırmızı hata diyaloğu "bir şey
                // bozuldu" izlenimi veriyordu; asıl endişeyi ("bakiye iki kez mi
                // düştü?") metnin sonundaki güvence kapatıyor.
                dialogs.Show(
                    "Sunucudan kesin cevap alınamadı; mesaj gönderilmedi. " +
                    "Bağlantıyı kontrol edip tekrar deneyin — çift düşüm olmaz.",
                    "Bakiye doğrulanamadı",
                    DialogSeverity.Warning);
                break;

            case PaymentRequestResult.PhoneRequired:
                // Buraya yalnız telefon diyaloğundan SONRA gelinir (ilk çağrının
                // PhoneRequired'ı diyaloğu açar, bildirime düşmez). Numara
                // kaydedildiği hâlde hâlâ geçersizse sessiz kalmak, operatörün
                // aynı düğmeye boşuna basmasına yol açardı.
                dialogs.ShowError(
                    "Numara kaydedildi ama geçerli bir WhatsApp numarası olarak " +
                    "okunamadı. Müşteri kartından numarayı kontrol edin.");
                break;
        }
    }
}
