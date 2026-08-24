using System.ComponentModel.DataAnnotations;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.IntakeForm;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;

namespace OrderDeck.LicenseServer.Pages.Public;

public class IntakeFormModel : PageModel
{
    private readonly IntakeFormService _service;
    private readonly WhatsAppLinkBuilder _linkBuilder;
    private readonly ILogger<IntakeFormModel> _log;

    public IntakeFormModel(
        IntakeFormService service,
        WhatsAppLinkBuilder linkBuilder,
        ILogger<IntakeFormModel> log)
    {
        _service = service;
        _linkBuilder = linkBuilder;
        _log = log;
    }

    [BindProperty(SupportsGet = true)]
    public string Slug { get; set; } = "";

    [BindProperty]
    public IntakeFormInput Input { get; set; } = new();

    public IntakeFormConfig? Config { get; private set; }

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
        [StringLength(64, ErrorMessage = "En fazla 64 karakter")]
        public string? YouTubeUsername { get; set; }

        [StringLength(64, ErrorMessage = "En fazla 64 karakter")]
        public string? InstagramUsername { get; set; }

        [StringLength(64, ErrorMessage = "En fazla 64 karakter")]
        public string? FacebookUsername { get; set; }

        [StringLength(64, ErrorMessage = "En fazla 64 karakter")]
        public string? TikTokUsername { get; set; }

        // JS doğrulaması başarılıysa doldurulan gizli alan (channels.list'ten).
        [StringLength(48)]
        public string? YouTubeChannelId { get; set; }

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
        return Page();
    }

    [EnableRateLimiting("intake-form-submit")]
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

        // Kullanıcı adları: baştaki @ + dış boşluk temizlenir, sonra her
        // platformun kendi kurallarına göre doğrulanır. Kurala uymayan kayıt
        // sohbetteki kişiyle eşleşemeyeceği için kabul edilmez.
        var yt = HandleValidator.Normalize(Input.YouTubeUsername);
        var ig = HandleValidator.Normalize(Input.InstagramUsername);
        var fb = HandleValidator.Normalize(Input.FacebookUsername);
        var tt = HandleValidator.Normalize(Input.TikTokUsername);

        // Temizlenmiş hâli forma geri yaz — hata varsa kullanıcı düzelteceği
        // metni görsün, geçerliyse gönderilen değerle kaydedilen aynı olsun.
        Input.YouTubeUsername = yt;
        Input.InstagramUsername = ig;
        Input.FacebookUsername = fb;
        Input.TikTokUsername = tt;

        AddHandleError("Input.YouTubeUsername", HandleValidator.YouTube, yt);
        AddHandleError("Input.InstagramUsername", HandleValidator.Instagram, ig);
        AddHandleError("Input.FacebookUsername", HandleValidator.Facebook, fb);
        AddHandleError("Input.TikTokUsername", HandleValidator.TikTok, tt);

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
        if (yt is null && ig is null && fb is null && tt is null)
        {
            ModelState.AddModelError(
                "Input.InstagramUsername",
                "En az bir platform kullanıcı adı girin (Instagram, YouTube, Facebook veya TikTok).");
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
        var legacyUsername = yt ?? ig ?? fb ?? tt ?? "";

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
            youTubeChannelId: Trim(Input.YouTubeChannelId),
            city: Input.City,
            district: Input.District);

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

    private void AddHandleError(string key, string platform, string? handle)
    {
        var error = HandleValidator.Validate(platform, handle);
        if (error is not null) ModelState.AddModelError(key, error);
    }

    /// <summary>Trims and normalizes empty/whitespace input to null.</summary>
    private static string? Trim(string? s)
        => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
