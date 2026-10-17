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

    /// <summary><c>add-backup</c> uygulandı ama yanıtın satırı bu bilgisayara yazılamadı (akışa kaldı): yedek sunucuda
    /// var, kuyruğa satış akışıyla iner.</summary>
    public const string BackupAddedRemotely = "Yedek sunucuda eklendi; bu bilgisayara satış akışıyla inecek.";

    /// <summary>
    /// Dekont kaydedildi ama kargo adımı yapılamadı, sunucuya ulaşılamadığı için (§5.8 "Dekont kaydı"): çevrimdışı,
    /// komut ucu yok, sunucu hatası ya da kip henüz hazır değil. Bir sonraki dekont bütün bağlanmamış etiketleri alır.
    /// </summary>
    public const string DekontShipmentDeferred = "Dekont kaydedildi; kargo adımı bağlantı gelince bir sonraki dekontta yapılır.";

    /// <summary>Dekont kaydedildi ama sunucu kargo adımını reddetti (güncelleme gerekli, küme çok büyük, araya başka
    /// bilgisayarın değişikliği girdi, …): "Dekont kaydedildi; kargo adımı yapılamadı: …".</summary>
    public static string DekontShipmentFailed(string reason) =>
        "Dekont kaydedildi; kargo adımı yapılamadı: " + Sentence(reason);

    /// <summary>Eşik çekmecesinin ya da dekont yönergesinin kargo kararı sunucuda uygulanamadı (§5.3): "Kargo kararı
    /// uygulanamadı: …". Dosyanın güncel hâli yerel aynaya yazıldı.</summary>
    public static string ShipmentDecisionFailed(string reason) => "Kargo kararı uygulanamadı: " + Sentence(reason);

    private static string Sentence(string text)
    {
        var t = text.Trim();
        return t.EndsWith('.') || t.EndsWith('!') || t.EndsWith('?') ? t : t + ".";
    }

    /// <summary>Yedek seçiminin satır içi şeridi (§5.3 "Yedek seçimi"): "Yedek eklenemedi: …".</summary>
    public static string BackupFailed(string problem)
    {
        var text = problem.Trim();
        return "Yedek eklenemedi: " + (text.EndsWith('.') || text.EndsWith('!') || text.EndsWith('?') ? text : text + ".");
    }

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

    // ── uzak yayın çipleri, force-end ve devir (§5.7; W2-09) ─────────────────────────────────────────────

    /// <summary>Çipin eylemi (§5.7): yalnız canlı ve sahibi eşik kadar sessiz yayında.</summary>
    public const string ForceEndAction = "Yayını bitir…";

    /// <summary>Kendi yayını devrediliyor (§5.7 "durum satırı").</summary>
    public const string HandoverInProgressStatus = "Yayın sunucuya devrediliyor…";

    // ── mod On açılışı (§8.4; W2-10) ────────────────────────────────────────────────────────────────────

    /// <summary>Durum satırı (§8.4-1): sunucu modu On, yerel kip bir giden kuyruk boşalmadığı için henüz açılmadı.</summary>
    public const string OpeningPending = "Senkron açılıyor; bekleyen gönderim var";

    /// <summary><see cref="OpeningPending"/>'in ipucu.</summary>
    public const string OpeningPendingAdvice =
        "Bu bilgisayarın gönderilmemiş kayıtları sunucuya gidince satış senkronu kendiliğinden açılır; geçmezse " +
        "internet bağlantısını kontrol et.";

    /// <summary>İlk yetişmenin şeridi (§5.4, §8.4-4): bu bilgisayarın kendi etiketlerinde sunucunun değeri alındı.
    /// Açılış başına bir kez; yalnız sayı.</summary>
    public static string CatchUpOverwritten(int count) =>
        $"{count.ToString(System.Globalization.CultureInfo.InvariantCulture)} etikette sunucudaki değer bu " +
        "bilgisayardakinden farklıydı; sunucunun değeri alındı.";

    /// <summary>Başka bilgisayarın canlı yayını (§5.7): "PC-A'da canlı · son sinyal 21:14". Son sinyal bilinmiyorsa
    /// saat yazılmaz.</summary>
    public static string RemoteLiveChip(string? machine, DateTimeOffset? ownerLastSeenAt, TimeZoneInfo? zone = null) =>
        $"{Where(machine)} canlı{Signal(ownerLastSeenAt, zone)}";

    /// <summary>Başka bilgisayarda devrediliyor (§5.7, Y8): "PC-A'da yayın bitti, N etiket gönderilmeyi bekliyor · son
    /// sinyal SS:DD". Sayı bilinmiyorsa "etiketleri".</summary>
    public static string RemoteHandingOverChip(string? machine, int? unsentLabelCount, DateTimeOffset? ownerLastSeenAt,
        TimeZoneInfo? zone = null)
    {
        var pending = unsentLabelCount is { } n
            ? $"{n.ToString(CultureInfo.InvariantCulture)} etiket gönderilmeyi bekliyor"
            : "etiketleri gönderilmeyi bekliyor";
        return $"{Where(machine)} yayın bitti, {pending}{Signal(ownerLastSeenAt, zone)}";
    }

    /// <summary>Devrediliyor çipinin, sahip eşik kadar sessizken ek satırı (§5.7): "PC-A açılıp internete bağlanınca
    /// yayın buradan düzenlenebilir olur." Eylem değil, bilgi (Y8).</summary>
    public static string RemoteHandingOverSilent(string? machine) =>
        $"{Who(machine)} açılıp internete bağlanınca yayın buradan düzenlenebilir olur.";

    /// <summary>"Yayını bitir…" onay metni (§5.7), çipteki sessizlik dakikası ve makine adıyla: "PC-A 17 dakikadır
    /// sessiz. Yayın gerçekten bitti mi? …"</summary>
    public static string ForceEndConfirm(string? machine, int silentMinutes)
    {
        var who = Who(machine);
        return $"{who} {silentMinutes.ToString(CultureInfo.InvariantCulture)} dakikadır sessiz. Yayın gerçekten bitti mi? "
             + "Bitirirsen yayın buradan düzenlenebilir olur. "
             + $"{who} aslında çalışıyorsa, bağlantısı gelince gönderdiği yeni etiketler ve değişiklikler de işlenir; "
             + "aynı etikette burada yaptığın değişiklik önceliklidir.";
    }

    /// <summary>
    /// Kendi yayını uzaktan bitirildi, yayın hâlâ açık (§5.7 MainShell bandı, M18). Bitiren bilgisayarın adı yerelde
    /// yok (sunucu yalnız kopya kimliğini yazar, <c>ForceEndedBy</c>): metin "başka bir bilgisayardan" der.
    /// </summary>
    public static string SessionEndedRemotely(DateTimeOffset releasedAt, TimeZoneInfo? zone = null) =>
        $"Bu yayın başka bir bilgisayardan bitirildi olarak işaretlendi ({Clock(releasedAt, zone)}). "
      + "Yayına devam edebilirsin; bitirdiğinde değişiklikler eşitlenir. "
      + "Diğer bilgisayarın aynı etikette yaptığı değişiklik önceliklidir.";

    /// <summary>Kendi bitmiş yayını devredilemiyor (§5.7 "Akış", §12 risk 4): "Yayın 13 Eki 21:00 devredilemedi: 3
    /// etiket gönderilemiyor". Başlık her yayında aynı olduğu için yayın başlangıç saatiyle anılır.</summary>
    public static string HandoverStuck(long sessionStartedAt, int unsentLabels, TimeZoneInfo? zone = null)
    {
        var started = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeSeconds(sessionStartedAt), zone ?? TimeZoneInfo.Local)
            .ToString("d MMM HH:mm", CultureInfo.GetCultureInfo("tr-TR"));
        return $"Yayın {started} devredilemedi: {unsentLabels.ToString(CultureInfo.InvariantCulture)} etiket gönderilemiyor";
    }

    /// <summary>Devredilemeyen yayının ipucu: ne olduğu ve ne yapılacağı.</summary>
    public const string HandoverStuckAdvice =
        "Bitmiş yayının bazı etiketleri sunucuya gönderilemiyor; yayın bu yüzden diğer bilgisayarlarda düzenlenemiyor. "
      + "Sürerse destekle iletişime geç.";

    /// <summary>Cümle başında makinenin bulunma hâli: "PC-A'da"; ad yoksa "Başka bir bilgisayarda".</summary>
    private static string Where(string? machine) =>
        string.IsNullOrWhiteSpace(machine) ? "Başka bir bilgisayarda" : Locative(machine);

    /// <summary>Cümle başında makine: "PC-A"; ad yoksa "Yayın bilgisayarı".</summary>
    private static string Who(string? machine) => string.IsNullOrWhiteSpace(machine) ? "Yayın bilgisayarı" : machine;

    private static string Signal(DateTimeOffset? at, TimeZoneInfo? zone) =>
        at is { } t ? $" · son sinyal {Clock(t, zone)}" : "";

    /// <summary><c>too-many-candidates</c> (§5.3): "Bu işlem çok fazla etiketi kapsıyor (N); destek ile iletişime
    /// geç."</summary>
    public static string TooManyCandidates(int? count) =>
        count is { } n
            ? $"Bu işlem çok fazla etiketi kapsıyor ({n.ToString(CultureInfo.InvariantCulture)}); destek ile iletişime geç."
            : "Bu işlem çok fazla etiketi kapsıyor; destek ile iletişime geç.";

    /// <summary>
    /// Bekleyen komut (§5.5) sunucuda kalıcı olarak reddedildi; kuyruktan düştü, örtüsü kalktı: "2 etiketin bekleyen
    /// iptali sunucuda işlenemedi: …" / "… bekleyen baskı onayı …". Sebep komutun ret metnidir (§5.3).
    /// </summary>
    public static string PendingRejected(string kind, int count, string? reason)
    {
        var what = kind == global::OrderDeck.Licensing.Api.Models.SalesCommandKinds.MarkPrinted
            ? "bekleyen baskı onayı"
            : "bekleyen iptali";
        var text = (reason ?? Failed).Trim();
        if (!(text.EndsWith('.') || text.EndsWith('!') || text.EndsWith('?'))) text += ".";
        return $"{count.ToString(CultureInfo.InvariantCulture)} etiketin {what} sunucuda işlenemedi: {text}";
    }

    /// <summary>
    /// Mod Off'a dönüşte (§8.5-3) bekleyen komutun kaydı okunamadı; yerel yazıma çevrilemeden kuyruktan düştü, örtüsü
    /// kalktı: "2 etiketin bekleyen iptali bu bilgisayara yazılamadı (iç hata); etiketleri kontrol et."
    /// </summary>
    public static string PendingNotConverted(string kind, int count)
    {
        var what = kind == global::OrderDeck.Licensing.Api.Models.SalesCommandKinds.MarkPrinted
            ? "bekleyen baskı onayı"
            : "bekleyen iptali";
        return $"{count.ToString(CultureInfo.InvariantCulture)} etiketin {what} bu bilgisayara yazılamadı (iç hata); " +
               "etiketleri kontrol et.";
    }

    /// <summary>Basılan kâğıdın sunucu satırı (başka bilgisayarın yayını; bekleyen komuta girmez, §5.5) işaretlenemedi:
    /// "1 etiketin baskısı sunucuya işlenemedi: …".</summary>
    public static string PrintNotRecorded(int count, string? reason)
    {
        var text = (reason ?? Failed).Trim();
        if (!(text.EndsWith('.') || text.EndsWith('!') || text.EndsWith('?'))) text += ".";
        return $"{count.ToString(CultureInfo.InvariantCulture)} etiketin baskısı sunucuya işlenemedi: {text}";
    }

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
