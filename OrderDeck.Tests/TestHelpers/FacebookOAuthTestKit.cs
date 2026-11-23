using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Web;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OrderDeck.Chat.Facebook;
using OrderDeck.Core.Chat;
using OrderDeck.Core.Settings;

namespace OrderDeck.Tests.TestHelpers;

/// <summary>
/// Facebook OAuth servisini ağa ve tarayıcıya çıkmadan kuran test kiti: geçici klasörde GERÇEK
/// şifreli depo (DPAPI), Graph yerine <see cref="FakeGraph"/>, lisans sunucusu yerine
/// <see cref="FakeFacebookOAuthBroker"/>, tarayıcı ve geri çağrı dinleyicisi yerine
/// <see cref="FakeFacebookOAuthBrowser"/>.
///
/// NEDEN gerçek depo: sınanan sözleşmelerin yarısı diskle ilgili (önce diske yaz, yazım düşerse
/// bellek değişmesin, ikinci servis örneği aynı dosyayı görsün); sahte depo bunları kanıtlamaz.
/// Depo PUBLIC olduğu için kimlik, token ve ad literal yazılmaz, her çağrıda üretilir.
/// </summary>
internal sealed class FacebookOAuthTestKit : IDisposable
{
    private readonly string _folder;

    public FacebookOAuthTestKit()
    {
        _folder = Path.Combine(Path.GetTempPath(), $"orderdeck-fb-oauth-{Guid.NewGuid():N}");
        Store = new EncryptedFacebookTokenStore(_folder);
        TokenFilePath = Path.Combine(_folder, "facebook-token.bin");
        HttpFactory = new GraphHttpClientFactory(Graph);
    }

    public EncryptedFacebookTokenStore Store { get; }

    /// <summary>Deponun yazdığı dosya (bayt karşılaştırması ve "yazım düşsün" kurguları için).</summary>
    public string TokenFilePath { get; }

    public AppSettings Settings { get; } = new();

    public FakeGraph Graph { get; } = new();

    public FakeFacebookOAuthBroker Broker { get; } = new();

    /// <summary>Kitin kurduğu bütün servis örneklerinin paylaştığı sahte tarayıcı.</summary>
    public FakeFacebookOAuthBrowser Browser { get; } = new();

    /// <summary>Hangi ad istenirse istensin <see cref="Graph"/>'a bağlı istemci verir.</summary>
    public IHttpClientFactory HttpFactory { get; }

    /// <summary>
    /// AYNI depo üstünde yeni bir servis örneği. Belleği boş başlar: diskten okur; "yazılan
    /// gerçekten diske indi mi" sorusu ikinci bir örnekle böyle sınanır. Tarayıcı onayı
    /// <see cref="Browser"/>'dan geçer; <paramref name="callbackTimeout"/> verilmezse servisin
    /// kendi süresi (<see cref="FacebookOAuthService.CallbackTimeout"/>) geçerlidir.
    /// </summary>
    public FacebookOAuthService CreateService(
        ILogger<FacebookOAuthService>? log = null, TimeSpan? callbackTimeout = null) =>
        new(() => Settings, Store, Broker, new HttpClient(Graph, disposeHandler: false),
            log ?? NullLogger<FacebookOAuthService>.Instance,
            Browser, callbackTimeout ?? FacebookOAuthService.CallbackTimeout);

    /// <summary>
    /// Bağlı bir kurulum tohumlar: birincil + <paramref name="extraPages"/> ek Sayfa. Demet depoya
    /// yazılır, ayarlardaki Sayfa kimliği birincile kurulur, her Sayfa Instagram hesabıyla
    /// <see cref="Graph"/>'a tanıtılır. Dönen liste birincil önce sıralıdır.
    /// </summary>
    public async Task<IReadOnlyList<SeededPage>> SeedAsync(int extraPages = 0)
    {
        var pages = Enumerable.Range(0, 1 + extraPages)
            .Select(_ => new SeededPage(
                new FacebookLinkedPage(NewId(), NewToken(), $"sayfa-{Guid.NewGuid():N}"),
                IgUserId: NewId(),
                IgUsername: $"hesap-{Guid.NewGuid():N}"))
            .ToList();
        var primary = pages[0].Page;

        await Store.SaveAsync(new FacebookTokenBundle(
            NewToken(), primary.PageId, primary.PageAccessToken, primary.PageName, ExpiresAt: null,
            extraPages == 0 ? null : pages.Skip(1).Select(p => p.Page).ToList()));
        Settings.FacebookPageId = primary.PageId;
        foreach (var seeded in pages)
            Graph.AddPage(seeded.Page.PageId, seeded.Page.PageName, seeded.IgUserId, seeded.IgUsername);
        return pages;
    }

    /// <summary>
    /// "Facebook izinlerini güncelle"yi baştan sona koşar: yeniden onayı başlatır (eksik Sayfa
    /// sorusuna "evet" der), servis tarayıcıyı açınca onayı verir ve sonucu
    /// <see cref="TestPatience.Upper"/> içinde bekler. Ön koşul: bağlı Sayfalar <see cref="Graph"/>'ta
    /// tanıtılmıştır (<see cref="SeedAsync"/> / <see cref="FakeGraph.AddPage"/>); o zaman hepsi yeni
    /// girişte listelenir, kullanıcı token'ı ve her Sayfanın token'ı değişir. "Token değişti"
    /// kurguları bununla üretilir.
    /// </summary>
    public async Task<FacebookReauthorizeResult> ReauthorizeAllAsync(FacebookOAuthService service)
    {
        var flow = service.ReauthorizeAsync((_, _) => Task.FromResult(true));
        await Browser.WaitUntilOpenedAsync();
        Browser.Complete();
        return await flow.WaitAsync(TestPatience.Upper);
    }

    public static string NewId() => $"id-{Guid.NewGuid():N}";

    public static string NewToken() => $"t-{Guid.NewGuid():N}";

    public void Dispose()
    {
        try { if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true); }
        catch { /* best effort */ }
    }

    private sealed class GraphHttpClientFactory(FakeGraph graph) : IHttpClientFactory
    {
        // disposeHandler: false — istemciyi kapatan kod ortak işleyiciyi kapatmasın.
        public HttpClient CreateClient(string name) => new(graph, disposeHandler: false);
    }
}

/// <summary>
/// <see cref="FacebookOAuthTestKit.SeedAsync"/>'in tohumladığı Sayfa ve ona bağlı Instagram hesabı
/// (kullanıcı adı "@"sız, Graph'ın döndürdüğü ham hâliyle).
/// </summary>
internal sealed record SeededPage(FacebookLinkedPage Page, string? IgUserId, string? IgUsername);

/// <summary>
/// Lisans sunucusunun OAuth ayağının sahtesi. Sayaçlar "tarayıcısız yol sunucuya gitmedi"
/// iddiasını sınar; her takas yeni bir kullanıcı token'ı üretir ve onu
/// <see cref="LastIssuedToken"/>'da tutar.
/// </summary>
internal sealed class FakeFacebookOAuthBroker : IFacebookOAuthBroker
{
    private readonly FacebookOAuthConfig _config = new(
        AppId: FacebookOAuthTestKit.NewId(),
        LoginConfigId: FacebookOAuthTestKit.NewId(),
        RedirectUri: "https://example.invalid/facebook/callback");

    private int _configCalls;
    private int _exchangeCalls;
    private volatile string? _lastIssuedToken;

    public int ConfigCalls => Volatile.Read(ref _configCalls);

    public int ExchangeCalls => Volatile.Read(ref _exchangeCalls);

    public string? LastIssuedToken => _lastIssuedToken;

    public Task<FacebookOAuthConfig> GetFacebookOAuthConfigAsync(CancellationToken ct = default)
    {
        Interlocked.Increment(ref _configCalls);
        return Task.FromResult(_config);
    }

    public Task<FacebookLongLivedToken> ExchangeFacebookCodeAsync(string code, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _exchangeCalls);
        var token = FacebookOAuthTestKit.NewToken();
        _lastIssuedToken = token;
        return Task.FromResult(new FacebookLongLivedToken(token, ExpiresInSeconds: 5_184_000));
    }
}

/// <summary>
/// Tarayıcının ve geri çağrı dinleyicisinin sahtesi: tarayıcı açılmaz, port dinlenmez. Servis
/// <see cref="Open"/> ile bir bekleme alır; test onu <c>Complete*</c> ile "Facebook geri döndü"
/// diye tamamlar ya da hiç tamamlamaz (zaman aşımı ve iptal kurguları). Eşzamanlı güvenlidir.
/// </summary>
internal sealed class FakeFacebookOAuthBrowser : IFacebookOAuthBrowser
{
    private readonly object _gate = new();
    private readonly List<string> _openedUrls = new();

    // Servisin henüz bırakmadığı (Dispose etmediği) beklemeler, açılış sırasıyla.
    private readonly List<Wait> _held = new();

    /// <summary>Açılan onay adresleri, <see cref="Open"/> sırasıyla (anlık kopya).</summary>
    public IReadOnlyList<string> OpenedUrls
    {
        get { lock (_gate) return _openedUrls.ToArray(); }
    }

    /// <summary>
    /// Servisin hâlâ tuttuğu bekleme sayısı: <see cref="Open"/> ile artar, beklemenin
    /// <c>Dispose</c>'u ile azalır. Gerçek tarayıcıda her biri açık bir dinleyicidir: sıfıra
    /// inmezse sonraki deneme aynı adrese bağlanamaz.
    /// </summary>
    public int PendingWaits
    {
        get { lock (_gate) return _held.Count; }
    }

    public IFacebookOAuthCallbackWait Open(string authorizeUrl)
    {
        var wait = new Wait(this, authorizeUrl);
        lock (_gate)
        {
            _openedUrls.Add(authorizeUrl);
            _held.Add(wait);
        }
        return wait;
    }

    /// <summary>
    /// AÇIK (tamamlanmamış ve bırakılmamış) bir bekleme oluşana dek yoklar; önceki akışın bitmiş
    /// beklemesi sayılmaz. <see cref="TestPatience.Upper"/> dolarsa <see cref="TimeoutException"/>.
    /// </summary>
    public async Task WaitUntilOpenedAsync()
    {
        var waited = Stopwatch.StartNew();
        while (!HasOpenWait())
        {
            if (waited.Elapsed > TestPatience.Upper)
                throw new TimeoutException(
                    $"servis {TestPatience.Upper.TotalSeconds} sn'de tarayıcı onayı açmadı");
            await Task.Delay(20);
        }
    }

    /// <summary>Onay verildi: üretilmiş bir code ve açılan adresin <c>state</c> değeriyle geri döner.</summary>
    public void Complete() =>
        CompleteOpenWait(url => new FacebookOAuthCallback(NewCode(), StateOf(url), null, null));

    /// <summary>Facebook hatayla geri döndü (ör. onay ekranındaki "İptal": <c>access_denied</c>).</summary>
    public void CompleteWithError(string error, string? description = null) =>
        CompleteOpenWait(_ => new FacebookOAuthCallback(null, null, error, description));

    /// <summary>Üretilmiş bir code ama verilen <paramref name="state"/> ile geri döner (başka denemeden gelen geri çağrı).</summary>
    public void CompleteWithState(string state) =>
        CompleteOpenWait(_ => new FacebookOAuthCallback(NewCode(), state, null, null));

    private bool HasOpenWait()
    {
        lock (_gate) return _held.Exists(wait => !wait.IsCompleted);
    }

    /// <summary>En son açılan açık beklemeyi tamamlar; açık bekleme yoksa <see cref="InvalidOperationException"/>.</summary>
    private void CompleteOpenWait(Func<string, FacebookOAuthCallback> callbackFor)
    {
        lock (_gate)
        {
            var wait = _held.FindLast(candidate => !candidate.IsCompleted)
                ?? throw new InvalidOperationException(
                    "Açık tarayıcı beklemesi yok: önce WaitUntilOpenedAsync ile bekle.");
            wait.Complete(callbackFor(wait.AuthorizeUrl));
        }
    }

    private void Release(Wait wait)
    {
        lock (_gate) _held.Remove(wait);
    }

    private static string NewCode() => $"code-{Guid.NewGuid():N}";

    private static string? StateOf(string authorizeUrl) =>
        HttpUtility.ParseQueryString(new Uri(authorizeUrl).Query)["state"];

    private sealed class Wait(FakeFacebookOAuthBrowser owner, string authorizeUrl) : IFacebookOAuthCallbackWait
    {
        // RunContinuationsAsynchronously: tamamlayan test, servisin devamını kendi iş parçacığında
        // (ve sahtenin kilidi altında) koşturmasın.
        private readonly TaskCompletionSource<FacebookOAuthCallback> _callback =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string AuthorizeUrl { get; } = authorizeUrl;

        public bool IsCompleted => _callback.Task.IsCompleted;

        public Task<FacebookOAuthCallback> WaitAsync(CancellationToken ct) => _callback.Task.WaitAsync(ct);

        public void Complete(FacebookOAuthCallback callback) => _callback.SetResult(callback);

        public void Dispose() => owner.Release(this);
    }
}

/// <summary>
/// Graph API'nin sahtesi; eşzamanlı güvenlidir (aynı anda birden çok poller ve servis isteği
/// gelebilir). Yanıt sırası: <see cref="Map"/> kuralları (son eklenen önce) → tanıtılmış
/// Sayfaların varsayılan yanıtları → boş gövdeli 404.
///
/// NEDEN ortak <c>FakeHttpMessageHandler</c> değil: onun istek listesi kilitsizdir, eşzamanlı
/// isteklerde bozulur; ayrıca burada her istek için betik yazmak yerine "şu Sayfalar var"
/// demek yeter.
/// </summary>
internal sealed class FakeGraph : HttpMessageHandler
{
    private const string PermissionsBody =
        """{"data":[{"permission":"instagram_basic","status":"granted"},{"permission":"instagram_manage_comments","status":"granted"}]}""";

    private readonly object _gate = new();
    private readonly List<Uri> _requests = new();
    private readonly List<Rule> _rules = new();
    private readonly List<PageEntry> _pages = new();

    private sealed record Rule(Func<Uri, bool> Match, Func<Uri, HttpResponseMessage> Respond);

    private sealed record PageEntry(string PageId, string PageName, string? IgUserId, string? IgUsername);

    /// <summary>
    /// Bir Sayfayı tanıtır; <paramref name="igUserId"/> verilmezse Sayfa Instagram'sızdır. Aynı
    /// kimlikle yeniden çağrılırsa sırayı koruyarak öncekinin yerini alır.
    /// </summary>
    public void AddPage(string pageId, string pageName, string? igUserId = null, string? igUsername = null)
    {
        var entry = new PageEntry(pageId, pageName, igUserId, igUsername);
        lock (_gate)
        {
            var index = _pages.FindIndex(p => p.PageId == pageId);
            if (index >= 0) _pages[index] = entry;
            else _pages.Add(entry);
        }
    }

    /// <summary>
    /// Eşleşen isteğin yanıtını betikler; son eklenen kural önce denenir. <paramref name="respond"/>
    /// fırlatırsa istisna isteği yapana çıkar (ağ hatası kurgusu), bekletirse yalnız o istek bekler.
    /// </summary>
    public void Map(Func<Uri, bool> match, Func<Uri, HttpResponseMessage> respond)
    {
        lock (_gate) _rules.Add(new Rule(match, respond));
    }

    /// <summary>Şimdiye dek gelen isteklerin adresleri, geliş sırasıyla (anlık kopya).</summary>
    public IReadOnlyList<Uri> Requests
    {
        get { lock (_gate) return _requests.ToArray(); }
    }

    public static HttpResponseMessage Json(
        string body, HttpStatusCode status = HttpStatusCode.OK, string? usageHeader = null)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        if (usageHeader is not null)
            response.Headers.TryAddWithoutValidation("X-Business-Use-Case-Usage", usageHeader);
        return response;
    }

    /// <summary>Graph'ın hata gövdesi; alt kod verilmezse <c>error_subcode</c> alanı yazılmaz.</summary>
    public static HttpResponseMessage Error(
        int code, HttpStatusCode status = HttpStatusCode.BadRequest, int? subcode = null)
    {
        var error = new JsonObject
        {
            ["message"] = $"(#{code}) sahte Graph hatası",
            ["type"] = "OAuthException",
            ["code"] = code,
        };
        if (subcode is { } value) error["error_subcode"] = value;
        return Json(new JsonObject { ["error"] = error }.ToJsonString(), status);
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        lock (_gate) _requests.Add(uri);

        // Yanıt havuz iş parçacığında üretilir: her istek gerçek bir bekleme noktası olur ve
        // bekleten bir kural çağıranın iş parçacığını tutmaz. WaitAsync: gerçek işleyici gibi,
        // istek iptal edilince bekleyen yanıt beklenmeden iptal yükselir.
        return Task.Run(() => Respond(uri), cancellationToken).WaitAsync(cancellationToken);
    }

    private HttpResponseMessage Respond(Uri uri)
    {
        Rule[] rules;
        PageEntry[] pages;
        lock (_gate)
        {
            rules = _rules.ToArray();
            pages = _pages.ToArray();
        }

        // Kurallar kilit DIŞINDA çalışır: bekleten bir kural öteki istekleri kilitlemesin.
        for (var i = rules.Length - 1; i >= 0; i--)
        {
            if (rules[i].Match(uri)) return rules[i].Respond(uri);
        }

        return DefaultFor(uri, pages) ?? new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    /// <summary>Tanıtılmış Sayfalardan türeyen yanıt; hiç Sayfa yoksa ya da uç tanınmıyorsa null.</summary>
    private static HttpResponseMessage? DefaultFor(Uri uri, PageEntry[] pages)
    {
        if (pages.Length == 0) return null;

        var path = uri.AbsolutePath;
        if (path.EndsWith("/me/accounts", StringComparison.Ordinal))
        {
            var data = new JsonArray(pages.Select(AccountRow).ToArray<JsonNode?>());
            return Json(new JsonObject { ["data"] = data }.ToJsonString());
        }

        if (path.EndsWith("/me/permissions", StringComparison.Ordinal))
            return Json(PermissionsBody);

        var page = Array.Find(pages, p => path.EndsWith("/" + p.PageId, StringComparison.Ordinal));
        if (page is null) return null;

        var fields = QueryValue(uri, "fields") ?? string.Empty;
        if (fields.Split(',').Contains("access_token"))
        {
            // Her çağrıda yeni token: "yeniden türetildi" iddiası değer farkıyla sınanabilsin.
            return Json(new JsonObject { ["access_token"] = FacebookOAuthTestKit.NewToken() }.ToJsonString());
        }

        if (fields.Contains("instagram_business_account", StringComparison.Ordinal))
        {
            var body = new JsonObject();
            if (InstagramNode(page) is { } account) body["instagram_business_account"] = account;
            body["id"] = page.PageId;
            return Json(body.ToJsonString());
        }

        return null;
    }

    private static JsonNode AccountRow(PageEntry page)
    {
        var row = new JsonObject { ["id"] = page.PageId, ["name"] = page.PageName };
        if (InstagramNode(page) is { } account) row["instagram_business_account"] = account;
        return row;
    }

    /// <summary>Instagram'sız Sayfada null; kullanıcı adı bilinmiyorsa <c>username</c> alanı yazılmaz.</summary>
    private static JsonObject? InstagramNode(PageEntry page)
    {
        if (page.IgUserId is null) return null;
        var account = new JsonObject { ["id"] = page.IgUserId };
        if (page.IgUsername is not null) account["username"] = page.IgUsername;
        return account;
    }

    /// <summary>Sorgu parametresinin çözülmüş (unescape edilmiş) değeri; parametre yoksa null.</summary>
    private static string? QueryValue(Uri uri, string key)
    {
        foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf('=');
            var name = separator < 0 ? part : part[..separator];
            if (string.Equals(name, key, StringComparison.Ordinal))
                return separator < 0 ? string.Empty : Uri.UnescapeDataString(part[(separator + 1)..]);
        }
        return null;
    }
}
