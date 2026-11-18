using System;
using System.Collections.Generic;
using System.Diagnostics;
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

    private const string NotConnectedMessage = "Facebook bağlı değil.";

    private static readonly string GraphBase =
        $"https://graph.facebook.com/{FacebookOAuthDefaults.GraphApiVersion}";
    private static readonly string DialogBase =
        $"https://www.facebook.com/{FacebookOAuthDefaults.GraphApiVersion}/dialog/oauth";

    private readonly Func<AppSettings> _settings;
    private readonly EncryptedFacebookTokenStore _tokenStore;
    private readonly IFacebookOAuthBroker _broker;
    private readonly HttpClient _http;
    private readonly ILogger<FacebookOAuthService> _log;

    // Demeti oku-değiştir-yaz yapan bölümler (ek Sayfa ekle / kaldır, ConnectAsync'in
    // kaydetme adımı, DisconnectAsync) bu kilit altında çalışır. NEDEN: iki değişiklik
    // aynı eski demetten yola çıkarsa sonuncusu ötekinin yazdığını ezer (kayıp
    // güncelleme). Kilit altında önce diske yazılır, sonra _bundle TEK atamayla
    // değişir: yazım düşerse bellek eski hâlinde kalır. Kilit tarayıcı beklemesini
    // KAPSAMAZ. Okuyan üyeler kilit almaz: demet değişmez bir kayıttır.
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
    {
        _settings = settings;
        _tokenStore = tokenStore;
        _broker = broker;
        _http = http;
        _log = log;
    }

    /// <summary>True if a Page access token is on disk.</summary>
    public async Task<bool> IsConnectedAsync(CancellationToken ct = default)
    {
        if (_bundle is not null) return true;
        _bundle = await _tokenStore.LoadAsync(ct).ConfigureAwait(false);
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
    /// </summary>
    public async Task ConnectAsync(
        Func<IReadOnlyList<FacebookPageCandidate>, CancellationToken, Task<FacebookPageCandidate?>> pagePicker,
        CancellationToken ct = default)
    {
        var s = _settings();

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

        // 2. Spin up the loopback listener BEFORE opening the browser so we
        //    don't lose the redirect if Facebook is uncharacteristically fast.
        using var listener = new HttpListener();
        listener.Prefixes.Add(LoopbackPrefix);
        try
        {
            listener.Start();
        }
        catch (HttpListenerException ex)
        {
            throw new InvalidOperationException(
                $"Failed to bind {LoopbackPrefix} — another process is using " +
                "the port, or you need to run as administrator once to grant " +
                "URL ACL.", ex);
        }

        OpenBrowser(url);

        // 3. Block until either the user completes the flow or the cancel
        //    token fires (e.g. operator closed the settings dialog). The
        //    listener stops the moment we return.
        var (code, returnedState) = await WaitForCallbackAsync(listener, ct).ConfigureAwait(false);
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(state),
                Encoding.UTF8.GetBytes(returnedState ?? string.Empty)))
        {
            throw new InvalidOperationException("OAuth state mismatch — possible CSRF.");
        }

        // 4-5. Code → uzun ömürlü kullanıcı token'ı. İki adımı da sunucu
        //      yapıyor; App Secret bu binary'de YOK. Code kısa yaşıyor,
        //      araya bir şey sokma.
        var longLived = await _broker.ExchangeFacebookCodeAsync(code, ct).ConfigureAwait(false);

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

    // ---- helpers --------------------------------------------------------

    /// <summary>
    /// Güncel demet; bağlı değilse null. Bellekte demet yoksa önce token dosyasından
    /// yüklenir (ağ yok), sonra alan BİR KEZ okunur. NEDEN tek okuma: okuyan üyeler
    /// kilitsizdir; yüklemeyle okuma arasına <see cref="DisconnectAsync"/> girerse alan
    /// null olur ve çağıran istisna yerine "bağlı değil" görür. Değiştiren üyeler bunu
    /// <see cref="_mutate"/> altında çağırır: o zaman dönen demet günceldir.
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

    private static void OpenBrowser(string url)
    {
        // Process.Start with UseShellExecute=true lets Windows resolve the
        // operator's default browser — same trick as the YouTube broker.
        Process.Start(new ProcessStartInfo
        {
            FileName = url,
            UseShellExecute = true,
        });
    }

    private static async Task<(string Code, string? State)> WaitForCallbackAsync(
        HttpListener listener, CancellationToken ct)
    {
        // Hook ct so closing the settings dialog actually aborts the listener.
        using var reg = ct.Register(() =>
        {
            try { listener.Stop(); } catch { /* already disposed */ }
        });

        HttpListenerContext ctx;
        try
        {
            ctx = await listener.GetContextAsync().ConfigureAwait(false);
        }
        catch (HttpListenerException) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }
        catch (ObjectDisposedException) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }

        var query = ctx.Request.QueryString;
        var code = query["code"];
        var state = query["state"];
        var error = query["error"];
        var errorDesc = query["error_description"];

        // Always send a friendly HTML response so the user knows they can
        // close the tab — otherwise they're staring at a blank page.
        string html = error is null
            ? "<html><body style='font-family:sans-serif;text-align:center;padding-top:80px'>" +
              "<h2>Facebook bağlantısı tamamlandı.</h2>" +
              "<p>Bu sekmeyi kapatabilirsiniz.</p></body></html>"
            : $"<html><body style='font-family:sans-serif;text-align:center;padding-top:80px'>" +
              $"<h2>Bağlantı iptal edildi.</h2>" +
              $"<p>{WebUtility.HtmlEncode(errorDesc ?? error)}</p></body></html>";
        var bytes = Encoding.UTF8.GetBytes(html);
        ctx.Response.ContentType = "text/html; charset=utf-8";
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes, ct).ConfigureAwait(false);
        ctx.Response.OutputStream.Close();

        if (error is not null)
            throw new InvalidOperationException(
                $"Facebook OAuth error: {error} — {errorDesc ?? "(no description)"}");
        if (string.IsNullOrEmpty(code))
            throw new InvalidOperationException("Facebook OAuth callback missing ?code");

        return (code!, state);
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
            // Surface the Graph error body — it's small JSON, very useful in logs.
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var (code, subcode) = ReadGraphError(body);
            throw new FacebookGraphException(
                $"Graph API {(int)resp.StatusCode} {resp.ReasonPhrase}: {body}",
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
