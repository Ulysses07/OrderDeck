using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using OrderDeck.Core.Storage.Repositories;

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

            case PaymentRequestResult.ListStale:
                // U12 (Bölüm C): liste açıkken yerel taşıma müşteriyi başka kayda geçirdi;
                // hiçbir şey gönderilmedi, çağıran listeyi yeniledi.
                dialogs.ShowInfo(
                    "Müşteri listesi bilgisayarlar arası birleştirme nedeniyle güncellendi — lütfen tekrar seçin.");
                break;

            case PaymentRequestResult.PaymentInProgress:
                // Aynı kişi için öbür pencerede bir akış sürüyor (ör. Cloud API gönderimi) ve
                // sınırlı bekleme doldu; hiçbir şey gönderilmedi.
                dialogs.ShowInfo(
                    "Bu müşteri için başka bir pencerede ödeme isteği sürüyor — biraz sonra tekrar deneyin.");
                break;

            // AlreadyRequested / MergeRequired soru olarak RequestConfirmedAsync'te sorulur; buraya
            // gelmeleri operatörün "Hayır" dediği anlamına gelir — hiçbir şey yapılmadı, bildirim yok.

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

    /// <summary>
    /// Ödeme isteğini, servisin sorularını (sahip kararları 2 ve 4) operatöre sorarak yürütür:
    /// <see cref="PaymentRequestResult.AlreadyRequested"/> / <see cref="PaymentRequestResult.MergeRequired"/>
    /// gelirse metin gösterilir; "Evet" → aynı istek onay belirteciyle yeniden çağrılır (PhoneRequired
    /// deseni — kira diyalog boyunca tutulmaz, servis belirteçle durumun değişmediğini doğrular).
    /// "Hayır" → hiçbir şey değişmez, soru sonucu döner (<see cref="Notify"/> sessiz geçer).
    /// İki çağrı yeri (müşteri arama, yayın raporu) aynı yoldan geçer (R4-07 gerekçesi).
    /// </summary>
    public static async Task<PaymentRequestOutcome> RequestConfirmedAsync(
        IDialogService dialogs, Func<IReadOnlySet<string>?, Task<PaymentRequestOutcome>> request)
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        var outcome = await request(null);
        // Onay ile yeniden çağrı arasında durum değişirse (öbür pencere gönderdi) yeni durum yeniden
        // sorulur; sınır sonsuz döngüye karşı.
        for (var round = 0; round < MaxPromptRounds && outcome.Prompt is { } prompt; round++)
        {
            var (title, message) = PromptText(prompt, DateTimeOffset.Now);
            if (!await dialogs.ConfirmAsync(message, title)) return outcome;
            tokens.Add(prompt.Token);
            outcome = await request(tokens);
        }
        return outcome;
    }

    private const int MaxPromptRounds = 3;

    private static readonly CultureInfo Tr = CultureInfo.GetCultureInfo("tr-TR");

    /// <summary>Sorunun başlığı ve metni (sahip kararlarındaki metinler).</summary>
    public static (string Title, string Message) PromptText(PaymentPrompt prompt, DateTimeOffset now)
    {
        if (prompt.Kind == PersonScopePromptKind.Resend)
        {
            var last = prompt.Requests.LastOrDefault();
            var at = last?.At is { } t ? WhenText(t, now) + " " : "";
            return ("Ödeme isteği zaten gönderildi",
                $"{at}{Money(last?.Amount ?? 0m)} istendi, tekrar gönderilsin mi?");
        }

        var scope = prompt.ScopeKey.StartsWith("session:", StringComparison.Ordinal)
            ? "bu yayın için"
            : "genel toplam için";
        var amounts = string.Join(", ", prompt.Requests.Select(r => Money(r.Amount)));
        return ("Birden çok ödeme isteği",
            $"Bu kişiye {scope} {prompt.Requests.Count} istek gitmiş ({amounts}); tek istekte birleştirilsin mi? " +
            "Fazladan düşülen bakiye iade edilir.");
    }

    private static string Money(decimal amount) => amount.ToString("#,##0.##", Tr) + " ₺";

    private static string WhenText(DateTimeOffset at, DateTimeOffset now)
    {
        var local = at.ToLocalTime();
        return local.Date == now.ToLocalTime().Date
            ? local.ToString("HH:mm", Tr)
            : local.ToString("dd.MM.yyyy HH:mm", Tr);
    }
}
