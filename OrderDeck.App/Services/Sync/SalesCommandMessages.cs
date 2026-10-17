using System;
using System.Globalization;

namespace OrderDeck.App.Services.Sync;

/// <summary>
/// Satış komutlarının operatöre görünen metinleri (spec 2026-10-13 §5.3 "409'ların ele alınışı", §5.7 arayüz durumları).
/// Tek yer: komut istemcisi (<see cref="SalesCommandClient"/>) sonuca bunları yazar, satır içi şeritler ve araç ipuçları
/// aynen gösterir. Metinde kişisel veri yok: yalnız makine adı, sayı ve yerel saat (SS:DD).
///
/// <para><b>Spec'ten sapmalar:</b> <c>stale</c>'in "Bu arada PC-X'te değişti" metnindeki makine ret'te yok (sunucu
/// yalnız güncel satırları döner); metin sunucunun kendi ifadesiyle "başka bir bilgisayarda" der. Spec'in metin
/// vermediği durumlar (kargoya verilmiş dosya, sunucu hatası) sunucunun ret açıklamasından kuruldu.</para>
/// </summary>
public static class SalesCommandMessages
{
    /// <summary><c>stale</c> ve <c>plan-stale</c> (§5.3, §5.7).</summary>
    public const string Stale = "Bu arada başka bir bilgisayarda değişti; plan yenilendi.";

    /// <summary><c>client-update-required</c> (§5.3); güncelleme sorusu ayrıca açılır.</summary>
    public const string ClientUpdateRequired = "Bu işlem için uygulamayı güncelle.";

    /// <summary><c>content-conflict</c> (§5.3) ve istemci hatası sayılan 400'ler.</summary>
    public const string ContentConflict = "İşlem kaydedilemedi (iç hata); tekrar dene.";

    /// <summary><c>mode-off</c> (§5.3): istemci modu yanlış biliyordu, Off geçişi başlar.</summary>
    public const string ModeOff = "Senkron kapatıldı; ekran yenileniyor.";

    /// <summary>404: komut ucu yok (§5.3, §5.7).</summary>
    public const string EndpointMissing = "Sunucu güncelleniyor; biraz sonra tekrar dene.";

    /// <summary>Çevrimdışı (§5.7 "Devredilmiş yayın, çevrimdışı").</summary>
    public const string Offline = "Bu değişiklik için sunucu bağlantısı gerekli";

    /// <summary>Komut henüz gönderilemiyor: geri yüklemeden sonra kip tazelenmedi ya da kopya kimliği yok (§5.3'ün
    /// "Senkron açılıyor" durumu).</summary>
    public const string NotReady = "Senkron açılıyor; birkaç dakika sonra tekrar dene.";

    /// <summary>Sunucu hatası (5xx) ya da çözümlenemeyen yanıt. Komut uygulanmış olabilir; yeniden deneme aynı
    /// komutu gönderir, sunucu ikinci kez uygulamaz.</summary>
    public const string Failed = "Sunucu işlemi tamamlayamadı; biraz sonra tekrar dene.";

    /// <summary><c>shipment-shipped</c>, etiket fiyatında (K6).</summary>
    public const string ShipmentShippedPrice = "Bu etiket kargoya verilmiş bir dosyada; fiyatı değişmez.";

    /// <summary><c>shipment-shipped</c>, kargo kararında.</summary>
    public const string ShipmentShippedDecision = "Kargo dosyası zaten kargoya verilmiş; karar değişmez.";

    /// <summary>Devir kapısı (§5.2): kendi bitmiş ama devredilmemiş yayınının kapanış gönderimi o an kilidi tutuyor;
    /// yerel düzenleme beklemez, reddedilir.</summary>
    public const string HandoverInProgress = "Yayın sunucuya devrediliyor; birkaç saniye sonra tekrar dene.";

    /// <summary>Çevrimdışı yeniden baskının ekran notu (§5.6): kâğıt önbellekten basıldı, verinin tazeliği son satış
    /// akışı eşitlemesidir. Kâğıda yazılmaz.</summary>
    public static string LastSync(DateTimeOffset? at, TimeZoneInfo? zone = null) =>
        at is { } t ? $"son eşitleme {Clock(t, zone)}" : "son eşitleme bilinmiyor";

    /// <summary><c>session-live</c> (§5.3): "Bu yayın PC-A'da canlı (son sinyal SS:DD)".</summary>
    public static string SessionLive(string? machine, DateTimeOffset? ownerLastSeenAt, TimeZoneInfo? zone = null)
    {
        var where = string.IsNullOrWhiteSpace(machine) ? "başka bir bilgisayarda" : Locative(machine);
        var signal = ownerLastSeenAt is { } t ? $" (son sinyal {Clock(t, zone)})" : "";
        return $"Bu yayın {where} canlı{signal}";
    }

    /// <summary><c>owner-active</c> (force-end, §5.3): "PC-A hâlâ sinyal veriyor (son SS:DD); yayın buradan
    /// bitirilemez."</summary>
    public static string OwnerActive(string? machine, DateTimeOffset? lastSeenAt, TimeZoneInfo? zone = null)
    {
        var who = string.IsNullOrWhiteSpace(machine) ? "Yayın bilgisayarı" : machine;
        var signal = lastSeenAt is { } t ? $" (son {Clock(t, zone)})" : "";
        return $"{who} hâlâ sinyal veriyor{signal}; yayın buradan bitirilemez.";
    }

    /// <summary><c>handing-over</c> (force-end, §5.3): "PC-A'da yayın bitti ama N etiket henüz gönderilmedi. PC-A
    /// açılıp internete bağlanınca yayın buradan düzenlenebilir olur."</summary>
    public static string HandingOver(string? machine, int? unsentLabelCount)
    {
        var known = !string.IsNullOrWhiteSpace(machine);
        var ended = known ? $"{Locative(machine!)} yayın bitti" : "Yayın bitti";
        var pending = unsentLabelCount is { } n
            ? $"ama {n.ToString(CultureInfo.InvariantCulture)} etiket henüz gönderilmedi."
            : "ama etiketleri henüz gönderilmedi.";
        var who = known ? machine : "Yayın bilgisayarı";
        return $"{ended} {pending} {who} açılıp internete bağlanınca yayın buradan düzenlenebilir olur.";
    }

    /// <summary><c>too-many-candidates</c> (§5.3): "Bu işlem çok fazla etiketi kapsıyor (N); destek ile iletişime
    /// geç."</summary>
    public static string TooManyCandidates(int? count) =>
        count is { } n
            ? $"Bu işlem çok fazla etiketi kapsıyor ({n.ToString(CultureInfo.InvariantCulture)}); destek ile iletişime geç."
            : "Bu işlem çok fazla etiketi kapsıyor; destek ile iletişime geç.";

    /// <summary>Yerel saat, SS:DD.</summary>
    public static string Clock(DateTimeOffset at, TimeZoneInfo? zone = null) =>
        TimeZoneInfo.ConvertTime(at, zone ?? TimeZoneInfo.Local).ToString("HH:mm", CultureInfo.InvariantCulture);

    /// <summary>
    /// Makine adının bulunma hâli: "PC-A" → "PC-A'da", "PC" → "PC'de", "KASA" → "KASA'da", "DESKTOP-4F2KJ3" →
    /// "DESKTOP-4F2KJ3'te". Ekin sesini adın son hecesi belirler: sonda sayı varsa okunuşu ("3" üç → te, "10" on → da),
    /// sesli harfi olmayan kısaltma ya da tek harf ise harfin adı ("C" ce → de), değilse son sesli (kalın → a, ince →
    /// e) ve son ünsüzün sertliği (ç f h k p s ş t → t). Bilgisayar adları ASCII yazıldığı için büyük "I" ince sayılır
    /// ("OFIS" ofis → te). Sezgiseldir; amaç makine adının arkasına doğru eki koymaktır.
    /// </summary>
    public static string Locative(string machine)
    {
        var (back, hard) = LastSound(machine);
        return $"{machine}'{(hard ? 't' : 'd')}{(back ? 'a' : 'e')}";
    }

    private static (bool Back, bool Hard) LastSound(string name)
    {
        var end = name.Length - 1;
        while (end >= 0 && !char.IsLetterOrDigit(name[end])) end--;
        if (end < 0) return (false, false);

        if (char.IsDigit(name[end]))
        {
            var start = end;
            while (start > 0 && char.IsDigit(name[start - 1])) start--;
            return NumberSound(name.AsSpan(start, end - start + 1));
        }

        var first = end;
        while (first > 0 && char.IsLetter(name[first - 1])) first--;
        var word = name.AsSpan(first, end - first + 1);
        var lastVowel = '\0';
        foreach (var ch in word)
            if (IsVowel(ch)) lastVowel = ch;
        var last = name[end];
        if (word.Length == 1 || lastVowel == '\0') return LetterSound(last);
        return (IsBackVowel(lastVowel), IsVoiceless(last));
    }

    /// <summary>Harfin adının sesi: sesliler kendisi; ünsüzlerin adı "-e" ile biter (be, ce, ke…), X "iks".</summary>
    private static (bool Back, bool Hard) LetterSound(char letter) =>
        IsVowel(letter) ? (IsBackVowel(letter), false)
        : char.ToUpperInvariant(letter) == 'X' ? (false, true)
        : (false, false);

    /// <summary>Sayının okunuşunun son hecesi.</summary>
    private static (bool Back, bool Hard) NumberSound(ReadOnlySpan<char> digits)
    {
        var lastNonZero = digits.LastIndexOfAnyExcept('0');
        if (lastNonZero < 0) return (true, false);                                  // sıfır
        var zeros = digits.Length - 1 - lastNonZero;
        var d = digits[lastNonZero] - '0';
        return zeros switch
        {
            0 => d switch
            {
                1 or 2 or 7 or 8 => (false, false),                                   // bir, iki, yedi, sekiz
                3 or 4 or 5 => (false, true),                                         // üç, dört, beş
                _ => (true, false),                                                   // altı, dokuz
            },
            1 => d switch
            {
                1 or 3 or 9 => (true, false),                                         // on, otuz, doksan
                4 or 6 => (true, true),                                               // kırk, altmış
                7 => (false, true),                                                   // yetmiş
                _ => (false, false),                                                  // yirmi, elli, seksen
            },
            2 => (false, false),                                                      // yüz
            <= 5 => (false, false),                                                   // bin
            _ => (true, false),                                                       // milyon, milyar
        };
    }

    private static bool IsVowel(char c) => "aeıioöuüAEIİOÖUÜ".Contains(c);

    /// <summary>Kalın sesli: a, ı, o, u. ASCII büyük "I" ince sayılır (bkz. <see cref="Locative"/>).</summary>
    private static bool IsBackVowel(char c) => "aıouAOU".Contains(c);

    private static bool IsVoiceless(char c) => "çfhkpsştÇFHKPSŞT".Contains(c);
}
