using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Controllers;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.IntakeForm;
using OrderDeck.LicenseServer.Services.IntakeForm.Login;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;

namespace OrderDeck.LicenseServer.Pages.Public;

// Hız sınırı SINIF düzeyinde olmak ZORUNDA: Razor Pages uç nokta üstverisini
// sayfa tipinden okuyor, handler metoduna konan öznitelik hiç uygulanmıyor
// (sessizce etkisiz kalıyordu). Politika kendi içinde POST/GET ayrımı yapıyor —
// bkz. Program.cs "intake-form-submit".
[EnableRateLimiting("intake-form-submit")]
public class IntakeFormModel : PageModel
{
    private readonly IntakeFormService _service;
    private readonly WhatsAppLinkBuilder _linkBuilder;
    private readonly ILogger<IntakeFormModel> _log;
    private readonly IYouTubeChannelResolver _youTube;
    private readonly IntakeLinkStore _linkStore;
    private readonly IOptions<IntakeLoginOptions> _loginOptions;

    public IntakeFormModel(
        IntakeFormService service,
        WhatsAppLinkBuilder linkBuilder,
        ILogger<IntakeFormModel> log,
        IYouTubeChannelResolver youTube,
        IntakeLinkStore linkStore,
        IOptions<IntakeLoginOptions> loginOptions)
    {
        _service = service;
        _linkBuilder = linkBuilder;
        _log = log;
        _youTube = youTube;
        _linkStore = linkStore;
        _loginOptions = loginOptions;
    }

    [BindProperty(SupportsGet = true)]
    public string Slug { get; set; } = "";

    [BindProperty]
    public IntakeFormInput Input { get; set; } = new();

    public IntakeFormConfig? Config { get; private set; }

    // Hatalı gönderimden sonra kanal kartını tekrar çizmek için. Kalıcı değil.
    public string? YouTubeChannelTitle { get; private set; }
    public string? YouTubeChannelThumbnail { get; private set; }

    // Faz 2 — OAuth ile bağlanmış kimlikler. Çerezdeki nonce üzerinden
    // sunucunun KENDİ kaydından okunur; istemciden kimlik kabul edilmez.
    public IntakeLinkedIdentity? LinkedYouTube { get; private set; }
    public IntakeLinkedIdentity? LinkedFacebook { get; private set; }

    // Dönüş banner'ı. Metin SABİT koddan seçilir; query'deki serbest
    // metin ASLA ekrana yazılmaz (XSS).
    public string? LinkBanner { get; private set; }
    public bool LinkBannerIsError { get; private set; }

    // Link yalnız özellik gerçekten çalışır durumdayken çizilir — 404 veren
    // uca görünür link koymak müşteriyi çıkmaza sokar.
    public bool ShowYouTubeLink => _loginOptions.Value.YouTubeLoginReady;
    public bool ShowFacebookLink => _loginOptions.Value.FacebookLoginReady;

    // Kayıt alındıktan sonraki onay ekranını süren iki değer. POST doğrudan
    // WhatsApp'a 302 dönmüyor; kendi sayfasına dönüyor ve geçiş orada JS ile
    // yapılıyor. Sebep: CSP `form-action` yalnız form gönderimlerini denetliyor
    // ve bunu yönlendirme zincirinin HER adımına uyguluyor — `wa.me` kendi de
    // `api.whatsapp.com`'a 302 attığı için form 2026-08-23 ve 08-24'te iki kez
    // sessizce öldü. Sayfa içi `location.href` hiçbir CSP direktifinin kapsamında
    // değil (bunu yapacak `navigate-to` direktifi spec'ten çıkarıldı), yani
    // Meta zincire hop eklese bile bu akış kırılmaz.
    //
    // TempData çerezi Data Protection ile şifreli ve tek kullanımlık: sayfa
    // okununca siliniyor, böylece ad/adres/telefon içeren taslak metin tarayıcıda
    // kalmıyor. Çerez engelliyse zaten anti-forgery de çalışmadığı için form hiç
    // gönderilemiyor — yani bu akış kimseye yeni bir kısıt getirmiyor.
    [TempData] public string? WhatsAppUrl { get; set; }
    [TempData] public bool Submitted { get; set; }

    public sealed class IntakeFormInput
    {
        // Çoklu-platform kullanıcı adları — her biri opsiyonel, en az 1 zorunlu
        // (OnPostSubmitAsync içinde doğrulanır).
        // 200: yapıştırılan profil ADRESİ de bu alandan geçiyor (uzunluk kontrolü
        // model binding'de, parser'dan ÖNCE koşuyor). Kullanıcı adının kendi
        // sınırını HandleValidator uyguluyor — 64 karakter kuralı orada duruyor.
        [StringLength(200, ErrorMessage = "En fazla 200 karakter")]
        public string? YouTubeUsername { get; set; }

        [StringLength(200, ErrorMessage = "En fazla 200 karakter")]
        public string? InstagramUsername { get; set; }

        [StringLength(64, ErrorMessage = "En fazla 64 karakter")]
        public string? FacebookUsername { get; set; }

        [StringLength(200, ErrorMessage = "En fazla 200 karakter")]
        public string? TikTokUsername { get; set; }

        /// <summary>
        /// "Bu benim kanalım" onayı. Kanal bulunduğunda ZORUNLU.
        ///
        /// Tek başına yetmez: bu bayrak "bir kutu işaretlendi" diyor, "HANGİ
        /// kanal onaylandı" demiyor. Bağı <see cref="YouTubeConfirmedChannelId"/>
        /// kuruyor; ikisi birlikte okunur.
        /// </summary>
        public bool YouTubeConfirmed { get; set; }

        /// <summary>
        /// Onayın verildiği kanalın kimliği — müşterinin ekranda GÖRDÜĞÜ kanal.
        /// Sunucu kendi çözdüğü kimlikle karşılaştırır; tutmazsa onay sayılmaz.
        ///
        /// Neden gerekli: JS kapalıyken şu tur mümkün — müşteri A kanalının kartını
        /// görüp kutuyu işaretler, aynı gönderimde kullanıcı adını B yapar. Sunucu
        /// B'yi çözer, <c>Confirmed=true</c> görür ve müşterinin HİÇ GÖRMEDİĞİ
        /// kanalı onaylanmış sayar. Özelliğin tek koruması "gördüğün adı onayla"
        /// bağı; bu alan olmadan o bağ kopuyor.
        ///
        /// Kimlik KAYNAĞI DEĞİL: kayda yazılan değer yine API'nin döndürdüğü
        /// <c>ch.ChannelId</c>. İstemci buraya ne yazarsa yazsın yalnız sunucunun
        /// kendi çözdüğü kimliğe EŞİTSE işe yarıyor — yeni bir güven yüzeyi açmaz.
        ///
        /// Bilerek [StringLength] YOK: uzunluk hatası için ekranda hata kutusu
        /// olmadığından sayfa sessizce geri dönerdi. Uyuşmayan değer zaten
        /// "kanalı onayla" hatasına düşüyor — müşterinin okuyup yapabileceği bir şey.
        /// </summary>
        public string? YouTubeConfirmedChannelId { get; set; }

        [Required(ErrorMessage = "Ad Soyad gerekli")]
        [StringLength(200, ErrorMessage = "En fazla 200 karakter")]
        public string FullName { get; set; } = "";

        // İl ve ilçe ayrı seçilir; kalan (mahalle/cadde/sokak/no) serbest metin.
        // Ayrım e-Fatura toplu yükleme şablonundan geliyor: "Alıcı Şehir",
        // "Alıcı İlçe" ve "Alıcı Sokak" ayrı kolonlar ve entegratör bunları
        // serbest metinden çıkaramıyor. Değerler TurkeyRegions listesine karşı
        // sunucuda da doğrulanır (JS kapalıysa da).
        [Required(ErrorMessage = "İl seçin")]
        public string City { get; set; } = "";

        [Required(ErrorMessage = "İlçe seçin")]
        public string District { get; set; } = "";

        [Required(ErrorMessage = "Adres gerekli")]
        [StringLength(500, ErrorMessage = "En fazla 500 karakter")]
        public string Address { get; set; } = "";

        // Biçim doğrulaması OnPostSubmitAsync içinde EmailValidator ile yapılır:
        // [EmailAddress] gerçek veride 500 kaydın sıfırını reddediyor ve alan adı
        // yazım hatalarını ("gmail.con") hiç görmüyor.
        [Required(ErrorMessage = "E-posta gerekli")]
        [StringLength(200, ErrorMessage = "En fazla 200 karakter")]
        public string Email { get; set; } = "";

        // Opsiyonel — fatura için. Doğrulaması OnPostSubmitAsync içinde
        // TcknValidator ile yapılır: ^\d{11}$ tek başına yetmiyor, gerçek veride
        // 162 numaranın 9'u bu kalıptan geçtiği hâlde kontrol basamağı tutmuyor.
        public string? Tckn { get; set; }

        [Required(ErrorMessage = "WhatsApp numarası zorunlu.")]
        [StringLength(20)]
        public string Phone { get; set; } = "";

        // Mesaj izinleri (onay kutuları)
        public bool WhatsAppConsent { get; set; }
        public bool SmsConsent { get; set; }
    }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        Config = await _service.GetActiveBySlugAsync(Slug, ct);
        if (Config is null) return StatusCode(StatusCodes.Status410Gone);
        LoadLinkedIdentities();

        // "ok" yalnız kimlik GERÇEKTEN varsa çizilir: kod elle URL'e yazılmış
        // ya da kimliğin 30 dakikası dolmuş olabilir.
        (LinkBanner, LinkBannerIsError) = Request.Query["baglanti"].ToString() switch
        {
            "ok" when LinkedYouTube is not null || LinkedFacebook is not null
                => ("Hesabın bağlandı. Kalan bilgileri doldurup formu gönder.", false),
            "iptal" => ("Bağlantı iptal edildi. İstersen kullanıcı adını elle yazabilirsin.", true),
            "kanalyok" => ("Bu Google hesabında YouTube kanalı yok. Kanalın olan hesabı seç.", true),
            "saglayici" => ("Bağlantı sırasında bir sorun oldu. Tekrar dene ya da kullanıcı adını elle yaz.", true),
            _ => (null, false)
        };
        return Page();
    }

    public async Task<IActionResult> OnPostSubmitAsync(CancellationToken ct)
    {
        // Honeypot — bot doldurursa silent 200, persist YOK, redirect YOK
        if (!string.IsNullOrEmpty(Request.Form["website"]))
        {
            _log.LogInformation("Honeypot triggered for slug {Slug}", Slug);
            Config = await _service.GetActiveBySlugAsync(Slug, ct);
            if (Config is null) return StatusCode(StatusCodes.Status410Gone);
            return Page();
        }

        Config = await _service.GetActiveBySlugAsync(Slug, ct);
        if (Config is null) return StatusCode(StatusCodes.Status410Gone);

        LoadLinkedIdentities();

        // ChannelId'siz YouTube kimliğini bağlı sayma: Facebook kimliklerinde
        // ChannelId null olur ve gelecekte bir OAuth hatası da boş bırakabilir.
        // Boşsa bağlı gibi davranmak çözücüyü atlatır ve null channelId kaydeder.
        // Kimliği burada sıfırlamak invariant'ı tek yerde tutuyor; aşağıdaki
        // resolve ternarysi, at-least-one ve linked-YT bloğu tutarlı kalır.
        if (LinkedYouTube is not null && string.IsNullOrEmpty(LinkedYouTube.ChannelId))
        {
            _log.LogWarning(
                "Bağlı YouTube kimliğinde ChannelId boş, bağlı sayılmıyor — Handle={Handle}",
                LinkedYouTube.Handle);
            LinkedYouTube = null;
        }

        // Her platform için: adres→kullanıcı adı çevirisi, çeviri hatası,
        // normalize, kural doğrulaması — dört adım tek metodda, dört kutu
        // aynı sırayı izliyor. Facebook bilerek dahil: parser kısa devre
        // yapıp girdiyi olduğu gibi geçiriyor, davranış değişmez.
        var (yt, channelIdFromUrl) = LinkedYouTube is null
            ? Resolve("Input.YouTubeUsername", HandleValidator.YouTube, Input.YouTubeUsername)
            : (null, null);
        var (ig, _) = Resolve("Input.InstagramUsername", HandleValidator.Instagram, Input.InstagramUsername);
        var (fb, _) = LinkedFacebook is null
            ? Resolve("Input.FacebookUsername", HandleValidator.Facebook, Input.FacebookUsername)
            : (null, null);
        var (tt, _) = Resolve("Input.TikTokUsername", HandleValidator.TikTok, Input.TikTokUsername);

        // Facebook: OAuth'tan gelen GÖRÜNEN ad. HandleValidator BYPASS bilinçli —
        // görünen ad boşluk/Türkçe karakter içerir ve chat satırı da görünen adla
        // düştüğü için eşleşme tam bu değer üzerinden.
        if (LinkedFacebook is not null)
        {
            var fbName = LinkedFacebook.DisplayName.Trim();
            fb = fbName.Length > 64 ? fbName[..64] : fbName;
        }

        // E-posta: dış boşluk + alan adı normalize edilir, sonra biçim ve
        // yaygın alan adı yazım hatası kontrolü. Hatalıysa kayıt oluşmaz.
        Input.Email = EmailValidator.Normalize(Input.Email) ?? "";
        var emailError = EmailValidator.Validate(Input.Email);
        if (emailError is not null)
            ModelState.AddModelError("Input.Email", emailError);

        // TCKN opsiyonel ama girildiyse resmî kontrol basamağından geçmeli —
        // hatalı numara e-Fatura sayfasında entegratörde reddedilir.
        Input.Tckn = TcknValidator.Normalize(Input.Tckn);
        var tcknError = TcknValidator.Validate(Input.Tckn);
        if (tcknError is not null)
            ModelState.AddModelError("Input.Tckn", tcknError);

        // İl/ilçe listeden gelmeli — elle uydurulan değer faturada reddedilir.
        var matchedCity = TurkeyRegions.MatchCity(Input.City);
        if (matchedCity is null)
        {
            if (!string.IsNullOrWhiteSpace(Input.City))
                ModelState.AddModelError("Input.City", "Listeden bir il seçin.");
        }
        else
        {
            Input.City = matchedCity;
            var matchedDistrict = TurkeyRegions.MatchDistrict(matchedCity, Input.District);
            if (matchedDistrict is null)
            {
                if (!string.IsNullOrWhiteSpace(Input.District))
                    ModelState.AddModelError("Input.District", $"{matchedCity} iline ait bir ilçe seçin.");
            }
            else
            {
                Input.District = matchedDistrict;
            }
        }

        // En az bir platform kullanıcı adı zorunlu.
        if (LinkedYouTube is null && LinkedFacebook is null &&
            yt is null && channelIdFromUrl is null && ig is null && fb is null && tt is null)
            ModelState.AddModelError("Input.InstagramUsername",
                "En az bir platform kullanıcı adı girin (Instagram, YouTube, Facebook veya TikTok).");

        // YouTube kimliği: sunucu KENDİSİ çözer — hem @handle hem channel/UC… yolunda.
        // İstemciden channelId kabul edilmiyor; JS'i atlayan bir istek kaydı istediği
        // kimliğe bağlardı. Kanal bulunduğunda onay zorunlu: "test1234" yerine "test"
        // yazan müşteri için doğrulama yeşil ✓ verir (test gerçek bir yabancının kanalı)
        // ve kayıt yabancıya bağlanır; kartta gördüğü adı onaylatmak bunu yakalayan tek şey.
        // Adres yolu da aynı kapıdan geçer: yanlış kanalın sayfasından kopyalayan
        // müşteriyi başka hiçbir şey yakalamıyor.
        string? resolvedChannelId = null;
        var fromUrl = channelIdFromUrl is not null;
        YouTubeChannel? ch = null;

        if (LinkedYouTube is not null)
        {
            resolvedChannelId = LinkedYouTube.ChannelId;
            // Handle aynı normalize/doğrulama kapısından geçer (mevcut customUrl
            // kuralıyla bire bir); geçemezse sessizce boş kalır.
            var linkedHandle = HandleValidator.Normalize(LinkedYouTube.Handle);
            if (HandleValidator.Validate(HandleValidator.YouTube, linkedHandle) is null)
                yt = linkedHandle;
        }
        else if (fromUrl)
            ch = await _youTube.ResolveChannelIdAsync(channelIdFromUrl, ct);
        else if (yt is not null && HandleValidator.Validate(HandleValidator.YouTube, yt) is null)
            ch = await _youTube.ResolveHandleAsync(yt, ct);

        if (ch is not null)
        {
            YouTubeChannelTitle = ch.Title;
            YouTubeChannelThumbnail = ch.Thumbnail;

            if (!ch.Available)
            {
                // Kota/ağ arızası bizim sorunumuz; müşteriyi kilitlemiyoruz.
                // channelIdFromUrl doluysa onu koruyoruz: adresten gelen kimlik
                // yapısal olarak geçerli, elimizdeki tek sağlam veriyi atmayalım.
                // Handle yolunda bu değer zaten null.
                //
                // Ama bu, özelliğin tek bilinçli deliği: kayıt DOĞRULANMADAN açılıyor
                // ve kimse onaylamıyor. İz bırakmazsak kaç kaydın bu delikten geçtiğini
                // ölçemeyiz — kota yükseltmesi mi, önbellek mi, yoksa hiç sorun mu var,
                // ancak bu satır söyleyebilir.
                _log.LogWarning(
                    "YouTube doğrulanamadı, kimlik onaysız kabul edildi — girdi={Girdi}, adresten={Adresten}",
                    channelIdFromUrl ?? yt, fromUrl);
                resolvedChannelId = channelIdFromUrl;
            }
            else if (!ch.Exists)
            {
                ModelState.AddModelError("Input.YouTubeUsername", fromUrl
                    ? "Bu adresteki YouTube kanalını bulamadık. Kanal sayfanı aç, adres çubuğundakini yapıştır."
                    : "Bu kullanıcı adına ait bir YouTube kanalı bulunamadı. Kanal sayfanı aç, "
                      + "adres çubuğundaki @ ile başlayan adresi yapıştır.");
            }
            else if (ch.ChannelId is null)
            {
                // Kanal var ama API kimlik döndürmedi (beklenmedik gövde). Onaylatacak
                // bir kimlik yok; müşteriyi kilitlemek yerine elimizdekiyle devam ediyoruz
                // ama sessiz kalmıyoruz — bu bizim tarafımızda bir arıza.
                _log.LogWarning("YouTube kanalı bulundu ama kimlik gelmedi — girdi={Girdi}", channelIdFromUrl ?? yt);
                resolvedChannelId = channelIdFromUrl;
            }
            // Onay, ONAYLANAN KANALA bağlı olmalı. Kutunun kendisi yalnız "bir kutu
            // işaretlendi" diyor. JS açıkken kullanıcı adına dokunulunca kutu
            // sıfırlanıyor, ama JS kapalıyken şu tur mümkün: müşteri A kanalının
            // kartını görüp kutuyu işaretler ve AYNI gönderimde kullanıcı adını B
            // yapar — sunucu B'yi çözer, Confirmed=true görür ve müşterinin hiç
            // görmediği kanalı onaylanmış sayar. Karşılaştırma bunu kapatıyor.
            // Ordinal: kanal kimlikleri büyük/küçük harf duyarlı.
            else if (!Input.YouTubeConfirmed ||
                     !string.Equals(Input.YouTubeConfirmedChannelId, ch.ChannelId, StringComparison.Ordinal))
            {
                // Sayfa yeniden çizilirken ÇÖZÜLEN kanalın kartı görünecek; onay da
                // o kanal için yeniden istenmeli. Tag helper postalanan değeri
                // modeldekine tercih ettiği için ModelState girdilerini temizlemek
                // şart: yoksa kutu işaretli, gizli alan eski kimlikle gelir ve
                // müşteri görmediği kanalı tek tıkla onaylamış olur.
                ModelState.Remove("Input.YouTubeConfirmed");
                ModelState.Remove("Input.YouTubeConfirmedChannelId");
                Input.YouTubeConfirmed = false;
                Input.YouTubeConfirmedChannelId = ch.ChannelId;

                ModelState.AddModelError("Input.YouTubeConfirmed",
                    ch.Title is { Length: > 0 }
                        ? $"\"{ch.Title}\" kanalının sana ait olduğunu onayla."
                        : "Bulunan kanalın sana ait olduğunu onayla.");
            }
            else
            {
                resolvedChannelId = ch.ChannelId;
            }
        }

        // Kanal adresi yapıştıran müşteri kullanıcı adı yazmıyor: yt null kalıyor
        // ve kayıt yalnız channelId ile açılıyor. WPF sync'i handle'ı DisplayName
        // olarak taşıyor (IntakeFormSyncService), taşıyacak handle yoksa yayıncı
        // müşteri listesinde çıplak "UCabc…" görüyor. API'nin AYNI yanıtta
        // döndürdüğü customUrl bunu ek kota harcamadan dolduruyor.
        //
        // Google'dan geldi diye doğrudan yazmıyoruz: girdi kutusuyla aynı normalize
        // ve doğrulama kapısından geçiyor. Geçemezse SESSİZCE boş kalıyor — burada
        // hata üretmek, kendi verimiz yüzünden müşteriyi engellemek olurdu.
        if (yt is null && ch is { Handle: not null })
        {
            var apiHandle = HandleValidator.Normalize(ch.Handle);
            if (HandleValidator.Validate(HandleValidator.YouTube, apiHandle) is null)
                yt = apiHandle;
        }

        if (!ModelState.IsValid) return Page();

        // Phase 4g — normalize TR phone to E.164
        var normalizedPhone = PhoneNormalizer.NormalizeTr(Input.Phone);
        if (normalizedPhone is null)
        {
            ModelState.AddModelError(
                "Input.Phone",
                "Geçersiz telefon numarası. 10 haneli TR mobil numara girin.");
            return Page();
        }

        // Eski WPF sync'i için legacy Username = ilk dolu platform adı.
        var legacyUsername = yt ?? ig ?? fb ?? tt ?? channelIdFromUrl ?? resolvedChannelId ?? "";

        await _service.SaveSubmissionAsync(
            Config.Id,
            youTubeUsername: yt, instagramUsername: ig,
            facebookUsername: fb, tikTokUsername: tt,
            legacyUsername: legacyUsername,
            fullName: Input.FullName.Trim(),
            address: Input.Address.Trim(),
            phone: normalizedPhone,
            email: Input.Email.Trim(),
            tckn: Trim(Input.Tckn),
            whatsAppConsent: Input.WhatsAppConsent,
            smsConsent: Input.SmsConsent,
            ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString(),
            userAgent: Request.Headers.UserAgent.ToString(),
            ct: ct,
            youTubeChannelId: resolvedChannelId,
            city: Input.City,
            district: Input.District);

        // Kimlik tek gönderimlik: bırakılsaydı aynı tarayıcıdan ikinci kayıt
        // (örn. aile üyesi) öncekinin kanalıyla açılırdı.
        var linkNonce = Request.Cookies[IntakeLinkController.CookieName];
        if (!string.IsNullOrEmpty(linkNonce))
        {
            _linkStore.RemoveIdentity(linkNonce, "youtube");
            _linkStore.RemoveIdentity(linkNonce, "facebook");
        }

        // Yayıncının numarası tanımsızsa link kurmuyoruz: `wa.me/?text=...`
        // WhatsApp'ta hiçbir sohbet açmıyor, müşteri de kaydının geçtiğini
        // göremiyor. Onay ekranı bu durumda linksiz gösteriliyor.
        if (WhatsAppLinkBuilder.HasUsablePhone(Config.WhatsAppPhone))
        {
            WhatsAppUrl = _linkBuilder.Build(
                Config.WhatsAppPhone,
                yt, ig, fb, tt,
                Input.FullName.Trim(),
                Input.Address.Trim(),
                normalizedPhone,
                Input.City,
                Input.District);
        }
        else
        {
            WhatsAppUrl = null;
            _log.LogWarning(
                "Intake config {Slug} has no usable WhatsApp phone; confirmation shown without link",
                Slug);
        }

        Submitted = true;

        // POST-Redirect-GET: müşteri F5'e bastığında kayıt tekrarlanmasın.
        // Hedef, formun açıldığı yolun kendisi — sayfanın iki route'u var
        // (/musteri-kayit/{slug} ve eski /r/{slug}) ve müşteriyi geldiği
        // adreste tutuyoruz.
        return LocalRedirect(Request.Path.Value ?? $"/musteri-kayit/{Slug}");
    }

    /// <summary>
    /// Bir platform kutusunun tam boru hattı: adres→kullanıcı adı çevirisi,
    /// çeviri hatası, normalize, kural doğrulaması. Sıra kritik — doğrulama
    /// çeviriden ÖNCE koşarsa yapıştırılan adres reddedilir. Dört kutunun da
    /// aynı sırayı izlemesi için tek yerde.
    ///
    /// Dönen ikilinin ikinci elemanı yalnız <c>youtube.com/channel/UC…</c>
    /// adresinde dolu: o adres handle değil, doğrudan kanal kimliği verir.
    /// İkisi aynı anda dolu olmaz.
    /// </summary>
    private (string? Handle, string? ChannelId) Resolve(string key, string platform, string? raw)
    {
        var parsed = ProfileUrlParser.Parse(platform, raw);
        if (parsed.Kind == ProfileInputKind.Error)
        {
            ModelState.AddModelError(key, parsed.Error!);
            return (null, null);
        }

        var channelId = parsed.Kind == ProfileInputKind.YouTubeChannelId ? parsed.Value : null;
        var handle = HandleValidator.Normalize(parsed.Kind == ProfileInputKind.Handle ? parsed.Value : null);
        var error = HandleValidator.Validate(platform, handle);
        if (error is not null) ModelState.AddModelError(key, error);
        return (handle, channelId);
    }

    /// <summary>
    /// Unlink de bu sayfaya POST'lanır (ayrı controller'a değil): anti-forgery
    /// token'ı zaten formda ve dönüş adresi Request.Path ile geldiği route'a
    /// (/musteri-kayit/{slug} veya eski /r/{slug}) gider.
    /// </summary>
    public async Task<IActionResult> OnPostUnlinkAsync(string platform, CancellationToken ct)
    {
        Config = await _service.GetActiveBySlugAsync(Slug, ct);
        if (Config is null) return StatusCode(StatusCodes.Status410Gone);

        var nonce = Request.Cookies[IntakeLinkController.CookieName];
        if (!string.IsNullOrEmpty(nonce) && platform is "youtube" or "facebook")
            _linkStore.RemoveIdentity(nonce, platform);
        return LocalRedirect(Request.Path.Value ?? $"/musteri-kayit/{Slug}");
    }

    private void LoadLinkedIdentities()
    {
        var nonce = Request.Cookies[IntakeLinkController.CookieName];
        if (string.IsNullOrEmpty(nonce)) return;
        LinkedYouTube = _linkStore.GetIdentity(nonce, "youtube");
        LinkedFacebook = _linkStore.GetIdentity(nonce, "facebook");
    }

    /// <summary>Trims and normalizes empty/whitespace input to null.</summary>
    private static string? Trim(string? s)
        => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
