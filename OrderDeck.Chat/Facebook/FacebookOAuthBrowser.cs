using System;
using System.Diagnostics;
using System.Net;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace OrderDeck.Chat.Facebook;

/// <summary>
/// Facebook'un onay sonunda tarayıcıyı yönlendirdiği geri çağrının sorgu alanları,
/// yorumlanmadan. Hata, eksik code ve state denetimi çağıranındır
/// (<see cref="FacebookOAuthService"/>).
/// </summary>
internal readonly record struct FacebookOAuthCallback(
    string? Code, string? State, string? Error, string? ErrorDescription);

/// <summary>
/// Tarayıcı onayının dikişi: geri çağrı dinleyicisini kurar ve onay adresini
/// tarayıcıda açar. NEDEN dikiş: testte gerçek tarayıcı açılamaz ve sabit port
/// dinlenemez; servisin zaman aşımı, iptal ve tek akış kuralları bu arayüzün
/// sahtesiyle sınanır.
/// </summary>
internal interface IFacebookOAuthBrowser
{
    /// <summary>
    /// ÖNCE dinleyiciyi başlatır, SONRA tarayıcıyı açar (yönlendirme dinleyiciden
    /// önce gelip kaybolmasın). Dönen bekleme dinleyiciyi tutar; çağıran onu
    /// <c>Dispose</c> ile bırakmadıkça aynı adrese ikinci bir dinleyici bağlanamaz.
    /// </summary>
    IFacebookOAuthCallbackWait Open(string authorizeUrl);
}

/// <summary>Açık bir geri çağrı beklemesi. <c>Dispose</c> dinleyiciyi bırakır.</summary>
internal interface IFacebookOAuthCallbackWait : IDisposable
{
    /// <summary>
    /// Geri çağrı gelene dek bekler ve alanlarını döner. <paramref name="ct"/>
    /// iptal edilirse (çağrıdan önce edilmiş olsa da) beklemeyi keser ve
    /// <see cref="OperationCanceledException"/> fırlatır.
    /// </summary>
    Task<FacebookOAuthCallback> WaitAsync(CancellationToken ct);
}

/// <summary>
/// Üretimdeki tarayıcı dikişi: geri çağrıyı yerel bir <see cref="HttpListener"/>
/// ile dinler, onay adresini varsayılan tarayıcıda açar.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class LoopbackFacebookOAuthBrowser : IFacebookOAuthBrowser
{
    private readonly string _prefix;
    private readonly Action<string> _launchBrowser;

    internal LoopbackFacebookOAuthBrowser()
        : this(FacebookOAuthService.LoopbackPrefix, LaunchDefaultBrowser)
    {
    }

    /// <summary>Test girişi: dinlenecek önek ve tarayıcıyı açan eylem dışarıdan verilir.</summary>
    internal LoopbackFacebookOAuthBrowser(string prefix, Action<string> launchBrowser)
    {
        _prefix = prefix;
        _launchBrowser = launchBrowser;
    }

    public IFacebookOAuthCallbackWait Open(string authorizeUrl)
    {
        // Spin up the loopback listener BEFORE opening the browser so we
        // don't lose the redirect if Facebook is uncharacteristically fast.
        var listener = new HttpListener();
        listener.Prefixes.Add(_prefix);
        try
        {
            listener.Start();
        }
        catch (HttpListenerException ex)
        {
            listener.Close();
            throw new InvalidOperationException(
                $"Failed to bind {_prefix} — another process is using " +
                "the port, or you need to run as administrator once to grant " +
                "URL ACL.", ex);
        }

        try
        {
            _launchBrowser(authorizeUrl);
        }
        catch
        {
            // Tarayıcı açılamadıysa beklenecek bir onay yok. Dinleyici açık
            // kalırsa sonraki deneme aynı adrese bağlanamaz.
            listener.Close();
            throw;
        }

        return new CallbackWait(listener);
    }

    private static void LaunchDefaultBrowser(string url)
    {
        // Process.Start with UseShellExecute=true lets Windows resolve the
        // operator's default browser — same trick as the YouTube broker.
        Process.Start(new ProcessStartInfo
        {
            FileName = url,
            UseShellExecute = true,
        });
    }

    private sealed class CallbackWait : IFacebookOAuthCallbackWait
    {
        private readonly HttpListener _listener;

        public CallbackWait(HttpListener listener) => _listener = listener;

        public async Task<FacebookOAuthCallback> WaitAsync(CancellationToken ct)
        {
            // Belirteç çağrıdan ÖNCE iptal edilmişse dinleyiciye hiç dokunma. NEDEN:
            // tarayıcının açılmasıyla beklemenin başlaması arasına düşen iptal de
            // (Ayarlar'daki "İptal") iptal olarak çıkmalı, hata olarak değil.
            ct.ThrowIfCancellationRequested();

            // Bekleme iptal kaydından ÖNCE başlar. NEDEN: kayıt, belirteç o arada
            // iptal edilmişse dinleyiciyi hemen durdurur; durmuş dinleyicide
            // GetContextAsync InvalidOperationException fırlatır ve aşağıdaki iki
            // süzgeç onu yakalamaz. Önce başlatılan bekleme ise durdurulunca
            // HttpListenerException ile düşer ve iptale çevrilir.
            var pending = _listener.GetContextAsync();

            // Hook ct so closing the settings dialog actually aborts the listener.
            using var reg = ct.Register(() =>
            {
                try { _listener.Stop(); } catch { /* already disposed */ }
            });

            HttpListenerContext ctx;
            try
            {
                ctx = await pending.ConfigureAwait(false);
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

            // Hata ve eksik code burada fırlatılmaz: geri çağrı olduğu gibi döner,
            // denetimi servis yapar.
            return new FacebookOAuthCallback(code, state, error, errorDesc);
        }

        public void Dispose() => _listener.Close();
    }
}
