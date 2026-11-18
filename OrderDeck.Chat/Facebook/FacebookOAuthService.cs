using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OrderDeck.Core.Chat;
using OrderDeck.Core.Settings;

namespace OrderDeck.Chat.Facebook;

/// <summary>
/// Owns the Facebook OAuth 2.0 lifecycle for the desktop app. Mirrors
/// <c>YouTubeOAuthService</c> in shape (Connect / Disconnect / IsConnected /
/// GetConnectedPageName), but the implementation is hand-rolled because
/// Meta doesn't ship a .NET equivalent of <c>GoogleWebAuthorizationBroker</c>
/// — there is no library that spins up the loopback listener for us.
///
/// <para><b>App Secret bu derlemede yoktur.</b> Meta, Google'ın aksine masaüstü
/// sırrını "gizli değil" saymaz; üstelik yayın hattına enjeksiyon adımı hiç
/// eklenmemişti, bu yüzden sabitler sahaya BOŞ gidiyor ve Facebook'a bağlanma
/// her kurulumda patlıyordu. Hem yapılandırmayı hem code→token takasını artık
/// lisans sunucusu veriyor (<see cref="IFacebookOAuthBroker"/>).</para>
///
/// <para>Flow on Connect:</para>
/// <list type="number">
///   <item>Yapılandırmayı (<c>app_id</c>, <c>config_id</c>, <c>redirect_uri</c>)
///     lisans sunucusundan al.</item>
///   <item>Build the authorise URL with <c>client_id</c>, <c>redirect_uri</c>,
///     <c>state</c>, and <c>config_id</c> (Login for Business config).</item>
///   <item>Open the default browser.</item>
///   <item>Listen on <see cref="LoopbackPrefix"/> for the redirect.</item>
///   <item><c>?code=...</c>'u sunucuya ver, uzun ömürlü (~60g) kullanıcı
///     token'ını al — iki adımlı takası sunucu yapar.</item>
///   <item>Hit <c>/me/accounts</c>, ask the caller to pick a Page (or auto-pick
///     if there's only one).</item>
///   <item>Derive the Page access token from <c>/{page-id}?fields=access_token,name</c>.</item>
///   <item>Persist the <see cref="FacebookTokenBundle"/> via DPAPI.</item>
/// </list>
///
/// Single-operator install (same assumption as YouTube).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class FacebookOAuthService
{
    /// <summary>
    /// Loopback callback prefix — the local <see cref="HttpListener"/> that
    /// actually receives the authorization code.
    /// Port 4849 is intentionally distinct from 4747 (overlay) and 4748
    /// (extension bridge) to avoid clashing with running hosted services.
    /// </summary>
    public const string LoopbackPrefix = "http://localhost:4849/facebook/callback/";

    /// <summary>
    /// En çok bu kadar Sayfa bağlanabilir (birincil dahil). NEDEN 4: Instagram
    /// yoklamasının bağlantı havuzu aynı sunucuya en çok 4 bağlantı açar ve her
    /// Sayfanın poller'ı aynı anda tek istek yapar.
    /// </summary>
    public const int MaxInstagramPages = 4;

    /// <summary>
    /// <c>/me/accounts</c> okunurken izlenecek en çok yanıt sayfası (her biri 100
    /// satıra kadar). NEDEN sınır: <c>paging.next</c> bozuk ya da döngülü gelirse
    /// listeleme sonsuza dek sürmesin.
    /// </summary>
    private const int MaxAccountsResponsePages = 10;

    /// <summary>
    /// Tarayıcıdaki onayın en çok bekleneceği süre. NEDEN: kullanıcı onay sekmesini
    /// kapatırsa uygulama bunu algılayamaz; süre olmasaydı bekleme süresiz kalır,
    /// dinleyici açık kaldığı için sonraki deneme de bağlanamazdı. Süre tek yerde,
    /// burada durur; testler kısasını kurucudan verir.
    /// </summary>
    internal static readonly TimeSpan CallbackTimeout = TimeSpan.FromMinutes(5);

    private const string NotConnectedMessage = "Facebook bağlı değil.";

    // Sabit metin: arayüz bunu olduğu gibi gösterir; testlerin kurucudan verdiği
    // kısa süre metne yansımaz.
    private const string CallbackTimeoutMessage =
        "Facebook onayı 5 dakika içinde tamamlanmadı. Yeniden dene.";

    private const string PendingFlowMessage =
        "Bekleyen bir Facebook onayı var. Tarayıcıdaki onayı tamamla ya da " +
        "birkaç dakika sonra yeniden dene.";

    private const string DisconnectedWhileWaitingMessage =
        "Onay beklenirken Facebook bağlantısı kaldırıldı.";

    private static readonly string GraphBase =
        $"https://graph.facebook.com/{FacebookOAuthDefaults.GraphApiVersion}";
    private static readonly string DialogBase =
        $"https://www.facebook.com/{FacebookOAuthDefaults.GraphApiVersion}/dialog/oauth";

    private readonly Func<AppSettings> _settings;
    private readonly EncryptedFacebookTokenStore _tokenStore;
    private readonly IFacebookOAuthBroker _broker;
    private readonly HttpClient _http;
    private readonly ILogger<FacebookOAuthService> _log;
    private readonly IFacebookOAuthBrowser _browser;
    private readonly TimeSpan _callbackTimeout;

    // Aynı anda tek tarayıcı akışı: 0 = boş, 1 = bekleyen akış var (Interlocked).
    // NEDEN: dinleyici tek bir adrese bağlanır; ikinci akış "Failed to bind" ile
    // düşerdi. AcquireUserTokenAsync girişte alır, her çıkış yolunda bırakır.
    // _mutate bu iş için KULLANILMAZ: bekleme dakikalarca sürebilir ve o sırada
    // bağlantıyı kaldırmak ya da ek Sayfa eklemek mümkün kalmalıdır.
    private int _browserFlowPending;

    // Demeti oku-değiştir-yaz yapan bölümler (ek Sayfa ekle / kaldır, ConnectAsync'in
    // ve ReauthorizeAsync'in kaydetme adımı, DisconnectAsync) bu kilit altında
    // çalışır. NEDEN: iki değişiklik aynı eski demetten yola çıkarsa sonuncusu
    // ötekinin yazdığını ezer (kayıp güncelleme). Kilit altında önce diske yazılır,
    // sonra _bundle TEK atamayla değişir: yazım düşerse bellek eski hâlinde kalır.
    // Kilit tarayıcı beklemesini KAPSAMAZ. Okuyan üyeler kilit almaz: demet değişmez
    // bir kayıttır.
    //
    // Tembel yükleme (IsConnectedAsync) de kilitsizdir, ama alanı yalnız BOŞKEN
    // yayımlar: kilit altında yayımlanmış demeti ezemez. Bilinen ve kabul edilen
    // pencere: yüklemesi süren bir okuyucu, o sırada tamamlanan DisconnectAsync'ten
    // sonra eski demeti belleğe geri koyabilir. Ön koşul pratikte süreçteki İLK
    // yüklemenin bitmemiş olmasıdır (bağlantıyı kaldırma düğmesi ancak o yükleme
    // bittikten sonra etkinleşir). Kapatmak tembel yüklemeyi kilide almak demektir
    // ve yeniden giriş riski taşır: değiştiren üyeler demeti kilit altında aynı
    // yoldan okur.
    private readonly SemaphoreSlim _mutate = new(1, 1);

    // Cached after a successful Connect / IsConnected probe; cleared on
    // Disconnect. Mirrors the YouTube service so the settings dialog can
    // render "Bağlı: <Page Name>" without any Graph API hit.
    private FacebookTokenBundle? _bundle;

    public FacebookOAuthService(
        Func<AppSettings> settings,
        EncryptedFacebookTokenStore tokenStore,
        IFacebookOAuthBroker broker,
        HttpClient http,
        ILogger<FacebookOAuthService> log)
        : this(settings, tokenStore, broker, http, log,
            new LoopbackFacebookOAuthBrowser(), CallbackTimeout)
    {
    }

    /// <summary>Test girişi: tarayıcı dikişi ve onay bekleme süresi dışarıdan verilir.</summary>
    internal FacebookOAuthService(
        Func<AppSettings> settings,
        EncryptedFacebookTokenStore tokenStore,
        IFacebookOAuthBroker broker,
        HttpClient http,
        ILogger<FacebookOAuthService> log,
        IFacebookOAuthBrowser browser,
        TimeSpan callbackTimeout)
    {
        _settings = settings;
        _tokenStore = tokenStore;
        _broker = broker;
        _http = http;
        _log = log;
        _browser = browser;
        _callbackTimeout = callbackTimeout;
    }

    /// <summary>True if a Page access token is on disk.</summary>
    public async Task<bool> IsConnectedAsync(CancellationToken ct = default)
    {
        if (_bundle is not null) return true;
        var loaded = await _tokenStore.LoadAsync(ct).ConfigureAwait(false);
        // Yalnız alan hâlâ boşsa yayımla: kilit altında yazılmış taze demeti ezme.
        Interlocked.CompareExchange(ref _bundle, loaded, null);
        return _bundle is not null;
    }

    /// <summary>
    /// Returns the cached Page display name, or null if not connected.
    /// </summary>
    public async Task<string?> GetConnectedPageNameAsync(CancellationToken ct = default)
    {
        if (await IsConnectedAsync(ct).ConfigureAwait(false))
            return _bundle!.PageName;
        return null;
    }

    /// <summary>
    /// Returns the current page access token (and page id), or null if not
    /// connected. Used by <c>FacebookModerationService</c> /
    /// <c>FacebookLiveCommentsPoller</c> as the bearer for Graph API calls.
    /// </summary>
    public async Task<(string PageId, string PageAccessToken)?> GetPageCredentialsAsync(
        CancellationToken ct = default)
    {
        if (!await IsConnectedAsync(ct).ConfigureAwait(false)) return null;
        return (_bundle!.PageId, _bundle.PageAccessToken);
    }

    /// <summary>
    /// Returns the stored long-lived <b>user</b> access token, or null if not
    /// connected. Only needed for user-scoped edges such as
    /// <c>GET /me/permissions</c> — Page-scoped calls must keep using
    /// <see cref="GetPageCredentialsAsync"/>, since with a Page token
    /// <c>/me</c> resolves to the Page and returns the wrong answer.
    /// </summary>
    public async Task<string?> GetUserAccessTokenAsync(CancellationToken ct = default)
    {
        if (!await IsConnectedAsync(ct).ConfigureAwait(false)) return null;
        return _bundle!.UserAccessToken;
    }

    /// <summary>
    /// Bağlı Sayfalar: birincil önce, sonra ekler saklanan sırayla. PageId'ye göre
    /// tekildir (ilk görülen kalır); kimliği ya da token'ı boş ek girdi atlanır,
    /// birincil hiç düşmez. Bağlı değilse boş liste. Ağ çağrısı yapmaz.
    /// </summary>
    public async Task<IReadOnlyList<FacebookLinkedPage>> GetConnectedPagesAsync(
        CancellationToken ct = default)
    {
        var bundle = await ReadBundleAsync(ct).ConfigureAwait(false);
        return bundle is null ? Array.Empty<FacebookLinkedPage>() : ConnectedPagesOf(bundle);
    }

    /// <summary>
    /// Starts the OAuth dance. <paramref name="pagePicker"/> is invoked when
    /// the operator has more than one Page; it gets the candidate list and
    /// should return the user's choice (or null to abort). If they have
    /// exactly one Page we auto-pick — keeps the common case one-click.
    ///
    /// <para>Tarayıcı onayı en çok <see cref="CallbackTimeout"/> beklenir ve aynı
    /// anda tek akış yürür (kurallar: <see cref="AcquireUserTokenAsync"/>).</para>
    /// </summary>
    /// <exception cref="TimeoutException">Onay süresinde tamamlanmadı; diske yazılmadı.</exception>
    /// <exception cref="InvalidOperationException">Bekleyen bir onay var, ya da
    /// akış hatayla bitti (Facebook ekranındaki "İptal" dahil).</exception>
    public async Task ConnectAsync(
        Func<IReadOnlyList<FacebookPageCandidate>, CancellationToken, Task<FacebookPageCandidate?>> pagePicker,
        CancellationToken ct = default)
    {
        var s = _settings();

        // 0-5. Tarayıcı onayıyla uzun ömürlü kullanıcı token'ı.
        var longLived = await AcquireUserTokenAsync(ct).ConfigureAwait(false);

        // 6. List the operator's Pages → ask the picker.
        var pages = await ListPagesAsync(longLived.AccessToken, ct).ConfigureAwait(false);
        if (pages.Count == 0)
            throw new InvalidOperationException(
                "Bu hesabın yönetebileceği Facebook Page bulunamadı.");

        var chosen = pages.Count == 1
            ? pages[0]
            : await pagePicker(pages, ct).ConfigureAwait(false);
        if (chosen is null)
            throw new OperationCanceledException("Operator aborted Page selection.");

        // 7. Page-scoped access token (separate from user token — what we
        //    bearer for every comment + moderation call).
        var pageToken = await GetPageAccessTokenAsync(chosen.Id, longLived.AccessToken, ct)
            .ConfigureAwait(false);

        // 8. Persist + cache.
        var expires = longLived.ExpiresInSeconds > 0
            ? DateTimeOffset.UtcNow.AddSeconds(longLived.ExpiresInSeconds)
            : (DateTimeOffset?)null;

        var bundle = new FacebookTokenBundle(
            UserAccessToken: longLived.AccessToken,
            PageId: chosen.Id,
            PageAccessToken: pageToken,
            PageName: chosen.Name,
            ExpiresAt: expires);

        // Kaydetme adımı kilit altında (gerekçe: _mutate): önce disk, sonra bellek.
        await _mutate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _tokenStore.SaveAsync(bundle, ct).ConfigureAwait(false);
            _bundle = bundle;

            // Mirror the Page id back into AppSettings so the rest of the app
            // can branch on "is Facebook configured?" without holding a reference
            // to this service.
            s.FacebookPageId = chosen.Id;
        }
        finally
        {
            _mutate.Release();
        }

        _log.LogInformation("Facebook OAuth connected for Page {PageId} ({PageName})",
            chosen.Id, chosen.Name);
    }

    /// <summary>Clears the local DPAPI blob. The upstream grant remains —
    /// operator can revoke it from facebook.com/settings &gt; Business
    /// Integrations if they want a hard reset. Ek Sayfalar da gider: demetin
    /// tamamı silinir.</summary>
    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        await _mutate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _tokenStore.ClearAsync(ct).ConfigureAwait(false);
            _bundle = null;
            _settings().FacebookPageId = null;
            _log.LogInformation("Facebook OAuth disconnected (local tokens cleared)");
        }
        finally
        {
            _mutate.Release();
        }
    }

    /// <summary>
    /// Bağlanabilecek Sayfaları SAKLI kullanıcı token'ıyla, tarayıcı açmadan listeler;
    /// zaten bağlı olanlar <see cref="FacebookAddablePage.AlreadyConnected"/> ile
    /// işaretlenir.
    ///
    /// <para>Token (190 / 463), izin (200 / 10) ve geçersiz alan (100) hatasında
    /// istisna yerine <see cref="FacebookPageListing.NeedsReauthorize"/> = true ve boş
    /// liste döner: bunların çözümü yeniden denemek değil, tarayıcılı izin
    /// güncellemesidir. 100 burada "yayın bitti" DEĞİLDİR (o anlam yalnız
    /// <c>live_media</c> içindir); bu uçta istenen alanın reddedildiğini gösterir.
    /// Diğer her hata (ağ, 5xx, kota) yukarı çıkar.</para>
    /// </summary>
    /// <exception cref="InvalidOperationException">Facebook bağlı değil.</exception>
    public async Task<FacebookPageListing> ListAddablePagesAsync(CancellationToken ct = default)
    {
        var bundle = await ReadBundleAsync(ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException(NotConnectedMessage);

        IReadOnlyList<FacebookAddablePage> rows;
        try
        {
            rows = await ListAccountsPagedAsync(bundle.UserAccessToken, ct).ConfigureAwait(false);
        }
        catch (FacebookGraphException ex) when (ex.Code is 190 or 463 or 200 or 10 or 100)
        {
            _log.LogInformation(
                "Facebook OAuth Page listing needs reauthorize (Graph code {Code}, subcode {Subcode})",
                ex.Code, ex.Subcode);
            return new FacebookPageListing(Array.Empty<FacebookAddablePage>(), NeedsReauthorize: true);
        }

        var connected = new HashSet<string>(StringComparer.Ordinal);
        foreach (var page in await GetConnectedPagesAsync(ct).ConfigureAwait(false))
            connected.Add(page.PageId);

        var pages = new List<FacebookAddablePage>(rows.Count);
        foreach (var row in rows)
            pages.Add(connected.Contains(row.PageId) ? row with { AlreadyConnected = true } : row);
        return new FacebookPageListing(pages, NeedsReauthorize: false);
    }

    /// <summary>
    /// Bir Sayfayı EK olarak bağlar. Sayfa token'ı SAKLI kullanıcı token'ıyla
    /// türetilir: tarayıcı açılmaz, sunucudaki takas ucu çağrılmaz. Yeni girdi ek
    /// listenin sonuna eklenir ve demet kaydedilir; birincil Sayfa ve ayarlardaki
    /// Sayfa kimliği değişmez.
    ///
    /// <para>Sayfa birincilse ya da zaten ekliyse Graph'a gidilmeden ve yazılmadan
    /// mevcut girdi döner (sınır dolu olsa da).</para>
    /// </summary>
    /// <exception cref="InvalidOperationException">Facebook bağlı değil.</exception>
    /// <exception cref="FacebookPageLimitException">Yeni bir Sayfaya yer yok
    /// (<see cref="MaxInstagramPages"/>); Graph'a gidilmez.</exception>
    public async Task<FacebookLinkedPage> AddExtraPageAsync(
        string pageId, string pageName, CancellationToken ct = default)
    {
        await _mutate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var bundle = await ReadBundleAsync(ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException(NotConnectedMessage);

            if (string.Equals(bundle.PageId, pageId, StringComparison.Ordinal))
                return PrimaryPageOf(bundle);

            var extras = ExtraPagesOf(bundle);
            var existing = extras.Find(page =>
                string.Equals(page.PageId, pageId, StringComparison.Ordinal));
            if (existing is not null) return existing;

            if (1 + extras.Count >= MaxInstagramPages)
                throw new FacebookPageLimitException(MaxInstagramPages);

            var pageToken = await GetPageAccessTokenAsync(pageId, bundle.UserAccessToken, ct)
                .ConfigureAwait(false);
            var added = new FacebookLinkedPage(pageId, pageToken, pageName);

            // Yazılan ek liste geçerli eklerden kurulur: diskten gelmiş bozuk ya da
            // yinelenen girdi varsa bu yazımda ayıklanır.
            extras.Add(added);
            var updated = bundle with { ExtraPages = extras };

            await _tokenStore.SaveAsync(updated, ct).ConfigureAwait(false);
            _bundle = updated;

            _log.LogInformation("Facebook OAuth extra Page added: {PageId} ({PageName})",
                pageId, pageName);
            return added;
        }
        finally
        {
            _mutate.Release();
        }
    }

    /// <summary>
    /// Bir EK Sayfanın bağlantısını kaldırır ve demeti kaydeder. Bağlı değilken,
    /// birincil Sayfada (tek başına kaldırılamaz) ve bilinmeyen kimlikte yazmadan
    /// false döner.
    /// </summary>
    public async Task<bool> RemoveExtraPageAsync(string pageId, CancellationToken ct = default)
    {
        await _mutate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var bundle = await ReadBundleAsync(ct).ConfigureAwait(false);
            if (bundle is null) return false;

            // Birincil ek listede yer almaz: onun kimliği de burada bulunamaz.
            var extras = ExtraPagesOf(bundle);
            var index = extras.FindIndex(page =>
                string.Equals(page.PageId, pageId, StringComparison.Ordinal));
            if (index < 0) return false;

            var removed = extras[index];
            extras.RemoveAt(index);
            var updated = bundle with { ExtraPages = extras.Count == 0 ? null : extras };

            await _tokenStore.SaveAsync(updated, ct).ConfigureAwait(false);
            _bundle = updated;

            _log.LogInformation("Facebook OAuth extra Page removed: {PageId} ({PageName})",
                removed.PageId, removed.PageName);
            return true;
        }
        finally
        {
            _mutate.Release();
        }
    }

    /// <summary>
    /// Tarayıcı onayını yeniden çalıştırır ("Facebook izinlerini güncelle"): kullanıcı
    /// token'ı ve yeni girişte görünen bağlı Sayfaların token'ları yenilenir. Sayfa
    /// eklenmez, çıkarılmaz ve birincil Sayfa değişmez: Sayfa seçici açılmaz,
    /// <see cref="ConnectAsync"/>'in "tek Sayfa dönerse onu seç" dalı bu yolda yoktur.
    /// NEDEN ayrı yol: bağlantıyı kaldırıp yeniden bağlanmak ek Sayfaları silerdi ve
    /// yeni girişte tek Sayfa dönerse birincil ona kayardı; Facebook yorumları ve
    /// moderasyon birincil Sayfadan çalışır.
    ///
    /// <para>Sıra: (1) tarayıcı onayıyla yeni kullanıcı token'ı (zaman aşımı, iptal ve
    /// tek akış kuralları: <see cref="AcquireUserTokenAsync"/>); (2) yeni token'la
    /// Sayfalar sayfalamalı listelenir; (3) bağlı olup listede görünmeyen Sayfa varsa
    /// <paramref name="confirmMissing"/> sorulur; (4) listede görünen her bağlı
    /// Sayfanın token'ı yeniden türetilir, türetilemeyenin eski token'ı kalır;
    /// (5) tek yazım.</para>
    ///
    /// <para>Kilit yalnız yazımı kapsar: tarayıcı beklemesi, soru ve türetme sürerken
    /// bağlantı kaldırılabilir, ek Sayfa eklenip kaldırılabilir. Yazım bu yüzden
    /// GÜNCEL demetin üstüne yapılır: o arada kaldırılan bağlantı dirilmez, kaldırılan
    /// ek Sayfa geri gelmez, eklenen korunur. Bağlantı o arada kaldırılıp YENİDEN
    /// kurulduysa da (güncel demetin kullanıcı token'ı, bağlı Sayfalar okunurken
    /// saklanandan farklıdır) hiçbir şey yazılmaz ve akış, bağlantı kaldırılmış gibi
    /// aynı istisnayla biter: bu akışın token'ları yeni bağlantının demetini ezmez.
    /// Listede görünmeyen Sayfanın girdisi SİLİNMEZ, eski token'ıyla kalır: erişimi
    /// gerçekten düştüyse sessizce kaybolmak yerine sağlık göstergesinde görünür.</para>
    ///
    /// <para>İstisnayla biten her yolda diskteki demet değişmemiştir.</para>
    /// </summary>
    /// <param name="confirmMissing">Yalnız bağlı olup yeni girişte görünmeyen Sayfa
    /// varsa, o Sayfalarla (bağlı sırasıyla, birincil dahil) çağrılır. False dönerse
    /// hiçbir şey yazılmaz. <see cref="ConnectAsync"/>'in Sayfa seçicisi gibi çağıranın
    /// iş parçacığında çağrılmayabilir.</param>
    /// <param name="ct">Akışın tamamını iptal eder.</param>
    /// <exception cref="InvalidOperationException">Facebook bağlı değil (tarayıcı
    /// açılmaz); bekleyen bir onay var; akış hatayla bitti (Facebook ekranındaki
    /// "İptal" dahil); ya da onay beklenirken bağlantı kaldırıldı.</exception>
    /// <exception cref="TimeoutException">Onay süresinde tamamlanmadı.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="confirmMissing"/>
    /// false döndü ya da <paramref name="ct"/> iptal edildi. Listeleme ağ zaman
    /// aşımına uğrarsa da bu tipin alt tipi (<see cref="TaskCanceledException"/>)
    /// çıkar; o durumda <paramref name="ct"/> iptal edilmemiştir.</exception>
    /// <exception cref="HttpRequestException">Sayfalar listelenemedi: ağ hatası ya da
    /// Graph hatası (<see cref="FacebookGraphException"/>). Token ve izin kodları da
    /// eşlenmeden çıkar: hata boş liste sayılmaz.</exception>
    public async Task<FacebookReauthorizeResult> ReauthorizeAsync(
        Func<IReadOnlyList<FacebookLinkedPage>, CancellationToken, Task<bool>> confirmMissing,
        CancellationToken ct = default)
    {
        // 1. Yalnız bağlıyken: bağlı değilse tarayıcı hiç açılmaz.
        if (await ReadBundleAsync(ct).ConfigureAwait(false) is null)
            throw new InvalidOperationException(NotConnectedMessage);

        var longLived = await AcquireUserTokenAsync(ct).ConfigureAwait(false);

        // ConnectAsync'teki hesap. Token alınır alınmaz yapılır: ömür o andan sayılır,
        // aşağıdaki soru ise dakikalarca yanıt bekleyebilir.
        var expires = longLived.ExpiresInSeconds > 0
            ? DateTimeOffset.UtcNow.AddSeconds(longLived.ExpiresInSeconds)
            : (DateTimeOffset?)null;

        // 2. Yeni girişin gördüğü Sayfalar, SAYFALAMALI. NEDEN ListPagesAsync değil: ilk
        //    yanıt sayfasına sığmayan bağlı Sayfa "eksik" sayılır, boşuna onay sorulurdu.
        //    Hata burada eşlenmez, olduğu gibi yükselir: boş liste sayılsaydı bağlı bütün
        //    Sayfalar eksik görünürdü.
        var listed = await ListAccountsPagedAsync(longLived.AccessToken, ct).ConfigureAwait(false);
        var listedIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in listed) listedIds.Add(row.PageId);

        // 3. Bağlı Sayfalar ŞİMDİ okunur: tarayıcı beklemesi kilit dışındaydı, o arada
        //    bağlantı kaldırılmış ya da ek Sayfalar değişmiş olabilir. Sayfalarla AYNI
        //    demetin kullanıcı token'ı saklanır: kayıt adımı, bağlantının bundan sonra
        //    kaldırılıp yeniden kurulduğunu ondan anlar.
        var snapshot = await ReadBundleAsync(ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException(DisconnectedWhileWaitingMessage);
        var connected = ConnectedPagesOf(snapshot);
        var connectedUserToken = snapshot.UserAccessToken;

        var missing = new List<FacebookLinkedPage>();
        foreach (var page in connected)
        {
            if (!listedIds.Contains(page.PageId)) missing.Add(page);
        }

        // Soru kilit DIŞINDA sorulur: yanıt beklenirken bağlantıyı kaldırmak mümkün kalır.
        if (missing.Count > 0 && !await confirmMissing(missing, ct).ConfigureAwait(false))
            throw new OperationCanceledException(
                "Yeni girişte görünmeyen Sayfalar onaylanmadı; izin güncellemesi kaydedilmedi.");

        // 4. Listede görünen her bağlı Sayfanın token'ı yeni kullanıcı token'ıyla, bağlı
        //    sırasıyla ve kilit DIŞINDA yeniden türetilir. Türetilemeyen Sayfa atlanır:
        //    eski token'ı kalır, öteki Sayfalar yenilenir. İptal, çağıranın belirtecinin
        //    iptal edilmiş olmasıdır; belirteç iptal edilmemişken gelen
        //    TaskCanceledException (HttpClient zaman aşımı) o Sayfanın hatasıdır.
        var derived = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var page in connected)
        {
            if (!listedIds.Contains(page.PageId)) continue;
            try
            {
                derived[page.PageId] = await GetPageAccessTokenAsync(
                    page.PageId, longLived.AccessToken, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Satır yalnız hata tipini, HTTP durumunu ve Graph kodlarını taşır: istisna
                // iletisi (Graph gövdesi) yazılmaz. NEDEN HTTP durumu: JSON olmayan 5xx / 429
                // gövdesinde Graph kodları okunamaz; durum yazılmasa 500, 502, 429 ve 404
                // günlükte ayırt edilemezdi. İstisna durum taşımıyorsa (bağlantı hatası, ağ
                // zaman aşımı, token'sız 200 gövdesi) null yazılır.
                var graph = ex as FacebookGraphException;
                _log.LogWarning(
                    "Facebook OAuth reauthorize kept the stored token for Page {PageId}: " +
                    "a new one could not be derived " +
                    "({Error}, HTTP {Status}, Graph code {Code}, subcode {Subcode})",
                    page.PageId, ex.GetType().Name, (ex as HttpRequestException)?.StatusCode,
                    graph?.Code, graph?.Subcode);
            }
        }

        // 5. Tek yazım, kilit altında (gerekçe: _mutate) ve GÜNCEL demetin üstüne: kilit
        //    yukarıdaki adımları kapsamadı. Bağlantı o arada kaldırıldıysa hiçbir şey
        //    yazılmaz (bağlantı dirilmez). Kaldırılıp YENİDEN kurulduysa da yazılmaz
        //    (yenisi ezilmez): o zaman güncel demetin kullanıcı token'ı 3. adımda
        //    saklanandan farklıdır; yazılsaydı bu akışın kullanıcı token'ı başka bir
        //    girişin demetine girerdi. Ek Sayfa eklemek / kaldırmak kullanıcı token'ını
        //    değiştirmez, bu denetime takılmaz. Sayfa eklenmez, çıkarılmaz: türetilen
        //    token'lar yalnız hâlâ bağlı Sayfalara, PageId eşleşmesiyle yazılır.
        await _mutate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var current = await ReadBundleAsync(ct).ConfigureAwait(false);
            if (current is null ||
                !string.Equals(current.UserAccessToken, connectedUserToken, StringComparison.Ordinal))
                throw new InvalidOperationException(DisconnectedWhileWaitingMessage);

            var refreshed = 0;
            var primaryToken = current.PageAccessToken;
            if (derived.TryGetValue(current.PageId, out var derivedPrimary))
            {
                primaryToken = derivedPrimary;
                refreshed++;
            }

            // Yazılan ek liste geçerli eklerden kurulur (AddExtraPageAsync'teki gibi).
            var extras = ExtraPagesOf(current);
            for (var i = 0; i < extras.Count; i++)
            {
                if (!derived.TryGetValue(extras[i].PageId, out var derivedExtra)) continue;
                extras[i] = extras[i] with { PageAccessToken = derivedExtra };
                refreshed++;
            }

            var updated = current with
            {
                UserAccessToken = longLived.AccessToken,
                PageAccessToken = primaryToken,
                ExpiresAt = expires,
                ExtraPages = extras.Count == 0 ? null : extras,
            };

            await _tokenStore.SaveAsync(updated, ct).ConfigureAwait(false);
            _bundle = updated;

            _log.LogInformation(
                "Facebook OAuth reauthorized for Page {PageId} ({PageName}): " +
                "{Refreshed} refreshed, {Missing} missing [{MissingPageIds}]",
                updated.PageId, updated.PageName, refreshed, missing.Count,
                string.Join(",", missing.ConvertAll(page => page.PageId)));
            return new FacebookReauthorizeResult(refreshed, missing);
        }
        finally
        {
            _mutate.Release();
        }
    }

    // ---- helpers --------------------------------------------------------

    /// <summary>
    /// Tarayıcı onayıyla uzun ömürlü KULLANICI token'ını alır: yapılandırma, onay
    /// adresi, dinleyici + tarayıcı, geri çağrı denetimi, code takası. Diske yazmaz,
    /// demete dokunmaz ve <see cref="_mutate"/> kilidini almaz.
    ///
    /// <para>Tarayıcı beklemesinin üç kuralı:</para>
    /// <list type="bullet">
    ///   <item><b>Zaman aşımı.</b> Geri çağrı kurucudan verilen sürede (üretimde
    ///     <see cref="CallbackTimeout"/>) gelmezse dinleyici bırakılır ve
    ///     <see cref="TimeoutException"/> fırlatılır.</item>
    ///   <item><b>İptal.</b> <paramref name="ct"/> beklemeyi keser, dinleyici
    ///     bırakılır, <see cref="OperationCanceledException"/> yükselir.</item>
    ///   <item><b>Tek akış.</b> Bekleyen bir akış varken ikinci çağrı sunucuya
    ///     gitmeden ve dinleyiciye dokunmadan
    ///     <see cref="InvalidOperationException"/> ile reddedilir.</item>
    /// </list>
    ///
    /// <para>Facebook ekranındaki "İptal" bir geri çağrıdır
    /// (<c>error=access_denied</c>): iptal değil, <see cref="InvalidOperationException"/>.</para>
    /// </summary>
    private async Task<FacebookLongLivedToken> AcquireUserTokenAsync(CancellationToken ct)
    {
        // Koruma İLK iştir (yapılandırma isteğinden de önce): reddedilen çağrı
        // bekleyen akışın dinleyicisine dokunmaz.
        if (Interlocked.CompareExchange(ref _browserFlowPending, 1, 0) != 0)
            throw new InvalidOperationException(PendingFlowMessage);

        try
        {
            // 0. Yapılandırma lisans sunucusundan gelir. Eskiden derleme
            //    zamanı sabitiydi; yayın hattına enjeksiyon adımı hiç eklenmediği
            //    için sahaya BOŞ gidiyor ve her kurulumda "App ID eksik" hatası
            //    veriyordu. Artık tek kaynak sunucu.
            var cfg = await _broker.GetFacebookOAuthConfigAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(cfg.AppId))
                throw new InvalidOperationException(
                    "Sunucu Facebook uygulamasını bildirmedi. Lisans sunucusunda " +
                    "OrderDeck:Facebook yapılandırması eksik.");

            // 1. Authorise URL — state binds the callback to this attempt so a
            //    stale browser tab can't smuggle a code from a different session.
            var state = GenerateState();
            var url = BuildAuthorizeUrl(cfg.AppId, cfg.LoginConfigId, cfg.RedirectUri, state);

            // 2-3. Dinleyici + tarayıcı, sonra geri çağrı beklenir. Bekleme
            //      çağıranın belirtecine bağlıdır ve süre dolunca kendiliğinden
            //      iptal olur. Bekleme nesnesi geri çağrı alınır alınmaz (takastan
            //      önce) ve her hata yolunda bırakılır: dinleyici açık kalmaz.
            FacebookOAuthCallback callback;
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            using (var wait = _browser.Open(url))
            {
                timeout.CancelAfter(_callbackTimeout);
                try
                {
                    callback = await wait.WaitAsync(timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                    when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
                {
                    // İptalin kaynağı çağıran değil, süre: iptal değil zaman aşımıdır.
                    throw new TimeoutException(CallbackTimeoutMessage);
                }
            }

            if (callback.Error is not null)
                throw new InvalidOperationException(
                    $"Facebook OAuth error: {callback.Error} — {callback.ErrorDescription ?? "(no description)"}");
            if (string.IsNullOrEmpty(callback.Code))
                throw new InvalidOperationException("Facebook OAuth callback missing ?code");
            if (!CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(state),
                    Encoding.UTF8.GetBytes(callback.State ?? string.Empty)))
            {
                throw new InvalidOperationException("OAuth state mismatch — possible CSRF.");
            }

            // 4-5. Code → uzun ömürlü kullanıcı token'ı. İki adımı da sunucu
            //      yapıyor; App Secret bu binary'de YOK. Code kısa yaşıyor,
            //      araya bir şey sokma.
            return await _broker.ExchangeFacebookCodeAsync(callback.Code, ct).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _browserFlowPending, 0);
        }
    }

    /// <summary>
    /// Güncel demet; bağlı değilse null. Bellekte demet yoksa önce token dosyasından
    /// yüklenir (ağ yok), sonra alan BİR KEZ okunur. NEDEN tek okuma: okuyan üyeler
    /// kilitsizdir; yüklemeyle okuma arasına <see cref="DisconnectAsync"/> girerse alan
    /// null olur ve çağıran istisna yerine "bağlı değil" görür. Yükleme de kilitsizdir
    /// ama alanı yalnız BOŞKEN yayımlar (<see cref="IsConnectedAsync"/>): kilit altında
    /// yayımlanmış demeti ezemez. Değiştiren üyeler bunu <see cref="_mutate"/> altında
    /// çağırır: o zaman dönen demet günceldir (kalan pencere <see cref="_mutate"/>
    /// alanının yorumunda).
    /// </summary>
    private async Task<FacebookTokenBundle?> ReadBundleAsync(CancellationToken ct)
    {
        await IsConnectedAsync(ct).ConfigureAwait(false);
        return _bundle;
    }

    private static FacebookLinkedPage PrimaryPageOf(FacebookTokenBundle bundle) =>
        new(bundle.PageId, bundle.PageAccessToken, bundle.PageName);

    /// <summary>
    /// Demetin geçerli EK Sayfaları, saklanan sırayla. Diskten gelen liste bozuk girdi
    /// taşıyabilir: null öğe ile kimliği ya da token'ı boş girdi atlanır; birincilin
    /// kimliğini taşıyan ya da yinelenen girdide ilk görülen kalır. Böylece tek bir
    /// bozuk girdi öteki Sayfaları düşürmez.
    /// </summary>
    private static List<FacebookLinkedPage> ExtraPagesOf(FacebookTokenBundle bundle)
    {
        var extras = new List<FacebookLinkedPage>(bundle.ExtraPages?.Count ?? 0);
        if (bundle.ExtraPages is null) return extras;

        var seen = new HashSet<string>(StringComparer.Ordinal) { bundle.PageId };
        foreach (var extra in bundle.ExtraPages)
        {
            if (extra is null ||
                string.IsNullOrWhiteSpace(extra.PageId) ||
                string.IsNullOrWhiteSpace(extra.PageAccessToken))
                continue;
            if (seen.Add(extra.PageId)) extras.Add(extra);
        }
        return extras;
    }

    /// <summary>Birincil önce, sonra <see cref="ExtraPagesOf"/>.</summary>
    private static List<FacebookLinkedPage> ConnectedPagesOf(FacebookTokenBundle bundle)
    {
        var extras = ExtraPagesOf(bundle);
        var pages = new List<FacebookLinkedPage>(1 + extras.Count) { PrimaryPageOf(bundle) };
        pages.AddRange(extras);
        return pages;
    }

    /// <summary>
    /// <paramref name="redirectUri"/> Meta'ya kayıtlı olan adrestir ve loopback
    /// DEĞİLDİR: Meta "Valid OAuth Redirect URIs" altına localhost kaydını
    /// Live mode'da kabul etmiyor. orderdeckapp.com/facebook/callback, sorgu
    /// dizesini olduğu gibi <see cref="LoopbackPrefix"/>'e 302'leyen bir Caddy
    /// kuralı — sunucuda hiçbir şey saklanmıyor (deploy/Caddyfile).
    ///
    /// Strict Mode birebir eşleşme istediği için authorize çağrısındaki ve
    /// code→token takasındaki değer AYNI olmalı; ikisi de artık sunucudan
    /// geliyor, tek kaynak orası.
    /// </summary>
    private static string BuildAuthorizeUrl(
        string appId, string configId, string redirectUri, string state)
    {
        // config_id encodes the Login for Business permission set so we don't
        // pass scope=... at all (Meta picks scopes from the configuration).
        var sb = new StringBuilder(DialogBase);
        sb.Append("?client_id=").Append(Uri.EscapeDataString(appId));
        sb.Append("&redirect_uri=").Append(Uri.EscapeDataString(redirectUri));
        sb.Append("&state=").Append(Uri.EscapeDataString(state));
        sb.Append("&response_type=code");
        if (!string.IsNullOrWhiteSpace(configId))
            sb.Append("&config_id=").Append(Uri.EscapeDataString(configId));
        return sb.ToString();
    }

    private async Task<IReadOnlyList<FacebookPageCandidate>> ListPagesAsync(
        string userAccessToken, CancellationToken ct)
    {
        // /me/accounts returns Pages the operator can manage. We only need
        // id + name for the picker; access_token is fetched per-Page in step 7
        // so we don't accidentally cache stale tokens for unselected Pages.
        var url = $"{GraphBase}/me/accounts?fields=id,name&access_token={Uri.EscapeDataString(userAccessToken)}";
        var resp = await GetJsonAsync<MeAccountsResponse>(url, ct).ConfigureAwait(false);
        var list = new List<FacebookPageCandidate>(resp.Data?.Count ?? 0);
        if (resp.Data is not null)
        {
            foreach (var item in resp.Data)
            {
                if (!string.IsNullOrEmpty(item.Id) && !string.IsNullOrEmpty(item.Name))
                    list.Add(new FacebookPageCandidate(item.Id!, item.Name!));
            }
        }
        return list;
    }

    /// <summary>
    /// Kullanıcının yönettiği Sayfaları, bağlı Instagram hesabıyla birlikte ve
    /// SAYFALAMALI okur: <c>paging.next</c> adresi aynen izlenir (en çok
    /// <see cref="MaxAccountsResponsePages"/> yanıt sayfası). Kimliği ya da adı boş
    /// satır atlanır; sonuç PageId'ye göre tekildir.
    /// <see cref="FacebookAddablePage.AlreadyConnected"/> hep false döner, onu çağıran
    /// kurar.
    ///
    /// <para>Hata eşlemesi yapmaz: Graph hatası <see cref="FacebookGraphException"/>
    /// olarak çıkar, eşleme çağıranındır. <c>paging.next</c> adresi kullanıcı token'ını
    /// taşır: hiçbir günlük satırına ve istisna mesajına yazılmaz.</para>
    ///
    /// <para><see cref="ListPagesAsync"/>'ten ayrı: ilk bağlantının isteği değişmesin
    /// diye o yöntem olduğu gibi bırakıldı.</para>
    /// </summary>
    private async Task<IReadOnlyList<FacebookAddablePage>> ListAccountsPagedAsync(
        string userAccessToken, CancellationToken ct)
    {
        var url = $"{GraphBase}/me/accounts" +
                  $"?fields={Uri.EscapeDataString("id,name,instagram_business_account{id,username}")}" +
                  $"&limit=100" +
                  $"&access_token={Uri.EscapeDataString(userAccessToken)}";

        var pages = new List<FacebookAddablePage>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var read = 0; read < MaxAccountsResponsePages && !string.IsNullOrEmpty(url); read++)
        {
            var resp = await GetJsonAsync<MeAccountsResponse>(url, ct).ConfigureAwait(false);
            if (resp.Data is not null)
            {
                foreach (var item in resp.Data)
                {
                    if (item is null || string.IsNullOrEmpty(item.Id) || string.IsNullOrEmpty(item.Name))
                        continue;
                    if (!seen.Add(item.Id)) continue;

                    // Instagram izni yoksa ya da Sayfaya hesap bağlı değilse Graph alanı
                    // yanıttan düşürür: iki Instagram alanı da null kalır.
                    var instagram = item.InstagramBusinessAccount;
                    var igUserId = string.IsNullOrEmpty(instagram?.Id) ? null : instagram.Id;
                    pages.Add(new FacebookAddablePage(
                        item.Id,
                        item.Name,
                        igUserId,
                        igUserId is null ? null : instagram?.Username,
                        AlreadyConnected: false));
                }
            }
            url = resp.Paging?.Next;
        }
        return pages;
    }

    private async Task<string> GetPageAccessTokenAsync(
        string pageId, string userAccessToken, CancellationToken ct)
    {
        var url = $"{GraphBase}/{Uri.EscapeDataString(pageId)}" +
                  $"?fields=access_token" +
                  $"&access_token={Uri.EscapeDataString(userAccessToken)}";
        var resp = await GetJsonAsync<PageTokenResponse>(url, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(resp.AccessToken))
            throw new InvalidOperationException(
                "Graph API returned no access_token for the selected Page. " +
                "Operator may lack admin role on that Page.");
        return resp.AccessToken!;
    }

    private async Task<T> GetJsonAsync<T>(string url, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            // Graph hata gövdesi iletiye girer (küçük JSON, günlükte çok işe yarar), ama
            // önce isteğin kendi token değerleri silinir. NEDEN: Graph ayrıştıramadığı
            // token'ı hata metninde geri yazabiliyor; bu ileti günlüğe ve Ayarlar'daki
            // durum satırına kadar çıkabilir. Kod ve alt kod ÖZGÜN gövdeden okunur.
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var (code, subcode) = ReadGraphError(body);
            throw new FacebookGraphException(
                $"Graph API {(int)resp.StatusCode} {resp.ReasonPhrase}: {MaskRequestTokens(body, url)}",
                resp.StatusCode, code, subcode);
        }
        var parsed = await resp.Content.ReadFromJsonAsync<T>(cancellationToken: ct).ConfigureAwait(false);
        if (parsed is null)
        {
            // Mesajda adres YOK: adres access_token taşır ve bu mesaj Ayarlar'daki
            // durum satırına kadar çıkabilir.
            throw new InvalidOperationException("Graph API returned empty body.");
        }
        return parsed;
    }

    /// <summary>
    /// Graph hata gövdesinden (<c>{"error":{"code":…,"error_subcode":…}}</c>) kodu ve
    /// alt kodu okur. Gövde JSON değilse (ör. HTML hata sayfası) ya da alanlar yoksa
    /// null döner; hiç fırlatmaz: asıl hata bu ayrıştırmanın hatasıyla örtülmesin.
    /// </summary>
    private static (int? Code, int? Subcode) ReadGraphError(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return (null, null);
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("error", out var error) ||
                error.ValueKind != JsonValueKind.Object)
                return (null, null);

            return (ReadInt(error, "code"), ReadInt(error, "error_subcode"));
        }
        catch (JsonException)
        {
            return (null, null);
        }

        static int? ReadInt(JsonElement parent, string name) =>
            parent.TryGetProperty(name, out var value) &&
            value.ValueKind == JsonValueKind.Number &&
            value.TryGetInt32(out var number)
                ? number
                : null;
    }

    /// <summary>
    /// Hata gövdesinden İSTEĞİN KENDİ token değerlerini siler. Kural: adresin sorgu
    /// dizgisinde adı <c>token</c> ile biten (büyük / küçük harf ayırmadan:
    /// <c>access_token</c>, <c>fb_exchange_token</c>, <c>input_token</c> …) her
    /// parametrenin boş olmayan değeri, gövdede hem adreste yazıldığı (yüzde kodlu)
    /// hâliyle hem çözülmüş hâliyle <c>***</c> olur. Hiç fırlatmaz; sorgusu
    /// ayrıştırılamayan adreste gövde olduğu gibi kalır: asıl hata bu yardımcının
    /// hatasıyla örtülmesin.
    /// </summary>
    private static string MaskRequestTokens(string body, string url)
    {
        const string Mask = "***";

        // Sorgu dizgisi: ilk '?' ile (varsa) '#' arası.
        var fragment = url.IndexOf('#');
        var address = fragment < 0 ? url : url[..fragment];
        var queryStart = address.IndexOf('?');
        if (queryStart < 0 || body.Length == 0) return body;

        foreach (var part in address[(queryStart + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf('=');
            if (separator < 0 ||
                !part[..separator].EndsWith("token", StringComparison.OrdinalIgnoreCase))
                continue;

            var written = part[(separator + 1)..];
            if (written.Length == 0) continue;

            // İki hâli de: Graph değeri çözülmüş hâliyle yankılar, istek adresini
            // aktaran bir hata ise yazıldığı hâliyle. Değer yüzde kodlu karakter
            // taşımıyorsa ikisi aynıdır.
            body = body.Replace(written, Mask, StringComparison.Ordinal)
                .Replace(Uri.UnescapeDataString(written), Mask, StringComparison.Ordinal);
        }
        return body;
    }

    private static string GenerateState()
    {
        Span<byte> buf = stackalloc byte[24];
        RandomNumberGenerator.Fill(buf);
        return Convert.ToHexString(buf);
    }

    // ---- wire shapes ----------------------------------------------------

    private sealed class MeAccountsResponse
    {
        [JsonPropertyName("data")] public List<MeAccount>? Data { get; set; }
        [JsonPropertyName("paging")] public MePaging? Paging { get; set; }
    }

    private sealed class MeAccount
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("instagram_business_account")]
        public MeInstagramAccount? InstagramBusinessAccount { get; set; }
    }

    private sealed class MeInstagramAccount
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("username")] public string? Username { get; set; }
    }

    private sealed class MePaging
    {
        [JsonPropertyName("next")] public string? Next { get; set; }
    }

    private sealed class PageTokenResponse
    {
        [JsonPropertyName("access_token")] public string? AccessToken { get; set; }
    }
}

/// <summary>Lightweight projection of a Page row from <c>/me/accounts</c>,
/// used by the picker UI during the OAuth flow.</summary>
public sealed record FacebookPageCandidate(string Id, string Name);

/// <summary>
/// "Instagram hesabı ekle" listesindeki bir Sayfa: kullanıcının yönettiği Sayfa ve
/// (varsa) ona bağlı Instagram profesyonel hesabı.
/// </summary>
/// <param name="PageId">Facebook Sayfa kimliği.</param>
/// <param name="PageName">Sayfanın görünen adı.</param>
/// <param name="IgUserId">Bağlı Instagram hesabının kimliği; Sayfaya hesap bağlı
/// değilse ya da oturumda Instagram izni yoksa null.</param>
/// <param name="IgUsername">Bağlı Instagram hesabının kullanıcı adı ("@"sız); hesap
/// yoksa ya da Graph adı döndürmediyse null.</param>
/// <param name="AlreadyConnected">Sayfa zaten bağlı (birincil ya da ek).</param>
public sealed record FacebookAddablePage(
    string PageId, string PageName, string? IgUserId, string? IgUsername, bool AlreadyConnected);

/// <summary>
/// <see cref="FacebookOAuthService.ListAddablePagesAsync"/> sonucu.
/// </summary>
/// <param name="Pages">Kullanıcının yönettiği Sayfalar (bağlı olanlar işaretli).</param>
/// <param name="NeedsReauthorize">Saklı oturumla liste okunamadı (token, izin ya da
/// reddedilen alan): tarayıcılı izin güncellemesi gerekir. True iken
/// <paramref name="Pages"/> boştur.</param>
public sealed record FacebookPageListing(IReadOnlyList<FacebookAddablePage> Pages, bool NeedsReauthorize);

/// <summary>
/// <see cref="FacebookOAuthService.ReauthorizeAsync"/> sonucu.
/// </summary>
/// <param name="RefreshedPages">Kaydedilen demete yeni türetilmiş token'ı yazılan Sayfa
/// sayısı (birincil dahil). Token'ı türetilemeyen, yeni girişte görünmeyen ve akış
/// sürerken kaldırılan Sayfa sayılmaz.</param>
/// <param name="MissingPages">Bağlı olup yeni girişte görünmeyen Sayfalar (bağlı
/// sırasıyla, birincil dahil). Girdileri silinmedi, eski token'larıyla duruyor;
/// erişimleri düşmüş olabilir.</param>
public sealed record FacebookReauthorizeResult(
    int RefreshedPages, IReadOnlyList<FacebookLinkedPage> MissingPages);
