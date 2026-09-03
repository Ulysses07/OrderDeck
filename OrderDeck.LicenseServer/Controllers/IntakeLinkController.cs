using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Services.Facebook;
using OrderDeck.LicenseServer.Services.IntakeForm;
using OrderDeck.LicenseServer.Services.IntakeForm.Login;

namespace OrderDeck.LicenseServer.Controllers;

/// <summary>
/// Kayıt formu "hesabını bağla" akışı (Faz 2). Anonim uçlar — koruma katmanları:
/// bayrak kontrolü (kapalı özellik 404), slug doğrulaması (aktif form şart),
/// IP hız sınırı, tek kullanımlık state + çerez nonce eşleşmesi.
///
/// Dönüş route'u SABİT (<c>/musteri-kayit/baglanti-donusu</c>) — sağlayıcı
/// panellerine slug'lı joker adres yazılamaz. Sayfa route'u
/// <c>/musteri-kayit/{slug}</c> ile çakışmaz: literal segment parametreyi yener.
/// </summary>
[EnableRateLimiting("intake-link")]
public sealed class IntakeLinkController : ControllerBase
{
    public const string CookieName = "od.link";
    private const string GoogleAuthUrl = "https://accounts.google.com/o/oauth2/v2/auth";
    private const string YouTubeReadonlyScope = "https://www.googleapis.com/auth/youtube.readonly";

    private readonly IntakeFormService _service;
    private readonly IntakeLinkStore _store;
    private readonly IOptions<IntakeLoginOptions> _login;
    private readonly IOptions<FacebookOptions> _facebook;
    private readonly IGoogleChannelClient _google;
    private readonly IFacebookNameClient _fb;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<IntakeLinkController> _log;

    public IntakeLinkController(
        IntakeFormService service,
        IntakeLinkStore store,
        IOptions<IntakeLoginOptions> login,
        IOptions<FacebookOptions> facebook,
        IGoogleChannelClient google,
        IFacebookNameClient fb,
        IWebHostEnvironment env,
        ILogger<IntakeLinkController> log)
    {
        _service = service;
        _store = store;
        _login = login;
        _facebook = facebook;
        _google = google;
        _fb = fb;
        _env = env;
        _log = log;
    }

    [HttpGet("/musteri-kayit/{slug}/baglan/{platform}")]
    public async Task<IActionResult> Start(string slug, string platform, CancellationToken ct)
    {
        var opt = _login.Value;
        var isYouTube = platform == "youtube";
        var isFacebook = platform == "facebook";
        // Bayrak kontrolü DB'den ÖNCE: kapalı özellik slug taramaya alet olmasın.
        if (isYouTube && !opt.YouTubeLoginReady) return NotFound();
        if (isFacebook && (!opt.FacebookEnabled || !_facebook.Value.IsConfigured)) return NotFound();
        if (!isYouTube && !isFacebook) return NotFound();

        var config = await _service.GetActiveBySlugAsync(slug, ct);
        if (config is null) return NotFound();

        // Nonce: state'i TARAYICIYA bağlar. Var olan (ve makul görünen) nonce
        // korunur — müşteri iki platformu peş peşe bağlarken kimliklerin
        // aynı nonce altında birikmesi gerekir.
        var nonce = Request.Cookies[CookieName];
        if (string.IsNullOrEmpty(nonce) || nonce.Length > 128)
            nonce = IntakeLinkStore.RandomToken();

        // Path="/" bilinçli: form eski /r/{slug} route'undan da açılıyor; dar
        // path orada kimliği görünmez yapardı. Çerezde PII yok, yalnız nonce.
        // Secure üretimde Always, testlerde SameAsRequest — AdminCookieSecurePolicy deseni.
        Response.Cookies.Append(CookieName, nonce, new CookieOptions
        {
            HttpOnly = true,
            Secure = _env.IsProduction(),
            SameSite = SameSiteMode.Lax, // Strict olmaz: sağlayıcıdan dönüş cross-site
            Path = "/",
            MaxAge = TimeSpan.FromHours(1)
        });

        var returnPath = "/musteri-kayit/" + Uri.EscapeDataString(slug);
        var state = _store.SaveState(new IntakeLinkState(nonce, slug, platform, returnPath));

        var authUrl = isYouTube
            ? GoogleAuthUrl +
              "?client_id=" + Uri.EscapeDataString(opt.GoogleClientId!) +
              "&redirect_uri=" + Uri.EscapeDataString(opt.RedirectUri) +
              "&response_type=code" +
              "&scope=" + Uri.EscapeDataString(YouTubeReadonlyScope) +
              // Hesap seçtir: yayıncı telefonda çoğu kez birden çok Google
              // hesabına girili; sessizce ilkine bağlamak yanlış kanal demek.
              "&prompt=select_account" +
              "&state=" + Uri.EscapeDataString(state)
            : "https://www.facebook.com/" + _facebook.Value.GraphApiVersion + "/dialog/oauth" +
              "?client_id=" + Uri.EscapeDataString(_facebook.Value.AppId) +
              "&redirect_uri=" + Uri.EscapeDataString(opt.RedirectUri) +
              "&response_type=code" +
              "&scope=public_profile" +
              "&state=" + Uri.EscapeDataString(state);

        return Redirect(authUrl);
    }

    /// <summary>
    /// Sağlayıcıdan dönüş. Sıra bilinçli: önce state (yoksa hiçbir şeye
    /// güvenilmez), sonra nonce (state gerçek ama bu tarayıcının değilse
    /// çalıntıdır), sonra error/code, en son sağlayıcı çağrısı.
    /// Sessiz başarısızlık YOK: her dal ya forma kodla döner ya açık sayfa basar.
    /// </summary>
    [HttpGet("/musteri-kayit/baglanti-donusu")]
    public async Task<IActionResult> Callback(
        string? state, string? code, string? error, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(state) || state.Length > 128)
            return ExpiredPage();

        var st = _store.ConsumeState(state);
        var nonce = Request.Cookies[CookieName];
        if (st is null || string.IsNullOrEmpty(nonce) ||
            !string.Equals(st.CookieNonce, nonce, StringComparison.Ordinal))
        {
            // state süresi dolmuş, tekrar oynatılmış ya da başka tarayyıcıdan
            // geliyor. Slug'ı bilmiyoruz (state'in içindeydi) — forma
            // yönlendiremeyiz, açık bir sayfa basarız.
            return ExpiredPage();
        }

        // İzin reddi (error=access_denied) ya da code'suz dönüş: hata değil,
        // müşteri kararı. Kenar durum tablosu: "hatasız forma dönülür".
        if (!string.IsNullOrEmpty(error) || string.IsNullOrEmpty(code))
            return LocalRedirect(st.ReturnPath + "?baglanti=iptal");

        var result = st.Platform == "youtube"
            ? await _google.FetchChannelAsync(code, ct)
            : await _fb.FetchNameAsync(code, ct);

        if (!result.Ok || result.Identity is null)
        {
            _log.LogWarning("Hesap bağlama başarısız — platform={Platform}, kod={Kod}",
                st.Platform, result.ErrorCode);
            return LocalRedirect(st.ReturnPath + "?baglanti=" + (result.ErrorCode ?? "saglayici"));
        }

        _store.SaveIdentity(nonce, st.Platform, result.Identity);
        return LocalRedirect(st.ReturnPath + "?baglanti=ok");
    }

    /// <summary>Slug'sız çıkmaz sayfası. Razor sayfası değil: bu uca yalnız
    /// bozuk/bayat state ile gelinir, tam sayfa altyapısı kurmaya değmez.</summary>
    private ContentResult ExpiredPage() => Content(
        "<!doctype html><html lang=\"tr\"><head><meta charset=\"utf-8\">" +
        "<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">" +
        "<title>Bağlantının süresi doldu</title></head><body style=\"font-family:sans-serif;" +
        "max-width:28rem;margin:4rem auto;padding:0 1rem;text-align:center\">" +
        "<h1 style=\"font-size:1.2rem\">Bağlantının süresi doldu</h1>" +
        "<p>Kayıt formuna geri dön ve hesabını bağlamayı tekrar dene.</p>" +
        "</body></html>",
        "text/html; charset=utf-8");
}
