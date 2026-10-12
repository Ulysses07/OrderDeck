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

    /// <summary>Sorunun başlığı ve metni (sahip kararlarındaki metinler).
    ///
    /// <para>Birleştirmede yalnız TESLİM edilmiş istekler "gitmiş" sayılır; para taşıyan ama hiç
    /// gönderilmemiş istek ayrıca söylenir (operatöre gitmemiş bir isteği gitmiş diye göstermemek
    /// için). "Fazladan düşülen" denmez: iki ayrı kartken ayrı ürünler için yapılmış düşümler de
    /// birleşir — iade edilen her zaman fazla değildir. İade başka bir satırın bakiyesine dönüyorsa
    /// (bakiye satır başına) bu da söylenir: o tutar bu istekte kullanılmaz. İade edilecek düşüm yoksa
    /// (parasız istekler birleşiyor) iade cümlesi hiç kurulmaz.</para>
    ///
    /// <para>Sunucunun bulduğu ayrı düşümler (<see cref="PaymentPrompt.RemoteDeductions"/>) "gitmiş
    /// istek" değildir — sunucu teslimi bilmez; bulunan düşüm olarak anlatılır.</para>
    ///
    /// <para>Sonucu yazılamamış gönderim (<see cref="PriorPaymentRequest.Unconfirmed"/>) olgu diye
    /// anlatılmaz ("zaten gönderildi", "istendi" denmez): istek gitmemiş de olabilir — operatör az önce
    /// "WhatsApp açılamadı" görmüş olabilir; "Hayır" derse müşteriye hiç istek gitmeyebilir.</para></summary>
    public static (string Title, string Message) PromptText(PaymentPrompt prompt, DateTimeOffset now)
    {
        if (prompt.Kind == PersonScopePromptKind.Resend)
        {
            var last = prompt.Requests.LastOrDefault();
            var at = last?.At is { } t ? WhenText(t, now) + " " : "";
            var atte = last?.At is { } t2 ? WhenText(t2, now) + Locative(t2) + " " : "";
            // K5 (2026-10-12): tutar değiştiyse (ör. fiyat düzeltmesi) eski ve yeni tutar BİRLİKTE —
            // operatör "tekrar gönder"in aynı tutarı mı yoksa düzeltilmiş isteği mi göndereceğini görsün.
            var newText = prompt.NewTotal is { } nt
                ? (prompt.PriorHadBalance
                    ? $"yeni toplam {Money(nt)} (bakiye düşülmeden önce)"
                    : $"yeni tutar {Money(nt)}")
                : null;
            if (last is { Unconfirmed: true })
                return ("Ödeme isteği gönderilmiş olabilir", newText is null
                    ? $"{at}{Money(last.Amount)} tutarındaki istek gönderilmiş olabilir (sonucu kaydedilemedi), " +
                      "tekrar gönderilsin mi?"
                    : $"{at}{Money(last.Amount)} tutarındaki istek gönderilmiş olabilir (sonucu kaydedilemedi); " +
                      $"{newText}. Düzeltilmiş istek gönderilsin mi?");
            return ("Ödeme isteği zaten gönderildi", newText is null
                ? $"{at}{Money(last?.Amount ?? 0m)} istendi, tekrar gönderilsin mi?"
                : $"{atte}{Money(last?.Amount ?? 0m)} istendi; {newText}. Düzeltilmiş istek gönderilsin mi?");
        }

        var scope = prompt.ScopeKey.StartsWith("session:", StringComparison.Ordinal)
            ? "bu yayın için"
            : "genel toplam için";

        if (prompt.RemoteDeductions is { Count: > 0 } remote)
        {
            // Onayda en eski düşüm kalır (sunucu teslim sırasını bilmez), ötekiler iade edilir.
            var found = $"Sunucuda bu kişinin {scope} {remote.Count} ayrı bakiye düşümü bulundu " +
                        $"({string.Join(", ", remote.Select(Money))}); müşteriye gidip gitmedikleri bilinmiyor. " +
                        "Tek istekte birleştirilsin mi? En eski düşüm kalır, ötekiler iade edilir; " +
                        "kişinin toplamı tek istekte yeniden istenir.";
            return ("Birden çok ödeme isteği", WithOtherRowNote(found, prompt));
        }

        var sent = prompt.Requests.Count;
        var unsent = prompt.UnsentDeductions;
        string lead;
        if (sent > 0)
        {
            var amounts = string.Join(", ", prompt.Requests.Select(r =>
                r.Unconfirmed ? Money(r.Amount) + " (gitmiş olabilir)" : Money(r.Amount)));
            lead = $"Bu kişiye {scope} {sent} istek gitmiş ({amounts})";
            if (unsent > 0) lead += $"; ayrıca {unsent} gönderilmemiş bakiye düşümü var";
        }
        else
        {
            lead = $"Bu kişinin {scope} {unsent} gönderilmemiş bakiye düşümü var";
        }

        var message = lead + "; tek istekte birleştirilsin mi? " +
                      (prompt.RefundsDeductions
                          ? "Önceki düşümler iade edilir, kişinin toplamı tek istekte yeniden istenir."
                          : "Kişinin toplamı tek istekte yeniden istenir.");
        return ("Birden çok ödeme isteği", WithOtherRowNote(message, prompt));
    }

    private static string WithOtherRowNote(string message, PaymentPrompt prompt)
        => prompt.RefundsToOtherRow
            ? message + " Not: düşümlerden biri kişinin başka bir hesabının bakiyesinden yapılmış; " +
              "iade o hesaba döner, bu istekte kullanılmaz."
            : message;

    /// <summary>Uygulamanın öbür tutar metinleriyle aynı biçim (rapordaki kişi toplamı, kargo notu):
    /// iki kuruş hanesi + "TL".</summary>
    private static string Money(decimal amount) => amount.ToString("N2", Tr) + " TL";

    /// <summary>Saatin bulunma eki ("21:14'te", "21:30'da", "21:00'de") — saatin okunuşunun son
    /// sözcüğüne göre (dakika sıfırsa saat okunur: "yirmi bir").</summary>
    internal static string Locative(DateTimeOffset at)
    {
        var local = at.ToLocalTime();
        var n = local.Minute != 0 ? local.Minute : local.Hour;
        var word = n % 10 != 0 ? n % 10 : n;   // son okunan sözcük: birler, yoksa onlar
        var suffix = word switch
        {
            1 or 2 or 7 or 8 or 20 or 50 => "de",     // bir, iki, yedi, sekiz, yirmi, elli
            3 or 4 or 5 => "te",                      // üç, dört, beş
            40 => "ta",                               // kırk
            6 or 9 or 10 or 30 or 0 => "da",          // altı, dokuz, on, otuz, sıfır
            _ => "de",
        };
        return "'" + suffix;
    }

    private static string WhenText(DateTimeOffset at, DateTimeOffset now)
    {
        var local = at.ToLocalTime();
        return local.Date == now.ToLocalTime().Date
            ? local.ToString("HH:mm", Tr)
            : local.ToString("dd.MM.yyyy HH:mm", Tr);
    }
}
