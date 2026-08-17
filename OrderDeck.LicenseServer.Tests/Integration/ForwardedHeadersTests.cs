using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;

namespace OrderDeck.LicenseServer.Tests.Integration;

/// <summary>
/// <see cref="Program.CreateForwardedHeadersOptions"/> için davranış testleri.
///
/// <para>Bu ayar üretimde görünmez bir güvenlik kontrolü: yanlışsa hiçbir şey
/// patlamaz, yalnızca rate limit politikaları tek kovaya çöker ve denetim
/// kayıtlarındaki IP anlamsızlaşır. Bir paket yükseltmesi varsayılanları
/// değiştirdiğinde ya da biri "kullanılmıyor" diye listeyi sadeleştirdiğinde
/// haber verecek tek şey burası.</para>
///
/// <para>Gerçek middleware koşuluyor (yalnız seçenek nesnesi doğrulanmıyor),
/// çünkü kırılgan olan kısım seçeneklerin ASP.NET tarafından nasıl
/// yorumlandığı.</para>
/// </summary>
public class ForwardedHeadersTests
{
    /// <summary>
    /// İsteği verilen uzak adresten geliyormuş gibi koşturup middleware'in
    /// gördüğü nihai istemci IP'si ile şemayı döndürür.
    /// </summary>
    private static async Task<(string? Ip, string Scheme)> RunAsync(
        IPAddress remoteIp,
        params (string Name, string Value)[] headers)
    {
        using var host = await new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.Configure(app =>
                {
                    app.UseForwardedHeaders(Program.CreateForwardedHeadersOptions());
                    app.Run(ctx =>
                    {
                        ctx.Response.Headers["X-Seen-Ip"] =
                            ctx.Connection.RemoteIpAddress?.ToString() ?? "";
                        ctx.Response.Headers["X-Seen-Scheme"] = ctx.Request.Scheme;
                        return Task.CompletedTask;
                    });
                });
            })
            .StartAsync();

        var context = await host.GetTestServer().SendAsync(ctx =>
        {
            ctx.Connection.RemoteIpAddress = remoteIp;
            ctx.Request.Scheme = "http";
            foreach (var (name, value) in headers)
                ctx.Request.Headers[name] = value;
        });

        var seenIp = context.Response.Headers["X-Seen-Ip"].ToString();
        return (string.IsNullOrEmpty(seenIp) ? null : seenIp,
                context.Response.Headers["X-Seen-Scheme"].ToString());
    }

    [Fact]
    public async Task Caddy_arkasindan_gelen_istekte_gercek_istemci_IPsi_gorunur()
    {
        var (ip, _) = await RunAsync(
            IPAddress.Parse("172.18.0.3"),          // Caddy konteyneri
            ("X-Forwarded-For", "203.0.113.42"));

        Assert.Equal("203.0.113.42", ip);
    }

    [Fact]
    public async Task Istemcinin_uydurdugu_XForwardedFor_okunmaz()
    {
        // Caddy istemcinin gönderdiği başlığın SAĞINA ekler. ForwardLimit=1
        // en sağdakini okuduğu için saldırgan kendi uydurduğu adresi rate
        // limit kovası yapamaz — aksi hâlde her istekte yeni bir IP uydurup
        // limitleri tamamen atlardı.
        var (ip, _) = await RunAsync(
            IPAddress.Parse("172.18.0.3"),
            ("X-Forwarded-For", "10.9.9.9, 203.0.113.42"));

        Assert.Equal("203.0.113.42", ip);
    }

    [Fact]
    public async Task Guvenilmeyen_agdan_gelen_XForwardedFor_yok_sayilir()
    {
        // Docker köprüsü dışından (ör. ileride 8080 yanlışlıkla yayınlanırsa)
        // gelen bir istek başlığıyla kimliğini gizleyememeli.
        var (ip, _) = await RunAsync(
            IPAddress.Parse("198.51.100.7"),
            ("X-Forwarded-For", "203.0.113.42"));

        Assert.Equal("198.51.100.7", ip);
    }

    [Fact]
    public async Task XForwardedProto_semayi_httpsye_cevirir()
    {
        // Admin çerezi CookieSecurePolicy.SameAsRequest kullanıyor; şema http
        // kalırsa çerez Secure bayrağı olmadan yazılır.
        var (_, scheme) = await RunAsync(
            IPAddress.Parse("172.18.0.3"),
            ("X-Forwarded-Proto", "https"));

        Assert.Equal("https", scheme);
    }
}
